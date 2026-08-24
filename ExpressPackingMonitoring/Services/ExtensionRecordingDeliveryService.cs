using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using ExpressPackingMonitoring.Config;

namespace ExpressPackingMonitoring.Services;

internal static class ExtensionRecordingDeliveryProfiles
{
    internal const string SourceCodecTargetSize = "source_codec_target_size";
    internal const string H265TargetSize = "h265_target_size";

    internal static bool IsSupported(string profile) => profile is SourceCodecTargetSize or H265TargetSize;
}

internal sealed record ExtensionRecordingDeliveryBitrate(int VideoBitsPerSecond, int AudioBitsPerSecond)
{
    private const int MinimumVideoBitsPerSecond = 32_000;

    internal static bool TryCalculate(double durationSeconds, long targetBytes, out ExtensionRecordingDeliveryBitrate? bitrate)
    {
        bitrate = null;
        if (durationSeconds <= 0 || double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds) || targetBytes <= 0)
            return false;

        // 为 MP4 封装、时长偏差和编码器 ABR 波动保留 4% 安全余量。
        double totalBitsPerSecond = Math.Floor(targetBytes * 8d / durationSeconds * 0.96d);
        if (totalBitsPerSecond > int.MaxValue) return false;
        int total = (int)totalBitsPerSecond;
        int audio = Math.Clamp(total / 10, 32_000, 128_000);
        int video = total - audio;
        if (video < MinimumVideoBitsPerSecond) return false;
        bitrate = new ExtensionRecordingDeliveryBitrate(video, audio);
        return true;
    }
}

internal sealed class ExtensionRecordingDeliveryService : IDisposable
{
    private const int MinimumSizeMb = 1;
    private const int MaximumSizeMb = 200;
    private readonly ExtensionRecordingQueryService _queries;
    private readonly string _transcodeCacheDirectory;
    private readonly string _directory;
    private readonly long _maxCacheBytes;
    private readonly FfmpegWorkLimiter _ffmpegWorkLimiter;
    private readonly Action _requestCacheCleanup;
    private readonly ConcurrentDictionary<string, DeliveryState> _deliveries = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _cleanupTimer;

