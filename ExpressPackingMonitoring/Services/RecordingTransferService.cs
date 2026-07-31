using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

internal sealed record RecordingTransferProgress(
    long TaskId,
    long SentBytes,
    long TotalBytes,
    string State,
    string Error = "");

internal sealed class RecordingTransferService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly RecordingTransferQueueStore _store;
    private readonly VideoDatabase _database;
    private readonly Func<AppConfig> _configProvider;
    private readonly Func<string, CancellationToken, Task<PackingProofNodeInfo?>> _nodeInfoResolver;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private Task? _worker;
    private int _processing;
    private bool _disposed;

    internal event Action<RecordingTransferProgress>? ProgressChanged;

    public RecordingTransferService(
        RecordingTransferQueueStore store,
        VideoDatabase database,
        Func<AppConfig> configProvider,
        HttpClient? httpClient = null,
        Func<string, CancellationToken, Task<PackingProofNodeInfo?>>? nodeInfoResolver = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _nodeInfoResolver = nodeInfoResolver ?? WorkstationNetwork.GetNodeInfoAsync;
        _httpClient = httpClient ?? new HttpClient(WorkstationNetwork.CreateLanHttpMessageHandler())
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        _ownsHttpClient = httpClient == null;
        _store.RecoverInterrupted(DateTime.UtcNow);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _worker ??= Task.Run(() => WorkerLoopAsync(_cts.Token));
        Signal();
    }

    public int EnqueueCompletedRecordings()
    {
        AppConfig config = _configProvider();
        if (!IsRecordingWorkstation(config)
            || string.IsNullOrWhiteSpace(config.LastKnownHostNodeId)
            || string.IsNullOrWhiteSpace(config.LastKnownHostAddress))
        {
            return 0;
        }

        int added = 0;
        foreach (VideoRecord record in _database.GetCompletedPcVideosForTransfer(
                     config.RecordingWorkstationActivatedAtUtc))
        {
            if (!File.Exists(record.FilePath))
                continue;

            string sourceSessionId = $"{config.NodeId}:{record.Id}";
            if (_store.Enqueue(
                    record.Id,
                    record.FilePath,
                    sourceSessionId,
                    config.LastKnownHostNodeId,
                    config.LastKnownHostAddress,
                    DateTime.UtcNow))
            {
                added++;
            }
        }

        if (added > 0)
        {
            RuntimeLog.Info("RecordingTransfer", $"Queued completed recordings count={added}");
            Signal();
        }
        return added;
    }

    public void RetryNow()
    {
        _store.RetryFailedNow(DateTime.UtcNow);
        Signal();
    }

    internal async Task<int> ProcessReadyOnceAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _processing, 1) == 1)
            return 0;

        try
        {
            int processed = 0;
            foreach (RecordingTransferTask task in _store.GetReady(DateTime.UtcNow))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessTaskAsync(task, cancellationToken).ConfigureAwait(false);
                processed++;
            }
            return processed;
        }
        finally
        {
            Interlocked.Exchange(ref _processing, 0);
        }
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                EnqueueCompletedRecordings();
                await ProcessReadyOnceAsync(cancellationToken).ConfigureAwait(false);
                await _wakeSignal.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("RecordingTransfer", "Transfer worker loop failed", ex);
                try { await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ProcessTaskAsync(RecordingTransferTask task, CancellationToken cancellationToken)
    {
        DateTime now = DateTime.UtcNow;
        try
        {
            AppConfig config = _configProvider();
            ValidateTaskTarget(task, config);
            VideoRecord record = _database.GetVideoById(task.LocalVideoRecordId)
                ?? throw new InvalidOperationException("本地录像记录不存在");
            string filePath = ResolveCurrentFilePath(task, record);
            ValidateUploadFile(filePath);

            PackingProofNodeInfo? node = await _nodeInfoResolver(task.TargetAddress, cancellationToken)
                .ConfigureAwait(false);
            if (node == null)
                throw new HttpRequestException("保存主机离线");
            if (!string.Equals(node.NodeId, task.TargetNodeId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("保存主机身份已变化");
            if (!node.Capabilities.Contains(PackingProofCapabilities.MobileBackup, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("目标主机不支持录像接收");

            string sha256 = task.FileSha256;
            if (sha256.Length != 64)
            {
                sha256 = await ComputeFileSha256Async(filePath, cancellationToken).ConfigureAwait(false);
                _store.SetHash(task.Id, sha256, DateTime.UtcNow);
            }

            long totalBytes = new FileInfo(filePath).Length;
            MobileBackupCreateResponse create = await CreateOrResumeAsync(
                task,
                config,
                sha256,
                totalBytes,
                cancellationToken).ConfigureAwait(false);
            if (create.Offset < 0 || create.Offset > totalBytes)
                throw new InvalidDataException("主机返回的断点位置无效");

            _store.MarkUploading(task.Id, create.Offset, DateTime.UtcNow);
            ProgressChanged?.Invoke(new RecordingTransferProgress(
                task.Id, create.Offset, totalBytes, RecordingTransferStates.Uploading));

            if (!create.FileReady && create.Offset < totalBytes)
            {
                await UploadChunksAsync(
                    task,
                    config,
                    filePath,
                    sha256,
                    create.UploadId,
                    create.Offset,
                    create.ChunkSize,
                    totalBytes,
                    cancellationToken).ConfigureAwait(false);
            }

            MobileBackupCompleteResponse completed = await CompleteAsync(
                task,
                config,
                record,
                sha256,
                create.UploadId,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(completed.Status, "verified", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(completed.FileSha256, sha256, StringComparison.OrdinalIgnoreCase)
                || completed.RecordId <= 0)
            {
                throw new InvalidDataException("主机未明确确认录像已经校验并入库");
            }

            _store.MarkUploaded(task.Id, completed.RecordId, DateTime.UtcNow);
            _database.MarkVideoUploaded(task.LocalVideoRecordId, completed.RecordId);
            ProgressChanged?.Invoke(new RecordingTransferProgress(
                task.Id, totalBytes, totalBytes, RecordingTransferStates.Uploaded));
            RuntimeLog.Info(
                "RecordingTransfer",
                $"Upload verified localRecord={task.LocalVideoRecordId}, remoteRecord={completed.RecordId}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            int retryCount = task.RetryCount + 1;
            TimeSpan backoff = GetBackoff(retryCount);
            _store.MarkFailed(task.Id, retryCount, ex.Message, now.Add(backoff), now);
            ProgressChanged?.Invoke(new RecordingTransferProgress(
                task.Id, task.ServerOffset, GetExistingLength(task.LocalFilePath),
                RecordingTransferStates.Failed, ex.Message));
            RuntimeLog.Warn(
                "RecordingTransfer",
                $"Upload deferred localRecord={task.LocalVideoRecordId}, retry={retryCount}, error={ex.Message}");
        }
    }

    private async Task<MobileBackupCreateResponse> CreateOrResumeAsync(
        RecordingTransferTask task,
        AppConfig config,
        string sha256,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest(
            HttpMethod.Post,
            $"{BaseUrl(task.TargetAddress)}/api/mobile-backup/uploads",
            new MobileBackupCreateRequest
            {
                FileSha256 = sha256,
                TotalBytes = totalBytes,
                MimeType = "video/mp4"
            },
            config);
        return await SendAsync<MobileBackupCreateResponse>(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task UploadChunksAsync(
        RecordingTransferTask task,
        AppConfig config,
        string filePath,
        string fileSha256,
        string uploadId,
        long offset,
        int serverChunkSize,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        int chunkSize = Math.Clamp(serverChunkSize, 64 * 1024, MobileBackupService.ChunkSizeBytes);
        byte[] buffer = new byte[chunkSize];
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, chunkSize, useAsync: true);
        stream.Position = offset;

        while (offset < totalBytes)
        {
            int requested = (int)Math.Min(buffer.Length, totalBytes - offset);
            int read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                throw new EndOfStreamException("读取本地录像时提前结束");

            byte[] chunk = buffer.AsSpan(0, read).ToArray();
            string chunkSha256 = Convert.ToHexString(SHA256.HashData(chunk)).ToLowerInvariant();
            long end = offset + read - 1;
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                $"{BaseUrl(task.TargetAddress)}/api/mobile-backup/uploads/{Uri.EscapeDataString(uploadId)}/chunks");
            AddIdentityHeaders(request, config);
            request.Headers.TryAddWithoutValidation("X-Chunk-SHA256", chunkSha256);
            request.Content = new ByteArrayContent(chunk);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, end, totalBytes);
            MobileBackupChunkResponse response =
                await SendAsync<MobileBackupChunkResponse>(request, cancellationToken).ConfigureAwait(false);
            long nextOffset = response.Offset > offset ? response.Offset : end + 1;
            if (nextOffset > totalBytes)
                throw new InvalidDataException("主机返回的上传进度超过文件大小");
            offset = nextOffset;
            stream.Position = offset;
            _store.UpdateOffset(task.Id, offset, DateTime.UtcNow);
            ProgressChanged?.Invoke(new RecordingTransferProgress(
                task.Id, offset, totalBytes, RecordingTransferStates.Uploading));
        }
    }

    private async Task<MobileBackupCompleteResponse> CompleteAsync(
        RecordingTransferTask task,
        AppConfig config,
        VideoRecord record,
        string sha256,
        string uploadId,
        CancellationToken cancellationToken)
    {
        string trackingNumber = string.IsNullOrWhiteSpace(record.TrackingNumber)
            ? record.OrderId
            : record.TrackingNumber;
        using var request = CreateJsonRequest(
            HttpMethod.Post,
            $"{BaseUrl(task.TargetAddress)}/api/mobile-backup/uploads/{Uri.EscapeDataString(uploadId)}/complete",
            new MobileBackupCompleteRequest
            {
                FileSha256 = sha256,
                SessionId = task.SourceSessionId,
                TrackingNumber = trackingNumber,
                Mode = record.Mode,
                StartedAt = new DateTimeOffset(record.StartTime),
                DurationMilliseconds = Math.Max(1, (long)(record.DurationSeconds * 1000)),
                SourceDeviceId = config.NodeId,
                SourceDeviceName = config.NodeName,
                SourceDeviceKind = "pc"
            },
            config);
        return await SendAsync<MobileBackupCompleteResponse>(request, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateJsonRequest(
        HttpMethod method,
        string url,
        object payload,
        AppConfig config)
    {
        var request = new HttpRequestMessage(method, url);
        AddIdentityHeaders(request, config);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");
        return request;
    }

    private void AddIdentityHeaders(HttpRequestMessage request, AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.LastKnownHostAccessKey))
            throw new InvalidOperationException("保存主机尚未完成安全配对");
        request.Headers.TryAddWithoutValidation("X-EPM-Access-Key", config.LastKnownHostAccessKey);
        request.Headers.TryAddWithoutValidation("X-EPM-Device-Id", config.NodeId);
        request.Headers.TryAddWithoutValidation("X-EPM-Device-Kind", "pc");
        request.Headers.TryAddWithoutValidation(
            "X-EPM-Device-Name",
            Uri.EscapeDataString(string.IsNullOrWhiteSpace(config.NodeName)
                ? Environment.MachineName
                : config.NodeName));
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string message = TryReadError(body);
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(message)
                    ? $"主机请求失败：HTTP {(int)response.StatusCode}"
                    : message);
        }
        T? result = JsonSerializer.Deserialize<T>(body, JsonOptions);
        return result ?? throw new InvalidDataException("主机返回空响应");
    }

    private static string TryReadError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out JsonElement error)
                ? error.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static void ValidateTaskTarget(RecordingTransferTask task, AppConfig config)
    {
        if (!IsRecordingWorkstation(config))
            throw new InvalidOperationException("当前用途不是录制工位");
        if (!string.Equals(task.TargetNodeId, config.LastKnownHostNodeId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("上传任务绑定的保存主机与当前配置不一致");
    }

    private static string ResolveCurrentFilePath(RecordingTransferTask task, VideoRecord record)
    {
        if (File.Exists(record.FilePath))
            return record.FilePath;
        if (File.Exists(task.LocalFilePath))
            return task.LocalFilePath;
        throw new FileNotFoundException("本地缓存录像不存在");
    }

    private static void ValidateUploadFile(string path)
    {
        if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("录制工位只上传已经合成完成的 MP4");
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0)
            throw new FileNotFoundException("本地缓存录像为空或不存在");
        using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsRecordingWorkstation(AppConfig config) =>
        string.Equals(
            config.DeploymentPreset,
            DeploymentPresets.RecordingWorkstation,
            StringComparison.OrdinalIgnoreCase);

    private static TimeSpan GetBackoff(int retryCount) =>
        TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Clamp(retryCount - 1, 0, 6))));

    private static long GetExistingLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static string BaseUrl(string address) => WorkstationNetwork.ToUrl(address).TrimEnd('/');

    private void Signal()
    {
        if (_wakeSignal.CurrentCount == 0)
            _wakeSignal.Release();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        Signal();
        try { _worker?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
        _wakeSignal.Dispose();
        if (_ownsHttpClient)
            _httpClient.Dispose();
        _store.Dispose();
    }

    private sealed class MobileBackupCreateResponse
    {
        public string UploadId { get; set; } = "";
        public long Offset { get; set; }
        public int ChunkSize { get; set; }
        public bool FileReady { get; set; }
    }

    private sealed class MobileBackupChunkResponse
    {
        public long Offset { get; set; }
    }

    private sealed class MobileBackupCompleteResponse
    {
        public string Status { get; set; } = "";
        public string FileSha256 { get; set; } = "";
        public long RecordId { get; set; }
    }
}
