using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

internal sealed record RecordingTransferProgress(
    long TaskId,
    long SentBytes,
    long TotalBytes,
    string State,
    string Error = "");

internal sealed record RecordingTransferHostHealth(
    HostConnectionState State,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? NextProbeAt,
    string Reason);

internal sealed class RecordingTransferService : IDisposable
{
    private const int EnqueueBatchSize = 500;
    private const int MaxEnqueueBatchesPerPass = 10;
    private const int DeferredRecordingRecheckBatchSize = 32;

    private readonly record struct ChunkUploadResult(
        bool Completed,
        long Offset);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly RecordingTransferQueueStore _store;
    private readonly VideoDatabase _database;
    private readonly Func<AppConfig> _configProvider;
    private readonly Func<string, CancellationToken, Task<PackingProofNodeInfo?>> _nodeInfoResolver;
    private readonly Func<string, string, int, CancellationToken, Task<PackingProofNodeInfo?>> _hostResolver;
    private readonly Action<PackingProofNodeInfo>? _hostAddressChanged;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly ManualResetEventSlim _operationsIdle = new(initialState: true);
    private readonly object _lifecycleLock = new();
    private readonly object _hostHealthLock = new();
    private readonly object _enqueueScanLock = new();
    private readonly ConcurrentQueue<long> _completedRecordingEvents = new();
    private readonly SortedSet<long> _deferredRecordingIds = [];
    private readonly ConcurrentDictionary<string, bool> _uploadVideoCodecSupport = new(StringComparer.OrdinalIgnoreCase);
    private Task? _worker;
    private long _enqueueScanAfterId;
    private long _deferredRecheckAfterId;
    private int _recheckAllDeferredRecordingsRequested;
    private int _completedRecordingScanRequested;
    private int _retryFailedTasksRequested;
    private int _processing;
    private int _activeOperations;
    private int _resourcesDisposed;
    private HostConnectionState _hostConnectionState = HostConnectionState.Unknown;
    private DateTimeOffset? _hostLastSeenAt;
    private DateTimeOffset? _nextHostProbeAt;
    private DateTimeOffset? _nextProcessingAt;
    private string _hostHealthReason = "";
    private int _hostProbeFailureCount;
    private bool _disposed;

    internal TimeSpan ShutdownWaitTimeout { get; set; } = TimeSpan.FromSeconds(2);
    internal bool ResourcesDisposedForTesting => Volatile.Read(ref _resourcesDisposed) != 0;

    internal event Action<RecordingTransferProgress>? ProgressChanged;
    internal event Action<RecordingTransferHostHealth>? HostHealthChanged;

    public RecordingTransferService(
        RecordingTransferQueueStore store,
        VideoDatabase database,
        Func<AppConfig> configProvider,
        HttpClient? httpClient = null,
        Func<string, CancellationToken, Task<PackingProofNodeInfo?>>? nodeInfoResolver = null,
        Func<string, string, int, CancellationToken, Task<PackingProofNodeInfo?>>? hostResolver = null,
        Action<PackingProofNodeInfo>? hostAddressChanged = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _nodeInfoResolver = nodeInfoResolver ?? WorkstationNetwork.GetNodeInfoAsync;
        _hostResolver = hostResolver
            ?? (nodeInfoResolver == null
                ? WorkstationNetwork.FindHostByNodeIdAsync
                : ResolveUsingConfiguredProbeAsync);
        _hostAddressChanged = hostAddressChanged;
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
        AppConfig config = _configProvider();
        if (IsRecordingWorkstation(config)
            && !string.IsNullOrWhiteSpace(config.LastKnownHostNodeId))
        {
            if (!HasUsableHostCredential(config))
            {
                int requeued = _store.RequeueHostApprovalBlocked(DateTime.UtcNow);
                if (requeued > 0)
                {
                    RuntimeLog.Info(
                        "RecordingTransfer",
                        $"Host approval pending; restored uploads from failed state count={requeued}");
                }
            }
            int revived = _store.ReviveRetryExhaustedAbandoned(
                config.LastKnownHostNodeId,
                DateTime.UtcNow);
            if (revived > 0)
            {
                RuntimeLog.Info(
                    "RecordingTransfer",
                    $"Retry-exhausted uploads restored count={revived}; host={config.LastKnownHostNodeId}");
            }
        }
        Interlocked.Exchange(ref _completedRecordingScanRequested, 1);
        _worker ??= Task.Run(() => WorkerLoopAsync(_cts.Token));
        Signal();
    }