    internal ExtensionRecordingDeliveryService(
        ExtensionRecordingQueryService queries,
        string transcodeCacheDirectory,
        long maxCacheBytes,
        FfmpegWorkLimiter ffmpegWorkLimiter,
        Action requestCacheCleanup)
    {
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _transcodeCacheDirectory = transcodeCacheDirectory ?? throw new ArgumentNullException(nameof(transcodeCacheDirectory));
        _directory = Path.Combine(_transcodeCacheDirectory, "extension-recording-deliveries");
        _maxCacheBytes = Math.Max(64L * 1024 * 1024, maxCacheBytes);
        _ffmpegWorkLimiter = ffmpegWorkLimiter ?? throw new ArgumentNullException(nameof(ffmpegWorkLimiter));
        _requestCacheCleanup = requestCacheCleanup ?? throw new ArgumentNullException(nameof(requestCacheCleanup));
        Directory.CreateDirectory(_directory);
        _cleanupTimer = new Timer(_ => CleanupExpired(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    internal ExtensionRecordingDeliverySnapshot Create(
        string ownerId,
        string queryId,
        long recordingId,
        string profile,
        int maxFileSizeMb)
    {
        profile = profile?.Trim().ToLowerInvariant() ?? "";
        if (!ExtensionRecordingDeliveryProfiles.IsSupported(profile))
            throw new InvalidDataException("交付预设无效");
        if (maxFileSizeMb is < MinimumSizeMb or > MaximumSizeMb)
            throw new InvalidDataException($"目标大小必须在 {MinimumSizeMb} 到 {MaximumSizeMb} MB 之间");
        if (!_queries.TryGetPreparedRecording(queryId, recordingId, ownerId, out ExtensionPreparedRecording? source))
            throw new FileNotFoundException("录像不存在、尚未准备完成或已过期");
        ExtensionPreparedRecording preparedSource = source!;

        var state = new DeliveryState
        {
            DeliveryId = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            QueryId = queryId,
            RecordingId = recordingId,
            Profile = profile,
            MaxFileSizeBytes = maxFileSizeMb * 1024L * 1024L,
            Source = preparedSource,
            FileName = BuildDeliveryFileName(preparedSource.FileName),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = preparedSource.ExpiresAt
        };
        _deliveries[state.DeliveryId] = state;
        _ = Task.Run(() => PrepareAsync(state, _cts.Token));
        return Snapshot(state);
    }

    internal bool TryGet(
        string ownerId,
        string queryId,
        long recordingId,
        string deliveryId,
        out ExtensionRecordingDeliverySnapshot? snapshot)
    {
        snapshot = null;
        if (!TryGetOwnedState(ownerId, queryId, recordingId, deliveryId, out DeliveryState? state) || state == null) return false;
        lock (state.Gate)
        {
            if (state.ExpiresAtUtc <= DateTimeOffset.UtcNow) state.Status = "expired";
            snapshot = Snapshot(state);
            return true;
        }
    }

    internal bool TryBeginDownload(
        string ownerId,
        string queryId,
        long recordingId,
        string deliveryId,
        out string filePath,
        out string fileName)
    {
        filePath = "";
        fileName = "";
        if (!TryGetOwnedState(ownerId, queryId, recordingId, deliveryId, out DeliveryState? state) || state == null) return false;
        lock (state.Gate)
        {
            if (state.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                state.Status = "expired";
                return false;
            }
            if (state.Status is not ("ready" or "completed") || !File.Exists(state.OutputPath)) return false;
            state.Status = "downloading";
            filePath = state.OutputPath;
            fileName = state.FileName;
            return true;
        }
    }

    internal void FinishDownload(string deliveryId, bool completed)
    {
        if (!_deliveries.TryGetValue(deliveryId, out DeliveryState? state)) return;
        lock (state.Gate)
        {
            if (state.Status == "downloading") state.Status = completed ? "completed" : "ready";
        }
    }

    internal void CleanupExpired()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach ((string id, DeliveryState state) in _deliveries)
        {
            bool remove;
            lock (state.Gate)
            {
                if (state.ExpiresAtUtc > now || state.Status is "transcoding" or "downloading") continue;
                state.Status = "expired";
                remove = now >= state.ExpiresAtUtc.AddMinutes(10);
            }
            TryDeleteDirectory(Path.GetDirectoryName(state.OutputPath) ?? "");
            if (remove) _deliveries.TryRemove(id, out _);
        }
    }

    private bool TryGetOwnedState(string ownerId, string queryId, long recordingId, string deliveryId, out DeliveryState? state)
    {
        return _deliveries.TryGetValue(deliveryId, out state)
            && string.Equals(state.OwnerId, ownerId, StringComparison.Ordinal)
            && string.Equals(state.QueryId, queryId, StringComparison.Ordinal)
            && state.RecordingId == recordingId;
    }

    private async Task PrepareAsync(DeliveryState state, CancellationToken cancellationToken)
    {
        string outputDirectory = Path.Combine(_directory, state.QueryId, state.RecordingId.ToString(CultureInfo.InvariantCulture));
        string outputPath = Path.Combine(outputDirectory, state.FileName);
        string partialPath = outputPath + ".partial";
        lock (state.Gate)
        {
            state.Status = "transcoding";
            state.Progress = 5;
            state.OutputPath = outputPath;
        }

        try
        {
            if (!ExtensionRecordingDeliveryBitrate.TryCalculate(state.Source.DurationSeconds, state.MaxFileSizeBytes, out ExtensionRecordingDeliveryBitrate? initialBitrate))
            {
                Fail(state, "delivery_duration_unavailable");
                return;
            }
            ExtensionRecordingDeliveryBitrate bitrate = initialBitrate!;
            if (!HasCacheCapacity(state.MaxFileSizeBytes))
            {
                Fail(state, "delivery_cache_limit_exceeded");
                return;
            }
            string ffmpegPath = AppPaths.FindFFmpeg();
            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                Fail(state, "delivery_ffmpeg_unavailable");
                return;
            }

            string encoder = ResolveEncoder(state.Profile, state.Source.VideoCodec);
            if (encoder.Length == 0)
            {
                Fail(state, "delivery_profile_unsupported");
                return;
            }

            Directory.CreateDirectory(outputDirectory);
            using IDisposable slot = _ffmpegWorkLimiter.Enter(cancellationToken);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryDelete(partialPath);
                if (!RunTwoPass(ffmpegPath, state.Source.PreparedPath, partialPath, outputPath + ".pass", encoder, bitrate))
                {
                    Fail(state, "delivery_transcode_failed");
                    return;
                }
                long size = new FileInfo(partialPath).Length;
                if (size <= state.MaxFileSizeBytes)
                {
                    File.Move(partialPath, outputPath, overwrite: true);
                    lock (state.Gate)
                    {
                        state.FileSizeBytes = size;
                        state.VideoCodec = state.Profile == ExtensionRecordingDeliveryProfiles.H265TargetSize ? "h265" : state.Source.VideoCodec;
                        state.Progress = 100;
                        state.Status = "ready";
                    }
                    return;
                }

                long retryTarget = (long)Math.Floor(state.MaxFileSizeBytes * state.MaxFileSizeBytes / (double)size * 0.96d);
                if (!ExtensionRecordingDeliveryBitrate.TryCalculate(state.Source.DurationSeconds, retryTarget, out ExtensionRecordingDeliveryBitrate? retryBitrate)) break;
                bitrate = retryBitrate!;
                lock (state.Gate) state.Progress = 55;
            }
            TryDelete(partialPath);
            Fail(state, "delivery_size_limit_unreachable");
        }
        catch (OperationCanceledException)
        {
            TryDelete(partialPath);
            Fail(state, "delivery_canceled");
        }
        catch
        {
            TryDelete(partialPath);
            Fail(state, "delivery_transcode_failed");
        }
        finally
        {
            TryDelete(outputPath + ".pass-0.log");
            TryDelete(outputPath + ".pass-0.log.mbtree");
        }
        await Task.CompletedTask;
    }

