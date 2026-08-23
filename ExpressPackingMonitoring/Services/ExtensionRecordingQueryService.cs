using System.Collections.Concurrent;
using System.IO;
using ExpressPackingMonitoring.Data;

namespace ExpressPackingMonitoring.Services;

internal sealed class ExtensionRecordingQueryService : IDisposable
{
    private const int MaxResults = 20;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private readonly VideoDatabase _database;
    private readonly string _cacheDirectory;
    private readonly long _maxCacheBytes;
    private readonly ConcurrentDictionary<string, QueryState> _queries = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _archivePrepareSlot = new(1, 1);
    private readonly Timer _cleanupTimer;

    internal ExtensionRecordingQueryService(VideoDatabase database, string cacheRoot, long maxCacheBytes = 1024L * 1024 * 1024)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _cacheDirectory = Path.Combine(cacheRoot, "extension-recording-queries");
        _maxCacheBytes = Math.Max(64L * 1024 * 1024, maxCacheBytes);
        Directory.CreateDirectory(_cacheDirectory);
        _cleanupTimer = new Timer(_ => CleanupExpired(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    internal ExtensionRecordingQuerySnapshot Create(string ownerId, string trackingNumber)
    {
        string normalized = NormalizeTrackingNumber(trackingNumber);
        var state = new QueryState
        {
            QueryId = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            TrackingNumber = normalized,
            Status = "queued",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.Add(Lifetime)
        };
        _queries[state.QueryId] = state;
        _ = Task.Run(() => SearchAndPrepareAsync(state, _cts.Token));
        return Snapshot(state);
    }

    internal bool TryGet(string queryId, string ownerId, out ExtensionRecordingQuerySnapshot? snapshot)
    {
        snapshot = null;
        if (!_queries.TryGetValue(queryId, out QueryState? state)
            || !string.Equals(state.OwnerId, ownerId, StringComparison.Ordinal))
            return false;
        lock (state.Gate)
        {
            if (state.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                state.Status = "expired";
            snapshot = Snapshot(state);
            return true;
        }
    }

    internal bool TryBeginDownload(string queryId, long recordingId, string ownerId, out string filePath)
    {
        filePath = "";
        if (!_queries.TryGetValue(queryId, out QueryState? state)
            || !string.Equals(state.OwnerId, ownerId, StringComparison.Ordinal))
            return false;
        lock (state.Gate)
        {
            if (state.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                state.Status = "expired";
                return false;
            }
            QueryRecording? recording = state.Recordings.FirstOrDefault(value => value.RecordingId == recordingId);
            if (recording == null || recording.Status is not ("ready" or "completed")
                || !File.Exists(recording.PreparedPath))
                return false;
            recording.Status = "downloading";
            filePath = recording.PreparedPath;
            UpdateAggregateStatus(state);
            return true;
        }
    }

    internal void FinishDownload(string queryId, long recordingId, bool completed)
    {
        if (!_queries.TryGetValue(queryId, out QueryState? state)) return;
        lock (state.Gate)
        {
            QueryRecording? recording = state.Recordings.FirstOrDefault(value => value.RecordingId == recordingId);
            if (recording == null || recording.Status != "downloading") return;
            recording.Status = completed ? "completed" : "ready";
            UpdateAggregateStatus(state);
        }
    }

    internal static string NormalizeTrackingNumber(string value)
    {
        string normalized = value?.Trim() ?? "";
        if (normalized.Length is < 3 or > 128
            || normalized.Any(ch => !((ch is >= 'A' and <= 'Z')
                || (ch is >= 'a' and <= 'z')
                || (ch is >= '0' and <= '9')
                || ch is '-' or '_')))
            throw new InvalidDataException("快递单号格式无效");
        return normalized;
    }

    private async Task SearchAndPrepareAsync(QueryState state, CancellationToken cancellationToken)
    {
        try
        {
            lock (state.Gate) state.Status = "searching";
            PagedVideoResult result = _database.QueryVideosPaged(
                null, null, state.TrackingNumber, 1, MaxResults, includeDeleted: false,
                searchMode: VideoSearchMode.ExactOrderIdentifiers);
            lock (state.Gate) state.TotalMatches = result.Total;
            if (result.Records.Count == 0)
            {
                lock (state.Gate) state.Status = "not_found";
                return;
            }

            foreach (VideoRecord record in result.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string resolvedPath = PlaybackFileResolver.ResolvePlaybackPath(record);
                var item = new QueryRecording
                {
                    RecordingId = record.Id,
                    RecordedAt = record.StartTime,
                    DurationSeconds = record.DurationSeconds,
                    FileSizeBytes = record.FileSizeBytes,
                    VideoCodec = record.VideoCodec,
                    FileName = Path.GetFileName(resolvedPath.Length > 0 ? resolvedPath : record.FilePath)
                };
                lock (state.Gate) state.Recordings.Add(item);
                if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                {
                    lock (state.Gate)
                    {
                        item.Status = "failed";
                        item.ErrorCode = "recording_unavailable";
                    }
                    continue;
                }

                bool isLocal = !string.IsNullOrWhiteSpace(record.FilePath) && File.Exists(record.FilePath)
                    && PathsEqual(resolvedPath, record.FilePath);
                if (isLocal)
                {
                    lock (state.Gate)
                    {
                        item.PreparedPath = resolvedPath;
                        item.FileSizeBytes = new FileInfo(resolvedPath).Length;
                        item.Progress = 100;
                        item.Status = "ready";
                    }
                    continue;
                }

                lock (state.Gate)
                {
                    item.Status = "preparing";
                    UpdateAggregateStatus(state);
                }
                string queryDirectory = Path.Combine(_cacheDirectory, state.QueryId);
                Directory.CreateDirectory(queryDirectory);
                string extension = Path.GetExtension(resolvedPath);
                string target = Path.Combine(queryDirectory, $"{record.Id}{extension}");
                string partialTarget = target + ".partial";
                try
                {
                    await _archivePrepareSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (!HasCacheCapacity(new FileInfo(resolvedPath).Length))
                        {
                            lock (state.Gate)
                            {
                                item.Status = "failed";
                                item.ErrorCode = "archive_cache_limit_exceeded";
                            }
                            continue;
                        }
                        TryDelete(partialTarget);
                        await CopyWithProgressAsync(resolvedPath, partialTarget, state, item, cancellationToken).ConfigureAwait(false);
                        File.Move(partialTarget, target, overwrite: true);
                    }
                    finally { _archivePrepareSlot.Release(); }
                    lock (state.Gate)
                    {
                        item.PreparedPath = target;
                        item.FileName = Path.GetFileName(resolvedPath);
                        item.Status = "ready";
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    TryDelete(partialTarget);
                    TryDelete(target);
                    lock (state.Gate)
                    {
                        item.Status = "failed";
                        item.ErrorCode = "archive_prepare_failed";
                    }
                }
                catch (OperationCanceledException)
                {
                    TryDelete(partialTarget);
                    throw;
                }
            }
            lock (state.Gate) UpdateAggregateStatus(state);
        }
        catch (OperationCanceledException)
        {
            lock (state.Gate) state.Status = "failed";
        }
        catch
        {
            lock (state.Gate) state.Status = "failed";
        }
    }

    private static async Task CopyWithProgressAsync(
        string source, string target, QueryState state, QueryRecording item, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        lock (state.Gate) item.FileSizeBytes = input.Length;
        byte[] buffer = new byte[1024 * 1024];
        long copied = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            lock (state.Gate)
                item.Progress = input.Length == 0 ? 100 : (int)Math.Min(99, copied * 100 / input.Length);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        lock (state.Gate) item.Progress = 100;
    }

    private static void UpdateAggregateStatus(QueryState state)
    {
        if (state.Recordings.Any(value => value.Status == "downloading")) state.Status = "downloading";
        else if (state.Recordings.Any(value => value.Status == "preparing")) state.Status = "preparing";
        else if (state.Recordings.Any(value => value.Status == "ready")) state.Status = "ready";
        else if (state.Recordings.Count > 0 && state.Recordings.All(value => value.Status == "completed")) state.Status = "completed";
        else if (state.Recordings.Count > 0 && state.Recordings.All(value => value.Status == "failed")) state.Status = "failed";
    }

    private static ExtensionRecordingQuerySnapshot Snapshot(QueryState state) => new(
        state.QueryId, state.TrackingNumber, state.Status, GetMessage(state.Status),
        state.TotalMatches, state.TotalMatches > MaxResults,
        state.Recordings.Count == 0 ? null : (int?)state.Recordings.Average(value => value.Progress),
        state.Recordings.Select(value => new ExtensionRecordingSnapshot(
            value.RecordingId, value.Status, value.Progress, value.RecordedAt, value.DurationSeconds,
            value.FileSizeBytes, value.VideoCodec, value.FileName,
            state.Status != "expired" && (value.Status is "ready" or "completed")
                ? $"/api/extensions/v1/recording-queries/{state.QueryId}/recordings/{value.RecordingId}/download"
                : null,
            value.ErrorCode)).ToArray(),
        state.CreatedAtUtc, state.ExpiresAtUtc);

    private static string GetMessage(string status) => status switch
    {
        "queued" => "已接收查询请求",
        "searching" => "正在查询录像",
        "preparing" => "正在从归档存储准备录像",
        "ready" => "录像可以下载",
        "downloading" => "机器人正在下载录像",
        "completed" => "录像下载完成",
        "not_found" => "没有找到对应录像",
        "expired" => "查询任务已过期",
        _ => "录像查询或准备失败"
    };

    private void CleanupExpired()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach ((string id, QueryState state) in _queries)
        {
            lock (state.Gate)
            {
                if (state.ExpiresAtUtc > now || state.Status is "preparing" or "downloading") continue;
                if (now < state.ExpiresAtUtc.AddMinutes(10))
                {
                    state.Status = "expired";
                    TryDeleteDirectory(Path.Combine(_cacheDirectory, id));
                    continue;
                }
            }
            if (!_queries.TryRemove(id, out _)) continue;
            TryDeleteDirectory(Path.Combine(_cacheDirectory, id));
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private bool HasCacheCapacity(long incomingBytes)
    {
        long currentBytes = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(_cacheDirectory, "*", SearchOption.AllDirectories))
            {
                currentBytes += new FileInfo(file).Length;
                if (currentBytes > _maxCacheBytes - incomingBytes) return false;
            }
            return incomingBytes <= _maxCacheBytes - currentBytes;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }

    public void Dispose()
    {
        _cts.Cancel();
        _cleanupTimer.Dispose();
        _cts.Dispose();
    }

    private sealed class QueryState
    {
        internal object Gate { get; } = new();
        internal string QueryId { get; init; } = "";
        internal string OwnerId { get; init; } = "";
        internal string TrackingNumber { get; init; } = "";
        internal string Status { get; set; } = "queued";
        internal int TotalMatches { get; set; }
        internal List<QueryRecording> Recordings { get; } = [];
        internal DateTimeOffset CreatedAtUtc { get; init; }
        internal DateTimeOffset ExpiresAtUtc { get; init; }
    }

    private sealed class QueryRecording
    {
        internal long RecordingId { get; init; }
        internal string Status { get; set; } = "queued";
        internal int Progress { get; set; }
        internal DateTime RecordedAt { get; init; }
        internal double DurationSeconds { get; init; }
        internal long FileSizeBytes { get; set; }
        internal string VideoCodec { get; init; } = "";
        internal string FileName { get; set; } = "";
        internal string PreparedPath { get; set; } = "";
        internal string ErrorCode { get; set; } = "";
    }
}

internal sealed record ExtensionRecordingQuerySnapshot(
    string QueryId, string TrackingNumber, string Status, string Message,
    int TotalMatches, bool Truncated, int? Progress,
    IReadOnlyList<ExtensionRecordingSnapshot> Recordings,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

internal sealed record ExtensionRecordingSnapshot(
    long RecordingId, string Status, int Progress, DateTime RecordedAt, double DurationSeconds,
    long FileSizeBytes, string VideoCodec, string FileName, string? DownloadUrl, string ErrorCode);