    public int EnqueueCompletedRecordings()
    {
        AppConfig config = _configProvider();
        if (!CanTransferToConfiguredHost(config))
            return 0;

        int added = 0;
        lock (_enqueueScanLock)
        {
            long afterId = _enqueueScanAfterId;
            bool scanComplete = false;
            for (int batch = 0; batch < MaxEnqueueBatchesPerPass; batch++)
            {
                List<VideoRecord> records = _database.GetCompletedPcVideosForTransfer(
                    EnqueueBatchSize,
                    afterId);
                if (records.Count == 0)
                {
                    scanComplete = true;
                    break;
                }

                foreach (VideoRecord record in records)
                {
                    afterId = record.Id;
                    if (!File.Exists(record.FilePath))
                    {
                        _deferredRecordingIds.Add(record.Id);
                        continue;
                    }

                    string sourceSessionId = ResolveSessionId(config, record);
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

                _enqueueScanAfterId = afterId;
                if (records.Count < EnqueueBatchSize)
                {
                    scanComplete = true;
                    break;
                }
            }

            added += ProcessCompletedRecordingEventsLocked(config);
            if (!scanComplete)
                RequestCompletedRecordingsScan();
        }

        if (added > 0)
        {
            RuntimeLog.Info("RecordingTransfer", $"Queued completed recordings count={added}");
            Signal();
        }
        return added;
    }

    internal int ProcessCompletedRecordingEvents()
    {
        if (_completedRecordingEvents.IsEmpty
            && Volatile.Read(ref _recheckAllDeferredRecordingsRequested) == 0)
        {
            return 0;
        }
        AppConfig config = _configProvider();
        if (!CanTransferToConfiguredHost(config))
            return 0;

        int added;
        lock (_enqueueScanLock)
            added = ProcessCompletedRecordingEventsLocked(config);
        if (added > 0)
            RuntimeLog.Info(
                "RecordingTransfer",
                $"Queued completed recordings from event count={added}");
        return added;
    }

    private int ProcessCompletedRecordingEventsLocked(AppConfig config)
    {
        bool recheckAllDeferred =
            Interlocked.Exchange(
                ref _recheckAllDeferredRecordingsRequested,
                0) != 0;
        bool processDeferred = recheckAllDeferred;
        int added = 0;
        while (_completedRecordingEvents.TryDequeue(out long recordId))
        {
            processDeferred = true;
            added += EnqueueCompletedRecording(config, recordId);
        }
        if (processDeferred)
        {
            IReadOnlyList<long> deferredIds;
            if (recheckAllDeferred)
            {
                deferredIds = _deferredRecordingIds.ToArray();
                _deferredRecheckAfterId = 0;
            }
            else
            {
                deferredIds = _deferredRecordingIds
                    .Where(recordId => recordId > _deferredRecheckAfterId)
                    .Take(DeferredRecordingRecheckBatchSize)
                    .ToArray();
                if (deferredIds.Count == 0 && _deferredRecheckAfterId > 0)
                {
                    _deferredRecheckAfterId = 0;
                    deferredIds = _deferredRecordingIds
                        .Take(DeferredRecordingRecheckBatchSize)
                        .ToArray();
                }
                if (deferredIds.Count > 0)
                    _deferredRecheckAfterId = deferredIds[^1];
            }
            foreach (long recordId in deferredIds)
            {
                added += EnqueueCompletedRecording(config, recordId);
            }
        }
        return added;
    }

    private int EnqueueCompletedRecording(AppConfig config, long recordId)
    {
        _deferredRecordingIds.Remove(recordId);
        VideoRecord? record = _database.GetVideoById(recordId);
        if (record == null
            || !string.Equals(record.SourceType, "pc", StringComparison.OrdinalIgnoreCase)
            || record.EndTime == default
            || !string.Equals(record.StorageState, "Local", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetExtension(record.FilePath),
                ".mp4",
                StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (!File.Exists(record.FilePath))
        {
            _deferredRecordingIds.Add(recordId);
            return 0;
        }

        string sourceSessionId = ResolveSessionId(config, record);
        return _store.Enqueue(
            record.Id,
            record.FilePath,
            sourceSessionId,
            config.LastKnownHostNodeId,
            config.LastKnownHostAddress,
            DateTime.UtcNow)
                ? 1
                : 0;
    }

    public void RetryNow()
    {
        Interlocked.Exchange(
            ref _recheckAllDeferredRecordingsRequested,
            1);
        Interlocked.Exchange(ref _retryFailedTasksRequested, 1);
        lock (_hostHealthLock)
            _nextHostProbeAt = DateTimeOffset.UtcNow;
        Signal();
    }

    internal void RequestEnqueueCompletedRecording(long recordId)
    {
        if (recordId <= 0)
            return;
        _completedRecordingEvents.Enqueue(recordId);
        Signal();
    }

    internal void RequestCompletedRecordingsScan()
    {
        Interlocked.Exchange(ref _completedRecordingScanRequested, 1);
        Interlocked.Exchange(
            ref _recheckAllDeferredRecordingsRequested,
            1);
        Signal();
    }

    internal void Wake()
    {
        lock (_hostHealthLock)
        {
            if (_nextHostProbeAt > DateTimeOffset.UtcNow)
                _nextHostProbeAt = DateTimeOffset.UtcNow;
        }
        Signal();
    }

    internal void SetUploadPaused(bool paused)
    {
        if (!IsRecordingWorkstation(_configProvider()))
            return;

        if (paused)
        {
            SetHostHealth(
                HostConnectionState.PausedByUser,
                nextProbeAt: null,
                reason: AppLanguage.Get("已暂停"),
                lastSeenAt: null,
                updateLastSeen: false);
        }
        else
        {
            lock (_hostHealthLock)
            {
                _hostProbeFailureCount = 0;
                _nextHostProbeAt = DateTimeOffset.UtcNow;
            }
            SetHostHealth(
                HostConnectionState.Unknown,
                DateTimeOffset.UtcNow,
                reason: "",
                lastSeenAt: null,
                updateLastSeen: false);
        }
        Signal();
    }

    internal RecordingTransferHostHealth GetHostHealth()
    {
        lock (_hostHealthLock)
        {
            return new RecordingTransferHostHealth(
                _hostConnectionState,
                _hostLastSeenAt,
                _nextHostProbeAt,
                _hostHealthReason);
        }
    }

    internal DateTimeOffset? GetNextProcessingAt()
    {
        lock (_hostHealthLock)
            return _nextProcessingAt;
    }

    internal async Task<bool> ProbeHostNowAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_hostHealthLock)
            _nextHostProbeAt = DateTimeOffset.UtcNow;
        return await EnsureHostReadyForProcessingAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 上传会话标识：优先用录像自己的 GUID（VideoRecords.SourceSessionId 在插入时就生成），
    /// 这样本地库重建、自增 ID 重新从 1 开始时，不会和保存主机上上一代库的旧绑定撞车，
    /// 而同一条录像的多次重试仍然稳定。老数据缺 GUID 时才退回"节点:本地ID"。
    /// </summary>
    internal static string ResolveSessionId(AppConfig config, VideoRecord record)
    {
        string session = record.SourceSessionId?.Trim() ?? "";
        return session.Length > 0 ? session : $"{config.NodeId}:{record.Id}";
    }

    internal async Task<int> ProcessReadyOnceAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _processing, 1) == 1)
            return 0;