    private static bool RunTwoPass(
        string ffmpegPath,
        string sourcePath,
        string outputPath,
        string passLogPath,
        string encoder,
        ExtensionRecordingDeliveryBitrate bitrate)
    {
        string common = $"-loglevel warning -y -i \"{sourcePath}\" -map 0:v:0 -map 0:a? -c:v {encoder} -b:v {bitrate.VideoBitsPerSecond} -maxrate {bitrate.VideoBitsPerSecond} -bufsize {bitrate.VideoBitsPerSecond * 2L} -passlogfile \"{passLogPath}\"";
        if (!RunFfmpeg(ffmpegPath, common + " -pass 1 -an -f null NUL")) return false;
        return RunFfmpeg(
            ffmpegPath,
            common + $" -pass 2 -c:a aac -b:a {bitrate.AudioBitsPerSecond} -movflags +faststart -f mp4 \"{outputPath}\"");
    }

    private static bool RunFfmpeg(string ffmpegPath, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using Process? process = Process.Start(startInfo);
        if (process == null) return false;
        _ = process.StandardError.ReadToEnd();
        if (!process.WaitForExit((int)TimeSpan.FromHours(2).TotalMilliseconds))
        {
            try { process.Kill(); } catch { }
            return false;
        }
        return process.ExitCode == 0;
    }

    private bool HasCacheCapacity(long incomingBytes)
    {
        try
        {
            if (HasCacheCapacityAfterCleanup(incomingBytes)) return true;
            _requestCacheCleanup();
            return HasCacheCapacityAfterCleanup(incomingBytes);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private bool HasCacheCapacityAfterCleanup(long incomingBytes)
    {
        long currentBytes = Directory.Exists(_transcodeCacheDirectory)
            ? Directory.EnumerateFiles(_transcodeCacheDirectory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
            : 0;
        return incomingBytes <= _maxCacheBytes - currentBytes;
    }

    private static string ResolveEncoder(string profile, string sourceCodec)
    {
        if (profile == ExtensionRecordingDeliveryProfiles.H265TargetSize) return "libx265";
        return sourceCodec.Trim().ToLowerInvariant() switch
        {
            "h264" => "libx264",
            "h265" => "libx265",
            _ => ""
        };
    }

    private static string BuildDeliveryFileName(string sourceFileName)
    {
        string? baseName = Path.GetFileNameWithoutExtension(sourceFileName)?.Trim();
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "录像";
        return baseName + "_转码.mp4";
    }

    private static ExtensionRecordingDeliverySnapshot Snapshot(DeliveryState state) => new(
        state.DeliveryId, state.QueryId, state.RecordingId, state.Status, state.Progress,
        state.Profile, state.MaxFileSizeBytes, state.FileSizeBytes, state.Source.DurationSeconds,
        state.VideoCodec, state.FileName,
        state.Status is "ready" or "completed"
            ? $"/api/extensions/v1/recording-queries/{state.QueryId}/recordings/{state.RecordingId}/deliveries/{state.DeliveryId}/download"
            : null,
        state.ErrorCode, state.CreatedAtUtc, state.ExpiresAtUtc);

    private static void Fail(DeliveryState state, string errorCode)
    {
        lock (state.Gate)
        {
            state.Status = "failed";
            state.ErrorCode = errorCode;
        }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }

    public void Dispose()
    {
        _cts.Cancel();
        _cleanupTimer.Dispose();
        _cts.Dispose();
    }

    private sealed class DeliveryState
    {
        internal object Gate { get; } = new();
        internal string DeliveryId { get; init; } = "";
        internal string OwnerId { get; init; } = "";
        internal string QueryId { get; init; } = "";
        internal long RecordingId { get; init; }
        internal string Profile { get; init; } = "";
        internal long MaxFileSizeBytes { get; init; }
        internal ExtensionPreparedRecording Source { get; init; } = null!;
        internal string FileName { get; init; } = "";
        internal string OutputPath { get; set; } = "";
        internal string Status { get; set; } = "queued";
        internal int Progress { get; set; }
        internal long FileSizeBytes { get; set; }
        internal string VideoCodec { get; set; } = "";
        internal string ErrorCode { get; set; } = "";
        internal DateTimeOffset CreatedAtUtc { get; init; }
        internal DateTimeOffset ExpiresAtUtc { get; init; }
    }
}

internal sealed record ExtensionRecordingDeliverySnapshot(
    string DeliveryId,
    string QueryId,
    long RecordingId,
    string Status,
    int Progress,
    string Profile,
    long MaxFileSizeBytes,
    long FileSizeBytes,
    double DurationSeconds,
    string VideoCodec,
    string FileName,
    string? DownloadUrl,
    string ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