        try
        {
            BeginOperation();
        }
        catch
        {
            Interlocked.Exchange(ref _processing, 0);
            throw;
        }

        try
        {
            AppConfig config = _configProvider();
            if (IsRecordingWorkstation(config))
            {
                if (config.RecordingUploadPaused)
                {
                    SetHostHealth(
                        HostConnectionState.PausedByUser,
                        nextProbeAt: null,
                        reason: AppLanguage.Get("已暂停"),
                        lastSeenAt: null,
                        updateLastSeen: false);
                    return 0;
                }
                if (string.IsNullOrWhiteSpace(config.LastKnownHostNodeId)
                    || string.IsNullOrWhiteSpace(config.LastKnownHostAddress))
                {
                    ScheduleHostProbeFailure(
                        HostConnectionState.Offline,
                        "尚未选择保存主机");
                    return 0;
                }
                if (!HasUsableHostCredential(config))
                {
                    if (RecordingHostApprovalPolicy.IsPending(
                            config,
                            DateTimeOffset.UtcNow))
                    {
                        ScheduleHostProbeFailure(
                            HostConnectionState.AwaitingApproval,
                            AppLanguage.Get("等待主机允许连接"));
                    }
                    else
                    {
                        ScheduleHostProbeFailure(
                            HostConnectionState.Offline,
                            RecordingHostApprovalPolicy.BuildReconnectReason(config));
                    }
                    return 0;
                }
            }
            if (!await EnsureHostReadyForProcessingAsync(cancellationToken).ConfigureAwait(false))
                return 0;

            int processed = 0;
            foreach (RecordingTransferTask task in _store.GetReady(DateTime.UtcNow))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessTaskAsync(task, cancellationToken).ConfigureAwait(false);
                processed++;
                if (IsHostProcessingPaused())
                    break;
            }
            return processed;
        }
        finally
        {
            EndOperation();
            Interlocked.Exchange(ref _processing, 0);
        }
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (Interlocked.Exchange(
                        ref _retryFailedTasksRequested,
                        0) != 0)
                {
                    _store.RetryFailedNow(DateTime.UtcNow);
                }
                if (Interlocked.Exchange(
                        ref _completedRecordingScanRequested,
                        0) != 0)
                {
                    EnqueueCompletedRecordings();
                }
                else
                {
                    ProcessCompletedRecordingEvents();
                }
                await ProcessReadyOnceAsync(cancellationToken).ConfigureAwait(false);
                TimeSpan wait = TimeSpan.FromSeconds(15);
                RecordingTransferHostHealth health = GetHostHealth();
                if (health.NextProbeAt is DateTimeOffset probeAt)
                {
                    TimeSpan untilProbe = probeAt - DateTimeOffset.UtcNow;
                    if (untilProbe > TimeSpan.Zero && untilProbe < wait)
                        wait = untilProbe;
                }
                lock (_hostHealthLock)
                    _nextProcessingAt = DateTimeOffset.UtcNow.Add(wait);
                try
                {
                    await _wakeSignal.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    lock (_hostHealthLock)
                        _nextProcessingAt = null;
                }
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
            if (IsRecordingUploadPaused(config))
                return;
            ValidateTaskTarget(task, config);
            // 用包含已删除的读法，才能把"用户删了这条"和"记录不存在"分开告诉用户。
            VideoRecord? record = _database.GetVideoByIdIncludingDeleted(task.LocalVideoRecordId);
            if (record == null || record.IsDeleted)
            {
                // 用户删了本地录像就不该继续排队：以前会一直重试到"本地录像记录不存在"。
                AbandonTask(
                    task,
                    record == null ? "本地录像记录不存在，已停止上传" : "本地录像已删除，已停止上传",
                    now);
                return;
            }

            string sessionId = ResolveSessionId(config, record);
            if (!string.Equals(sessionId, task.SourceSessionId, StringComparison.Ordinal))
            {
                _store.SetSessionId(task.Id, sessionId, now);
                task.SourceSessionId = sessionId;
                RuntimeLog.Info(
                    "RecordingTransfer",
                    $"Upload session updated localRecord={task.LocalVideoRecordId}, session={sessionId}");
            }

            string filePath = ResolveCurrentFilePath(task, record);
            ValidateUploadFile(filePath);

            PackingProofNodeInfo? node = await ResolveTaskHostAsync(task, config, cancellationToken)
                .ConfigureAwait(false);
            if (node == null)
                throw new HttpRequestException("保存主机离线");
            if (!string.Equals(node.NodeId, task.TargetNodeId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("保存主机身份已变化");
            if (!node.Capabilities.Contains(PackingProofCapabilities.MobileBackup, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("目标主机不支持录像接收");
            if (!BackupCompatibilityPolicy.IsCompatibleHost(node.BackupCompatibility))
                throw new InvalidOperationException("保存主机版本过旧，请更新保存主机电脑");
            if (IsRecordingUploadPaused(config))
                return;
            MarkHostOnline();
            bool supportsUploadVideoCodec = await SupportsUploadVideoCodecAsync(
                task,
                config,
                cancellationToken).ConfigureAwait(false);

            string sha256 = task.FileSha256;
            if (sha256.Length != 64)
            {
                sha256 = await ComputeFileSha256Async(filePath, cancellationToken).ConfigureAwait(false);
                _store.SetHash(task.Id, sha256, DateTime.UtcNow);
            }
            if (IsRecordingUploadPaused(config))
                return;

            long totalBytes = new FileInfo(filePath).Length;
            MobileBackupCreateResponse create = await CreateOrResumeAsync(
                task,
                config,
                sha256,
                totalBytes,
                cancellationToken).ConfigureAwait(false);
            if (create.Offset < 0 || create.Offset > totalBytes)
                throw new InvalidDataException("主机返回的断点位置无效");
            if (IsRecordingUploadPaused(config))
            {
                DeferTaskForUserPause(task, create.Offset, totalBytes);
                return;
            }

            _store.MarkUploading(task.Id, create.Offset, DateTime.UtcNow);
            ProgressChanged?.Invoke(new RecordingTransferProgress(
                task.Id, create.Offset, totalBytes, RecordingTransferStates.Uploading));

            if (!create.FileReady && create.Offset < totalBytes)
            {
                ChunkUploadResult chunkResult = await UploadChunksAsync(
                    task,
                    config,
                    filePath,
                    sha256,
                    create.UploadId,
                    create.Offset,
                    create.ChunkSize,
                    totalBytes,
                    cancellationToken).ConfigureAwait(false);
                if (!chunkResult.Completed)
                {
                    DeferTaskForUserPause(task, chunkResult.Offset, totalBytes);
                    return;
                }
            }

            if (IsRecordingUploadPaused(config))
            {
                DeferTaskForUserPause(task, totalBytes, totalBytes);
                return;
            }

            MobileBackupCompleteResponse completed = await CompleteAsync(
                task,
                config,
                record,
                sha256,
                create.UploadId,
                supportsUploadVideoCodec,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(completed.Status, "verified", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(completed.FileSha256, sha256, StringComparison.OrdinalIgnoreCase)
                || completed.RecordId <= 0)
            {
                throw new InvalidDataException("主机未明确确认录像已经校验并入库");
            }

            int verificationVersion = ValidateVerifiedReceipt(
                completed,
                config,
                task,
                sha256,
                totalBytes);

            _store.MarkUploaded(
                task.Id,
                completed.RecordId,
                verificationVersion,
                completed.ReceiptSignature,
                DateTime.UtcNow);
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
            if (ex is RecordingTransferHostException { RetryWholeFile: true } wholeFile)
            {
                HandleWholeFileRetry(task, wholeFile, now);
                return;
            }

            int retryCount = task.RetryCount + 1;
            string stopReason = RecordingTransferFailurePolicy.ClassifyFailure(ex);
            string failureKind = RecordingTransferFailurePolicy.ClassifyFailureKind(ex);
            if (stopReason.Length > 0)
            {
                AbandonTask(task, stopReason, now, failureKind);
                return;
            }

            if (IsHostPauseFailure(failureKind))
            {
                int retryAfterSeconds = ex is RecordingTransferHostException host
                    ? host.RetryAfterSeconds
                    : 0;
                _store.DeferForHost(task.Id, ex.Message, failureKind, now);
                PauseHost(failureKind, ex.Message, now, retryAfterSeconds);
                ProgressChanged?.Invoke(new RecordingTransferProgress(
                    task.Id,
                    task.ServerOffset,
                    GetExistingLength(task.LocalFilePath),
                    RecordingTransferStates.Pending,
                    ex.Message));
                RuntimeLog.Warn(
                    "RecordingTransfer",
                    $"Host paused localRecord={task.LocalVideoRecordId}, kind={failureKind}, error={ex.Message}");
                return;
            }

            TimeSpan backoff = GetBackoff(retryCount, task.Id);
            if (failureKind == RecordingTransferFailureKinds.HostWriteBusy)
            {
                DateTime nextAttemptAt = now.Add(backoff);
                _store.DeferForRetry(
                    task.Id,
                    ex.Message,
                    failureKind,
                    nextAttemptAt,
                    now);
                ProgressChanged?.Invoke(new RecordingTransferProgress(
                    task.Id,
                    task.ServerOffset,
                    GetExistingLength(task.LocalFilePath),
                    RecordingTransferStates.Pending,
                    ex.Message));
                RuntimeLog.Info(
                    "RecordingTransfer",
                    $"Host disk busy, deferred localRecord={task.LocalVideoRecordId}, next={nextAttemptAt:O}");
                return;
            }
            _store.MarkFailed(
                task.Id,
                retryCount,
                ex.Message,
                now.Add(backoff),
                now,
                failureKind);
            ProgressChanged?.Invoke(new RecordingTransferProgress(
                task.Id, task.ServerOffset, GetExistingLength(task.LocalFilePath),
                RecordingTransferStates.Failed, ex.Message));
            RuntimeLog.Warn(
                "RecordingTransfer",
                $"Upload deferred localRecord={task.LocalVideoRecordId}, retry={retryCount}, error={ex.Message}");
        }
    }

    private void HandleWholeFileRetry(
        RecordingTransferTask task,
        RecordingTransferHostException exception,
        DateTime now)
    {
        int maxAttempts = Math.Clamp(
            exception.MaxFileAttempts > 0 ? exception.MaxFileAttempts : 3,
            1,
            10);
        int attemptCount = task.WholeFileRetryCount + 1;
        string reason = string.IsNullOrWhiteSpace(exception.Message)
            ? "上传失败"
            : exception.Message.Trim();
        if (attemptCount >= maxAttempts)
        {
            _store.MarkWholeFileRetry(task.Id, attemptCount, reason, now, now);
            AbandonTask(
                task,
                AppLanguage.Format(
                    "Main.RecordingTransfer.WholeFileAttemptsExhausted",
                    reason,
                    attemptCount),
                now);
            return;
        }

        TimeSpan backoff = GetBackoff(attemptCount, task.Id);
        _store.MarkWholeFileRetry(
            task.Id,
            attemptCount,
            reason,
            now.Add(backoff),
            now);
        ProgressChanged?.Invoke(new RecordingTransferProgress(
            task.Id,
            0,
            GetExistingLength(task.LocalFilePath),
            RecordingTransferStates.Failed,
            reason));
        RuntimeLog.Warn(
            "RecordingTransfer",
            $"Whole-file upload retry localRecord={task.LocalVideoRecordId}, attempt={attemptCount}/{maxAttempts}, error={reason}");
    }

    private void AbandonTask(
        RecordingTransferTask task,
        string reason,
        DateTime now,
        string failureKind = RecordingTransferFailureKinds.ActionRequired)
    {
        _store.MarkAbandoned(task.Id, reason, now, failureKind);
        ProgressChanged?.Invoke(new RecordingTransferProgress(
            task.Id,
            task.ServerOffset,
            GetExistingLength(task.LocalFilePath),
            RecordingTransferStates.Abandoned,
            reason));
        RuntimeLog.Warn(
            "RecordingTransfer",
            $"Upload abandoned localRecord={task.LocalVideoRecordId}, error={reason}");
    }

    private void DeferTaskForUserPause(
        RecordingTransferTask task,
        long offset,
        long totalBytes)
    {
        DateTime now = DateTime.UtcNow;
        string reason = AppLanguage.Get("已暂停");
        _store.DeferForUserPause(task.Id, offset, reason, now);
        SetHostHealth(
            HostConnectionState.PausedByUser,
            nextProbeAt: null,
            reason,
            lastSeenAt: null,
            updateLastSeen: false);
        ProgressChanged?.Invoke(new RecordingTransferProgress(
            task.Id,
            offset,
            totalBytes,
            RecordingTransferStates.Pending,
            reason));
        RuntimeLog.Info(
            "RecordingTransfer",
            $"Upload paused by user localRecord={task.LocalVideoRecordId}, offset={offset}");
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

    private async Task<ChunkUploadResult> UploadChunksAsync(
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
            if (IsRecordingUploadPaused(config))
                return new ChunkUploadResult(false, offset);

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
            request.Headers.TryAddWithoutValidation("X-Chunk-SHA256", chunkSha256);
            request.Content = new ByteArrayContent(chunk);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, end, totalBytes);
            AddAuthenticationHeaders(request, config, chunk);
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
            if (IsRecordingUploadPaused(config))
                return new ChunkUploadResult(false, offset);
        }

        return new ChunkUploadResult(true, offset);
    }

    private async Task<MobileBackupCompleteResponse> CompleteAsync(
        RecordingTransferTask task,
        AppConfig config,
        VideoRecord record,
        string sha256,
        string uploadId,
        bool supportsUploadVideoCodec,
        CancellationToken cancellationToken)
    {
        string trackingNumber = JdBarcodePolicy.Parse(record.OrderId) != null || string.IsNullOrWhiteSpace(record.TrackingNumber)
            ? record.OrderId
            : record.TrackingNumber;
        using var request = CreateJsonRequest(
            HttpMethod.Post,
            $"{BaseUrl(task.TargetAddress)}/api/mobile-backup/uploads/{Uri.EscapeDataString(uploadId)}/complete",
            new MobileBackupCompleteRequest
            {
                FileSha256 = sha256,
                SourceDeviceId = config.NodeId,
                SourceDeviceName = config.NodeName,
                SourceDeviceKind = "pc",
                VideoCodec = supportsUploadVideoCodec
                    ? MobileBackupService.NormalizeVideoCodec(record.VideoCodec)
                    : null,
                Sessions =
                [
                    new MobileBackupSessionRequest
                    {
                        Id = task.SourceSessionId,
                        TrackingNumber = trackingNumber,
                        Mode = record.Mode,
                        StartedAt = new DateTimeOffset(record.StartTime),
                        EndedAt = new DateTimeOffset(
                            record.StartTime.AddMilliseconds(Math.Max(1, record.DurationSeconds * 1000))),
                        MediaStartMs = 0,
                        MediaEndMs = Math.Max(1, (long)(record.DurationSeconds * 1000)),
                        Markers = []
                    }
                ]
            },
            config);
        return await SendAsync<MobileBackupCompleteResponse>(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SupportsUploadVideoCodecAsync(
        RecordingTransferTask task,
        AppConfig config,
        CancellationToken cancellationToken)
    {
        if (_uploadVideoCodecSupport.TryGetValue(task.TargetNodeId, out bool cached))
            return cached;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{BaseUrl(task.TargetAddress)}/api/mobile-backup/capabilities");
            AddAuthenticationHeaders(request, config, []);
            MobileBackupCapabilitiesResponse response =
                await SendAsync<MobileBackupCapabilitiesResponse>(request, cancellationToken).ConfigureAwait(false);
            bool supported = response.Features?.UploadVideoCodec == true;
            _uploadVideoCodecSupport[task.TargetNodeId] = supported;
            return supported;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RuntimeLog.Warn("RecordingTransfer", $"Host codec capability unavailable: {ex.Message}");
            _uploadVideoCodecSupport[task.TargetNodeId] = false;
            return false;
        }
    }

    private HttpRequestMessage CreateJsonRequest(
        HttpMethod method,
        string url,
        object payload,
        AppConfig config)
    {
        var request = new HttpRequestMessage(method, url);
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        AddAuthenticationHeaders(request, config, content);
        return request;
    }

    private static void AddAuthenticationHeaders(
        HttpRequestMessage request,
        AppConfig config,
        ReadOnlySpan<byte> content)
    {
        if (string.IsNullOrWhiteSpace(config.LastKnownHostAccessKey))
            throw new InvalidOperationException("保存主机尚未完成安全配对");
        request.Headers.TryAddWithoutValidation("X-EPM-Device-Id", config.NodeId);
        request.Headers.TryAddWithoutValidation("X-EPM-Device-Kind", "pc");
        request.Headers.TryAddWithoutValidation(
            "X-EPM-Device-Name",
            Uri.EscapeDataString(string.IsNullOrWhiteSpace(config.NodeName)
                ? Environment.MachineName
                : config.NodeName));

        if (config.LastKnownHostBackupAuthVersion < BackupRequestAuthentication.CurrentVersion)
            throw new InvalidOperationException("保存主机连接协议已升级，请重新连接保存主机");

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string contentHash = BackupRequestAuthentication.ComputeContentHash(content);
        string path = request.RequestUri?.PathAndQuery ?? "/";
        string signature = BackupRequestAuthentication.CreateRequestSignature(
            config.LastKnownHostAccessKey,
            request.Method.Method,
            path,
            timestamp,
            nonce,
            contentHash,
            config.NodeId);
        request.Headers.TryAddWithoutValidation(
            BackupRequestAuthentication.VersionHeader,
            BackupRequestAuthentication.CurrentVersion.ToString());
        request.Headers.TryAddWithoutValidation(
            BackupRequestAuthentication.TimestampHeader,
            timestamp.ToString());
        request.Headers.TryAddWithoutValidation(BackupRequestAuthentication.NonceHeader, nonce);
        request.Headers.TryAddWithoutValidation(
            BackupRequestAuthentication.ContentHashHeader,
            contentHash);
        request.Headers.TryAddWithoutValidation(
            BackupRequestAuthentication.SignatureHeader,
            signature);
    }

    private static int ValidateVerifiedReceipt(
        MobileBackupCompleteResponse completed,
        AppConfig config,
        RecordingTransferTask task,
        string fileSha256,
        long fileSizeBytes)
    {
        if (config.LastKnownHostBackupAuthVersion < BackupRequestAuthentication.CurrentVersion)
            return 0;
        if (completed.AuthVersion != BackupRequestAuthentication.CurrentVersion
            || !string.Equals(completed.HostNodeId, task.TargetNodeId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(completed.SourceDeviceId, config.NodeId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(completed.SourceSessionId, task.SourceSessionId, StringComparison.Ordinal)
            || completed.FileSizeBytes != fileSizeBytes
            || !BackupRequestAuthentication.IsFresh(
                completed.VerifiedAtUnixSeconds,
                DateTimeOffset.UtcNow))
        {
            throw new InvalidDataException("保存主机返回的完整性确认信息不一致");
        }
        string expected = BackupRequestAuthentication.CreateReceiptSignature(
            config.LastKnownHostAccessKey,
            completed.HostNodeId,
            completed.SourceDeviceId,
            completed.SourceSessionId,
            fileSha256,
            fileSizeBytes,
            completed.RecordId,
            completed.VerifiedAtUnixSeconds);
        if (!BackupRequestAuthentication.FixedTimeEquals(expected, completed.ReceiptSignature))
            throw new InvalidDataException("保存主机的完整性确认签名无效");
        return BackupRequestAuthentication.CurrentVersion;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            (string errorCode, string message, bool retryWholeFile, int maxFileAttempts) =
                RecordingTransferFailurePolicy.TryReadError(body);
            int retryAfterSeconds = ReadRetryAfterSeconds(response);
            string text = string.IsNullOrWhiteSpace(message)
                ? $"主机请求失败：HTTP {(int)response.StatusCode}"
                : message;
            throw string.IsNullOrWhiteSpace(errorCode)
                ? new HttpRequestException(text, null, response.StatusCode)
                : new RecordingTransferHostException(
                    errorCode,
                    text,
                    response.StatusCode,
                    retryWholeFile,
                    maxFileAttempts,
                    retryAfterSeconds);
        }
        T? result = JsonSerializer.Deserialize<T>(body, JsonOptions);
        return result ?? throw new InvalidDataException("主机返回空响应");
    }

    private static int ReadRetryAfterSeconds(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
            return Math.Clamp((int)Math.Ceiling(delta.TotalSeconds), 1, 900);
        if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
            return Math.Clamp(
                (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds),
                1,
                900);
        return 0;
    }

    private static void ValidateTaskTarget(RecordingTransferTask task, AppConfig config)
    {
        if (!IsRecordingWorkstation(config))
            throw new InvalidOperationException("当前用途不是录制工位");
        if (!string.Equals(task.TargetNodeId, config.LastKnownHostNodeId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("上传任务绑定的保存主机与当前配置不一致");
    }

    private async Task<PackingProofNodeInfo?> ResolveUsingConfiguredProbeAsync(
        string nodeId,
        string address,
        int _,
        CancellationToken cancellationToken)
    {
        PackingProofNodeInfo? node = await _nodeInfoResolver(address, cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(node?.NodeId, nodeId, StringComparison.OrdinalIgnoreCase)
            ? node
            : null;
    }

    private async Task<PackingProofNodeInfo?> ResolveTaskHostAsync(
        RecordingTransferTask task,
        AppConfig config,
        CancellationToken cancellationToken)
    {
        PackingProofNodeInfo? node = await _hostResolver(
                task.TargetNodeId,
                task.TargetAddress,
                config.WebServerPort,
                cancellationToken)
            .ConfigureAwait(false);
        if (node != null
            && !string.Equals(node.NodeId, task.TargetNodeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("保存主机身份已变化");
        }
        if (node == null
            || string.Equals(
                WorkstationNetwork.NormalizeAddress(node.Address),
                WorkstationNetwork.NormalizeAddress(task.TargetAddress),
                StringComparison.OrdinalIgnoreCase))
        {
            return node;
        }

        task.TargetAddress = node.Address.Trim().TrimEnd('/');
        _store.UpdateTargetAddress(task.TargetNodeId, task.TargetAddress, DateTime.UtcNow);
        _hostAddressChanged?.Invoke(node);
        RuntimeLog.Info(
            "RecordingTransfer",
            $"Resolved host address node={task.TargetNodeId}, address={task.TargetAddress}");
        return node;
    }

    private async Task<bool> EnsureHostReadyForProcessingAsync(
        CancellationToken cancellationToken)
    {
        RecordingTransferHostHealth health = GetHostHealth();
        if (!IsHostPauseState(health.State))
            return true;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (health.NextProbeAt is DateTimeOffset probeAt && probeAt > now)
            return false;
        return await ProbeHostHealthAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ProbeHostHealthAsync(CancellationToken cancellationToken)
    {
        RecordingTransferTask? task = _store.GetFirstUnfinishedTarget();
        if (task == null)
        {
            MarkHostOnline();
            return true;
        }

        RecordingTransferHostHealth previous = GetHostHealth();
        try
        {
            AppConfig config = _configProvider();
            PackingProofNodeInfo? node = await ResolveTaskHostAsync(
                    task,
                    config,
                    cancellationToken)
                .ConfigureAwait(false);
            if (node == null
                || !node.Capabilities.Contains(
                    PackingProofCapabilities.MobileBackup,
                    StringComparer.OrdinalIgnoreCase))
            {
                ScheduleHostProbeFailure(
                    previous.State == HostConnectionState.Unknown
                        ? HostConnectionState.Offline
                        : previous.State,
                    "保存主机离线");
                return false;
            }

            using var capabilitiesRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"{BaseUrl(task.TargetAddress)}/api/mobile-backup/capabilities");
            AddAuthenticationHeaders(capabilitiesRequest, config, ReadOnlySpan<byte>.Empty);
            MobileBackupCapabilitiesResponse capabilities =
                await SendAsync<MobileBackupCapabilitiesResponse>(
                        capabilitiesRequest,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (capabilities.StorageAvailability is { Available: false } storage)
            {
                HostConnectionState storageState = string.Equals(
                    storage.State,
                    "storage_unavailable",
                    StringComparison.OrdinalIgnoreCase)
                    ? HostConnectionState.StorageBlocked
                    : HostConnectionState.WriteBusy;
                ScheduleHostProbeFailure(
                    storageState,
                    storage.State,
                    storage.RetryAfterSeconds);
                return false;
            }

            MarkHostOnline();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ScheduleHostProbeFailure(
                previous.State == HostConnectionState.Unknown
                    ? HostConnectionState.Offline
                    : previous.State,
                ex.Message);
            return false;
        }
    }

    private bool IsHostProcessingPaused() =>
        IsHostPauseState(GetHostHealth().State);

    private static bool IsHostPauseState(HostConnectionState state) =>
        state is HostConnectionState.Offline
            or HostConnectionState.AwaitingApproval
            or HostConnectionState.PausedByUser
            or HostConnectionState.StorageBlocked
            or HostConnectionState.WriteBusy;

    private static bool IsHostPauseFailure(string failureKind) =>
        failureKind is RecordingTransferFailureKinds.HostOffline
            or RecordingTransferFailureKinds.HostApprovalRequired
            or RecordingTransferFailureKinds.HostStorageUnavailable;

    private void PauseHost(
        string failureKind,
        string reason,
        DateTime now,
        int retryAfterSeconds)
    {
        HostConnectionState state = failureKind switch
        {
            RecordingTransferFailureKinds.HostStorageUnavailable =>
                HostConnectionState.StorageBlocked,
            RecordingTransferFailureKinds.HostWriteBusy =>
                HostConnectionState.WriteBusy,
            RecordingTransferFailureKinds.HostApprovalRequired =>
                HostConnectionState.AwaitingApproval,
            _ => HostConnectionState.Offline
        };
        int delaySeconds;
        lock (_hostHealthLock)
        {
            _hostProbeFailureCount = Math.Min(_hostProbeFailureCount + 1, 8);
            delaySeconds = retryAfterSeconds > 0
                ? Math.Clamp(retryAfterSeconds, 1, 900)
                : GetHostProbeDelaySeconds(_hostProbeFailureCount);
        }
        SetHostHealth(
            state,
            DateTimeOffset.UtcNow.AddSeconds(delaySeconds),
            reason,
            lastSeenAt: null,
            updateLastSeen: false);
        RuntimeLog.Warn(
            "RecordingTransfer",
            $"Host circuit opened state={state}, retryAfter={delaySeconds}s, reason={reason}");
    }

    private void ScheduleHostProbeFailure(
        HostConnectionState state,
        string reason,
        int retryAfterSeconds = 0)
    {
        int delaySeconds;
        lock (_hostHealthLock)
        {
            _hostProbeFailureCount = Math.Min(_hostProbeFailureCount + 1, 8);
            delaySeconds = retryAfterSeconds > 0
                ? Math.Clamp(retryAfterSeconds, 1, 900)
                : GetHostProbeDelaySeconds(_hostProbeFailureCount);
        }
        SetHostHealth(
            state,
            DateTimeOffset.UtcNow.AddSeconds(delaySeconds),
            reason,
            lastSeenAt: null,
            updateLastSeen: false);
    }

    private static int GetHostProbeDelaySeconds(int failureCount) =>
        (int)Math.Min(
            900,
            5 * Math.Pow(2, Math.Clamp(failureCount - 1, 0, 8)));

    private void MarkHostOnline()
    {
        lock (_hostHealthLock)
            _hostProbeFailureCount = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SetHostHealth(
            HostConnectionState.Online,
            nextProbeAt: null,
            reason: "",
            lastSeenAt: now,
            updateLastSeen: true);
    }

    private static bool HasUsableHostCredential(AppConfig config) =>
        !string.IsNullOrWhiteSpace(config.LastKnownHostAccessKey)
        && config.LastKnownHostBackupAuthVersion
            >= BackupRequestAuthentication.CurrentVersion;

    private static bool CanTransferToConfiguredHost(AppConfig config) =>
        IsRecordingWorkstation(config)
        && !string.IsNullOrWhiteSpace(config.LastKnownHostNodeId)
        && !string.IsNullOrWhiteSpace(config.LastKnownHostAddress)
        && HasUsableHostCredential(config);

    private void SetHostHealth(
        HostConnectionState state,
        DateTimeOffset? nextProbeAt,
        string reason,
        DateTimeOffset? lastSeenAt,
        bool updateLastSeen)
    {
        RecordingTransferHostHealth health;
        bool changed;
        lock (_hostHealthLock)
        {
            changed = _hostConnectionState != state
                || _nextHostProbeAt != nextProbeAt
                || !string.Equals(_hostHealthReason, reason, StringComparison.Ordinal)
                || (updateLastSeen && _hostLastSeenAt != lastSeenAt);
            _hostConnectionState = state;
            _nextHostProbeAt = nextProbeAt;
            _hostHealthReason = reason ?? "";
            if (updateLastSeen)
                _hostLastSeenAt = lastSeenAt;
            health = new RecordingTransferHostHealth(
                _hostConnectionState,
                _hostLastSeenAt,
                _nextHostProbeAt,
                _hostHealthReason);
        }
        if (!changed)
            return;
        try { HostHealthChanged?.Invoke(health); } catch { }
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

    private static bool IsRecordingUploadPaused(AppConfig config) =>
        IsRecordingWorkstation(config) && config.RecordingUploadPaused;

    private static TimeSpan GetBackoff(int retryCount, long taskId)
    {
        double exponentialSeconds = Math.Min(
            900,
            5 * Math.Pow(2, Math.Clamp(retryCount - 1, 0, 8)));
        // 长时间离线后大量任务会同时到期；按任务 ID 加少量抖动，避免十台机器同时冲击主机。
        int jitterSeconds = (int)((ulong)taskId % 31);
        return TimeSpan.FromSeconds(exponentialSeconds + jitterSeconds);
    }

    private static long GetExistingLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    internal async Task<bool> VerifyRemoteRecordForCleanupAsync(
        RecordingTransferTask task,
        long localFileSizeBytes,
        CancellationToken cancellationToken = default)
    {
        if (task.RemoteVideoRecordId is not long recordId || recordId <= 0
            || task.VerificationVersion < BackupRequestAuthentication.CurrentVersion
            || string.IsNullOrWhiteSpace(task.FileSha256))
            return false;
        try
        {
            AppConfig config = _configProvider();
            ValidateTaskTarget(task, config);
            PackingProofNodeInfo? node = await ResolveTaskHostAsync(task, config, cancellationToken)
                .ConfigureAwait(false);
            if (node == null)
                return false;
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{BaseUrl(task.TargetAddress)}/api/mobile-backup/records/{recordId}/attestation");
            AddAuthenticationHeaders(request, config, ReadOnlySpan<byte>.Empty);
            MobileBackupCompleteResponse attestation =
                await SendAsync<MobileBackupCompleteResponse>(request, cancellationToken)
                    .ConfigureAwait(false);
            return string.Equals(attestation.Status, "verified", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attestation.FileSha256, task.FileSha256, StringComparison.OrdinalIgnoreCase)
                && attestation.RecordId == recordId
                && ValidateVerifiedReceipt(
                    attestation,
                    config,
                    task,
                    task.FileSha256,
                    localFileSizeBytes) == BackupRequestAuthentication.CurrentVersion;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "RecordingTransfer",
                $"Remote verification unavailable; local cache retained record={task.LocalVideoRecordId}, error={ex.Message}");
            return false;
        }
    }

    private static string BaseUrl(string address) => WorkstationNetwork.ToUrl(address).TrimEnd('/');

    private void Signal()
    {
        if (_wakeSignal.CurrentCount == 0)
            _wakeSignal.Release();
    }

    private void BeginOperation()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeOperations++;
            _operationsIdle.Reset();
        }
    }

    private void EndOperation()
    {
        lock (_lifecycleLock)
        {
            _activeOperations--;
            if (_activeOperations == 0)
                _operationsIdle.Set();
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _cts.Cancel();
        Signal();

        var shutdownTimer = System.Diagnostics.Stopwatch.StartNew();
        bool workerStopped = WaitForWorker(ShutdownWaitTimeout);
        TimeSpan operationsWait = ShutdownWaitTimeout - shutdownTimer.Elapsed;
        bool operationsStopped = _operationsIdle.Wait(
            operationsWait > TimeSpan.Zero ? operationsWait : TimeSpan.Zero);
        if (workerStopped && operationsStopped)
        {
            DisposeResources();
            return;
        }

        RuntimeLog.Warn(
            "RecordingTransfer",
            "Transfer shutdown is still completing; resources will be released after active work exits");
        _ = Task.Run(() =>
        {
            try
            {
                WaitForWorker(Timeout.InfiniteTimeSpan);
                _operationsIdle.Wait();
            }
            finally
            {
                DisposeResources();
            }
        });
    }

    private bool WaitForWorker(TimeSpan timeout)
    {
        Task? worker = _worker;
        if (worker == null)
            return true;
        try
        {
            return worker.Wait(timeout);
        }
        catch
        {
            return worker.IsCompleted;
        }
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
            return;
        _cts.Dispose();
        _wakeSignal.Dispose();
        _operationsIdle.Dispose();
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
        public int AuthVersion { get; set; }
        public string HostNodeId { get; set; } = "";
        public string SourceDeviceId { get; set; } = "";
        public string SourceSessionId { get; set; } = "";
        public long FileSizeBytes { get; set; }
        public long VerifiedAtUnixSeconds { get; set; }
        public string ReceiptSignature { get; set; } = "";
    }

    private sealed class MobileBackupCapabilitiesResponse
    {
        public MobileBackupFeaturesResponse? Features { get; set; }
        public MobileBackupStorageAvailabilityResponse? StorageAvailability { get; set; }
    }

    private sealed class MobileBackupFeaturesResponse
    {
        public bool UploadVideoCodec { get; set; }
    }

    private sealed class MobileBackupStorageAvailabilityResponse
    {
        public bool Available { get; set; }
        public string State { get; set; } = "";
        public int RetryAfterSeconds { get; set; }
    }
}
