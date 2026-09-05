using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Logging;
using System.IO;

namespace ExpressPackingMonitoring.Services;

internal enum ArchiveWorkerPhase
{
    Idle,
    Uploading,
    WaitingForNextBatch,
    PausedForRecording
}

internal readonly record struct ArchiveWorkerSnapshot(
    ArchiveWorkerPhase Phase,
    DateTime? NextBatchAt = null);

/// <summary>
/// 网络归档后台服务：从数据库队列取待归档/待删除记录，
/// 通过 IArchiveProvider 复制、校验、发布到网络位置。
/// </summary>
internal sealed class ArchiveService : IDisposable
{
    private readonly VideoDatabase _database;
    private readonly IArchiveProvider _provider;
    private readonly ArchiveWorkerOptions _options;
    private readonly Func<IReadOnlyList<StorageLocation>>? _archiveTargetResolver;
    private readonly Func<ArchiveLoadState> _loadStateProvider;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly Task _worker;
    private static readonly TimeSpan BackfillInterval = TimeSpan.FromMinutes(5);
    private const int BackfillCatchUpBatchSize = 2000;
    private DateTime _lastBackfillAt = DateTime.MinValue;
    private readonly object _circuitSync = new();
    private string _unreachableRoot = "";
    private int _consecutiveUnreachableFailures;
    private DateTime _circuitRetryAfter;
    private TimeSpan _nextUnreachableCooldown;
    private readonly object _recoverySync = new();
    private bool _recoveryMode;
    private int _recoveryBatchSize;
    private DateTime _recoveryNextRoundAt;
    private readonly ArchiveTransferThrottle? _transferThrottle;
    private readonly object _workerSnapshotSync = new();
    private ArchiveWorkerSnapshot _workerSnapshot =
        new(ArchiveWorkerPhase.Idle);
    private int _archiveInProgress;

    public ArchiveService(
        VideoDatabase database,
        IArchiveProvider provider,
        ArchiveWorkerOptions? options = null,
        Func<IReadOnlyList<StorageLocation>>? archiveTargetResolver = null,
        CancellationToken cancellationToken = default,
        Func<ArchiveLoadState>? loadStateProvider = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? new ArchiveWorkerOptions();
        _nextUnreachableCooldown = _options.UnreachableCooldown;
        _archiveTargetResolver = archiveTargetResolver;
        _loadStateProvider = loadStateProvider ?? (() => ArchiveLoadState.Healthy);
        InitializeBacklogRecovery();
        if (_provider is IArchiveTransferThrottleAware throttleAware)
        {
            _transferThrottle = new ArchiveTransferThrottle(
                IsRecoveryThrottleActive,
                ReadLoadState);
            throttleAware.SetTransferThrottle(_transferThrottle);
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// 备份目标可用性变化通知：available=false 表示目标不可达并进入熔断，
    /// 参数携带不可达根路径；available=true 表示已确认恢复可达。
    /// 事件在 Worker 线程触发，订阅方需自行切换 UI 线程。
    /// </summary>
    public event Action<bool, string>? BackupTargetAvailabilityChanged;

    /// <summary>Worker 阶段变化通知；在 Worker 线程触发，订阅方不得直接操作 UI。</summary>
    public event Action<ArchiveWorkerSnapshot>? WorkerStateChanged;

    /// <summary>单条录像完成一次归档尝试后的队列变化通知。</summary>
    public event Action? ArchiveQueueChanged;

    public ArchiveWorkerSnapshot CurrentWorkerSnapshot
    {
        get
        {
            lock (_workerSnapshotSync)
                return _workerSnapshot;
        }
    }

    public void Wake()
    {
        try
        {
            _lastBackfillAt = DateTime.MinValue; // 唤醒后立即允许历史回填扫描
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 信号已置位：并发唤醒时忽略，避免检查后再释放的竞态。
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>处理一轮待归档记录（供测试与手动触发使用）。</summary>
    internal async Task<int> ProcessPendingOnceAsync(CancellationToken cancellationToken)
    {
        if (ReadLoadState() == ArchiveLoadState.Paused)
        {
            SetWorkerSnapshot(new ArchiveWorkerSnapshot(
                ArchiveWorkerPhase.PausedForRecording));
            return 0;
        }
        if (!TryBeginRecoveryRound(out bool recoveryRound))
            return 0;

        string? archiveTarget = ResolveArchiveTarget();
        if (archiveTarget == null)
            return 0; // 解析失败：跳过本轮
        if (string.IsNullOrWhiteSpace(archiveTarget))
        {
            if (_archiveTargetResolver != null)
                return 0; // 明确未配置网络归档目标
        }
        else
        {
            if (IsCircuitOpen())
                return 0; // 备份位置不可达，冷却期内不逐条空转
            BackfillHistoricalArchives(archiveTarget);
        }

        int batchSize = recoveryRound
            ? Math.Min(
                Math.Max(1, _options.BatchSize),
                Math.Clamp(
                    _recoveryBatchSize,
                    1,
                    Math.Max(1, _options.RecoveryMaxBatchSize)))
            : Math.Max(1, _options.BatchSize);
        IReadOnlyList<VideoRecord> records = _database.GetPendingArchives(batchSize, DateTime.Now);
        int completed = 0;
        bool pausedForRecording = false;
        foreach (VideoRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadLoadState() == ArchiveLoadState.Paused)
            {
                pausedForRecording = true;
                break;
            }
            if (IsCircuitOpen())
                break; // 本轮中途触发熔断：停止剩余记录，避免继续做挂起探测
            using IDisposable ownership = await VideoLifecycleCoordinator.EnterAsync(
                record.Id,
                cancellationToken);
            Volatile.Write(ref _archiveInProgress, 1);
            SetWorkerSnapshot(new ArchiveWorkerSnapshot(ArchiveWorkerPhase.Uploading));
            try
            {
                if (await TryArchiveAsync(record, cancellationToken, archiveTarget).ConfigureAwait(false))
                    completed++;
            }
            finally
            {
                Volatile.Write(ref _archiveInProgress, 0);
                NotifyArchiveQueueChanged();
            }
        }
        CompleteRecoveryRound(recoveryRound, records.Count);
        if (pausedForRecording)
        {
            SetWorkerSnapshot(new ArchiveWorkerSnapshot(
                ArchiveWorkerPhase.PausedForRecording));
        }
        else if (!recoveryRound)
        {
            SetWorkerSnapshot(records.Count >= batchSize
                ? new ArchiveWorkerSnapshot(
                    ArchiveWorkerPhase.WaitingForNextBatch,
                    DateTime.Now + _options.PollInterval)
                : new ArchiveWorkerSnapshot(ArchiveWorkerPhase.Idle));
        }
        return completed;
    }

    /// <summary>
    /// 解析当前网络归档目标：优先选择当前可用（可达且空间充足）的备份位置，
    /// 全部不可用时回退到优先级最高的一个；未注入解析器返回空串（保持旧行为不限制目标）；
    /// 未配置网络归档目标返回空串；解析失败返回 null（本轮跳过，状态保留）。
    /// </summary>
    private string? ResolveArchiveTarget()
    {
        if (_archiveTargetResolver == null)
            return "";
        IReadOnlyList<StorageLocation>? locations = GetArchiveCandidates();
        if (locations == null)
            return null;
        if (locations.Count == 0)
            return "";
        return StorageLocationResolver.SelectUsableArchiveRoot(locations)
            ?? locations[0].Path;
    }

    /// <summary>
    /// 获取按优先级排序的网络备份位置列表；解析失败返回 null。
    /// </summary>
    private IReadOnlyList<StorageLocation>? GetArchiveCandidates()
    {
        if (_archiveTargetResolver == null)
            return null;
        try
        {
            return _archiveTargetResolver();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Archive", $"Archive target resolution failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 选择当前可用的下一个备份位置；排除当前目标（NAS 满后切换到下一个）。
    /// </summary>
    private string? SelectUsableArchiveRoot(string? excludePath)
    {
        IReadOnlyList<StorageLocation>? locations = GetArchiveCandidates();
        return locations == null
            ? null
            : StorageLocationResolver.SelectUsableArchiveRoot(locations, excludePath);
    }

    /// <summary>
    /// 历史回填：为 NAS 配置前已定稿的 MP4 记录补设归档路径并置 Pending，
    /// 按记录来源保持本机录像和外部上传的目录布局；本地文件已不存在的记录跳过。
    /// </summary>
    private void BackfillHistoricalArchives(string archiveTarget)
    {
        if (DateTime.UtcNow - _lastBackfillAt < BackfillInterval)
            return;

        int updated = 0;
        int processed = 0;
        while (processed < BackfillCatchUpBatchSize)
        {
            IReadOnlyList<VideoRecord> candidates =
                _database.GetBackfillCandidates(200);
            if (candidates.Count == 0)
                break;
            processed += candidates.Count;
            foreach (VideoRecord record in candidates)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(record.FilePath) || !File.Exists(record.FilePath))
                        continue;
                    string archivePath = BuildHistoricalArchivePath(record, archiveTarget);
                    updated += _database.SetArchiveTarget(record.Id, archivePath);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("Archive", $"Backfill failed id={record.Id}, error={ex.Message}");
                }
            }
            if (candidates.Count < 200)
                break;
        }
        // 本轮正常完成后才记录节流时间；异常中断不占用 5 分钟窗口
        _lastBackfillAt = DateTime.UtcNow;
        if (updated > 0)
            RuntimeLog.Info("Archive", $"Backfilled historical archive paths count={updated}");
    }

    private static string BuildHistoricalArchivePath(VideoRecord record, string archiveTarget)
    {
        if (string.Equals(record.SourceType, "external", StringComparison.OrdinalIgnoreCase))
        {
            return ArchivePathBuilder.BuildExternalUploadArchivePath(
                archiveTarget,
                record.SourceDeviceKind,
                record.SourceDeviceId,
                record.SourceDeviceName,
                record.StartTime,
                record.TrackingNumber,
                record.Mode,
                record.ContentSha256);
        }

        return ArchivePathBuilder.BuildLocalRecordingArchivePath(
            archiveTarget,
            record.StartTime,
            Path.GetFileName(record.FilePath));
    }

    internal Task<bool> ArchiveRecordAsync(long recordId, CancellationToken cancellationToken)
    {
        VideoRecord? record = _database.GetVideoById(recordId);
        if (record == null)
            return Task.FromResult(false);
        return ArchiveRecordWithOwnershipAsync(record, cancellationToken);
    }

    private async Task<bool> TryArchiveAsync(
        VideoRecord record,
        CancellationToken cancellationToken,
        string? targetRoot = null)
    {
        if (record.ArchiveStatus is VideoArchiveStatus.Verified or VideoArchiveStatus.LocalDeleted)
            return true;

        string localPath = record.FilePath;
        string networkPath = record.ArchivePath;
        DateTime attemptedAt = DateTime.Now;
        if (!File.Exists(localPath))
        {
            // 本地缺失：统一判定（BackupLost / LocalMissingUnverified / 已确认 LocalDeleted），
            // 立即结束本轮，不进入 Provider 与重试逻辑。
            RemoteFileProbe.FileProbeState probe =
                string.IsNullOrWhiteSpace(networkPath)
                    ? RemoteFileProbe.FileProbeState.ConfirmedMissing
                    : RemoteFileProbe.TryProbeFileState(
                        networkPath,
                        record.FileSizeBytes,
                        TimeSpan.FromSeconds(3));
            LocalMissingRepair.Apply(_database, record, probe);
            return false;
        }

        networkPath = ResolveCurrentArchivePath(record, targetRoot);
        if (string.IsNullOrWhiteSpace(networkPath))
            return false;

        _database.UpdateArchiveState(record.Id, VideoArchiveStatus.Copying, attemptedAt: attemptedAt);
        try
        {
            long localSize = new FileInfo(localPath).Length;
            string sourceHash = string.IsNullOrWhiteSpace(record.ContentSha256)
                ? await ComputeSourceSha256Async(localPath, cancellationToken).ConfigureAwait(false)
                : record.ContentSha256;

            RemoteProbeResult probe = await WithTimeoutAsync(
                token => _provider.ProbeAsync(networkPath, localSize, token),
                cancellationToken).ConfigureAwait(false);
            switch (probe)
            {
                case RemoteProbeResult.ExistsSameSize:
                    string existingHash = await WithTimeoutAsync(
                        token => _provider.ComputeSha256Async(networkPath, token),
                        cancellationToken).ConfigureAwait(false);
                    RecordTargetReachable();
                    if (string.Equals(existingHash, sourceHash, StringComparison.OrdinalIgnoreCase))
                    {
                        _database.UpdateArchiveState(
                            record.Id,
                            VideoArchiveStatus.Verified,
                            contentSha256: sourceHash,
                            attemptedAt: attemptedAt,
                            completedAt: DateTime.Now,
                            lastProbeAt: DateTime.Now);
                        return true;
                    }
                    throw new ArchiveConflictException("网络目标已存在同名但内容不同的文件，已禁止覆盖");
                case RemoteProbeResult.ExistsDifferentSize:
                    throw new ArchiveConflictException("网络目标已存在同名但大小不同的文件，已禁止覆盖");
                case RemoteProbeResult.Error:
                    throw new IOException("无法探测网络目标状态");
            }

            // 复制是主要网络负载，不套 3 秒短超时：慢 NAS 大文件会持续在后台完成，
            // 超时误判会引发重复复制；探测与哈希仍走 WithTimeoutAsync。
            string attemptToken = $"{record.ArchiveRetryCount + 1}-"
                + Guid.NewGuid().ToString("N")[..8];
            await _provider.PublishFileAsync(
                    localPath,
                    networkPath,
                    record.Id,
                    sourceHash,
                    attemptToken,
                    cancellationToken).ConfigureAwait(false);
            RecordTargetReachable();

            _database.UpdateArchiveState(record.Id, VideoArchiveStatus.Verifying, attemptedAt: attemptedAt);
            string publishedHash = await _provider.ComputeSha256Async(
                networkPath,
                cancellationToken).ConfigureAwait(false);
            RecordTargetReachable();
            if (!string.Equals(publishedHash, sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                // 发布后校验失败：本地源仍保留，先把损坏目标改名 .corrupt（失败则仅记录），
                // 置 Failed(HashMismatch) 以便重试，不留下状态不明的文件。
                try
                {
                    await _provider.RenameAsync(
                        networkPath,
                        networkPath + ".corrupt",
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception renameEx)
                {
                    RuntimeLog.Warn(
                        "Archive",
                        $"Archive hash mismatch rename failed id={record.Id}, target={networkPath}, error={renameEx.Message}");
                }
                _database.UpdateArchiveState(
                    record.Id,
                    VideoArchiveStatus.Failed,
                    error: "HashMismatch：网络归档文件 SHA-256 校验失败",
                    attemptedAt: attemptedAt,
                    incrementRetry: true,
                    nextRetryAt: ComputeNextRetryAt(record.ArchiveRetryCount + 1));
                return false;
            }

            _database.UpdateArchiveState(
                record.Id,
                VideoArchiveStatus.Verified,
                contentSha256: sourceHash,
                attemptedAt: attemptedAt,
                completedAt: DateTime.Now,
                lastProbeAt: DateTime.Now);
            RuntimeLog.Info("Archive", $"Archive verified id={record.Id}, target={networkPath}");
            return true;
        }
        catch (ArchiveConflictException ex)
        {
            _database.UpdateArchiveState(
                record.Id,
                VideoArchiveStatus.Conflict,
                error: ex.Message,
                attemptedAt: attemptedAt);
            RuntimeLog.Warn("Archive", $"Archive conflict id={record.Id}, target={networkPath}, error={ex.Message}");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _database.UpdateArchiveState(
                record.Id,
                VideoArchiveStatus.Pending,
                error: "归档已暂停，等待下次继续",
                attemptedAt: attemptedAt);
            throw;
        }
        catch (TimeoutException ex)
        {
            _database.UpdateArchiveState(
                record.Id,
                VideoArchiveStatus.Failed,
                error: $"网络操作超时：{ex.Message}",
                attemptedAt: attemptedAt,
                incrementRetry: true,
                nextRetryAt: ComputeNextRetryAt(record.ArchiveRetryCount + 1));
            return false;
        }
        catch (Exception ex)
        {
            if (NetworkArchiveSpacePolicy.IsDiskFullException(ex))
            {
                string? alternateRoot = SelectUsableArchiveRoot(networkPath);
                if (alternateRoot != null)
                {
                    string newArchivePath = ArchivePathBuilder.BuildLocalRecordingArchivePath(
                        alternateRoot,
                        record.StartTime,
                        Path.GetFileName(networkPath));
                    _database.RerouteArchivePath(record.Id, newArchivePath);
                    _database.UpdateArchiveState(
                        record.Id,
                        VideoArchiveStatus.Pending,
                        error: "NAS 空间不足，已切换到下一个备份位置",
                        attemptedAt: attemptedAt);
                    RuntimeLog.Warn(
                        "Archive",
                        $"Archive rerouted id={record.Id}, target={alternateRoot}, error={ex.Message}");
                    return false;
                }

                _database.UpdateArchiveState(
                    record.Id,
                    VideoArchiveStatus.NASFull,
                    error: "所有备份位置空间不足，归档暂停，等待空间恢复",
                    attemptedAt: attemptedAt);
                RuntimeLog.Warn("Archive", $"Archive NAS full id={record.Id}, target={networkPath}, error={ex.Message}");
                return false;
            }

            DateTime? circuitRetryAt = NetworkArchiveErrorClassifier.IsTargetUnreachable(ex)
                ? RecordUnreachableFailure(
                    attemptedAt,
                    targetRoot ?? ResolveUnreachableRootKey(networkPath))
                : null;

            _database.UpdateArchiveState(
                record.Id,
                VideoArchiveStatus.Failed,
                error: ex.Message,
                attemptedAt: attemptedAt,
                incrementRetry: true,
                nextRetryAt: circuitRetryAt
                    ?? ComputeNextRetryAt(record.ArchiveRetryCount + 1));
            RuntimeLog.Warn("Archive", $"Archive failed id={record.Id}, target={networkPath}, error={ex.Message}");
            return false;
        }
    }

    private async Task<bool> ArchiveRecordWithOwnershipAsync(
        VideoRecord record,
        CancellationToken cancellationToken)
    {
        using IDisposable ownership = await VideoLifecycleCoordinator.EnterAsync(
            record.Id,
            cancellationToken);
        return await TryArchiveAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private Task<string> ComputeSourceSha256Async(
        string localPath,
        CancellationToken cancellationToken) =>
        _provider is NasArchiveProvider nas
            ? nas.ComputeSourceSha256Async(localPath, cancellationToken)
            : NasArchiveProvider.ComputeSha256FileAsync(localPath, cancellationToken);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_options.AutomaticWorkerEnabled)
                    await ProcessPendingOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Archive", $"Archive worker iteration failed: {ex.Message}");
            }

            try
            {
                await _wakeSignal.WaitAsync(
                    GetNextWorkerDelay(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<T> WithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        Task<T> task = action(cancellationToken);
        Task completed = await Task.WhenAny(task, Task.Delay(_options.RemoteTimeout, cancellationToken))
            .ConfigureAwait(false);
        if (completed != task)
            throw new TimeoutException($"超过 {_options.RemoteTimeout.TotalSeconds:F0} 秒");
        return await task.ConfigureAwait(false);
    }

    private static DateTime ComputeNextRetryAt(int retryCount)
    {
        double seconds = Math.Min(1800, 30 * Math.Pow(2, Math.Max(0, retryCount - 1)));
        return DateTime.Now.AddSeconds(seconds);
    }

    private bool IsCircuitOpen()
    {
        lock (_circuitSync)
            return _circuitRetryAfter > DateTime.Now;
    }

    private bool IsRecoveryThrottleActive()
    {
        lock (_recoverySync)
            return _recoveryMode;
    }

    private ArchiveLoadState ReadLoadState()
    {
        ArchiveLoadState state;
        try
        {
            state = _loadStateProvider();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Archive", $"读取实时负载状态失败：{ex.Message}");
            state = ArchiveLoadState.Degraded;
        }

        if (state == ArchiveLoadState.Paused)
        {
            SetWorkerSnapshot(new ArchiveWorkerSnapshot(
                ArchiveWorkerPhase.PausedForRecording));
        }
        else if (Volatile.Read(ref _archiveInProgress) != 0
            && CurrentWorkerSnapshot.Phase == ArchiveWorkerPhase.PausedForRecording)
        {
            SetWorkerSnapshot(new ArchiveWorkerSnapshot(ArchiveWorkerPhase.Uploading));
        }
        return state;
    }

    private void SetWorkerSnapshot(ArchiveWorkerSnapshot snapshot)
    {
        Action<ArchiveWorkerSnapshot>? handler;
        lock (_workerSnapshotSync)
        {
            if (_workerSnapshot == snapshot)
                return;
            _workerSnapshot = snapshot;
            handler = WorkerStateChanged;
        }

        try { handler?.Invoke(snapshot); }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Archive", $"归档状态通知失败：{ex.Message}");
        }
    }

    private TimeSpan GetNextWorkerDelay()
    {
        TimeSpan delay = _options.PollInterval;
        lock (_recoverySync)
        {
            if (!_recoveryMode || _recoveryNextRoundAt == default)
                return delay;

            TimeSpan recoveryDelay = _recoveryNextRoundAt - DateTime.Now;
            if (recoveryDelay <= TimeSpan.Zero)
                return TimeSpan.Zero;
            return delay == Timeout.InfiniteTimeSpan || recoveryDelay < delay
                ? recoveryDelay
                : delay;
        }
    }

    private void NotifyArchiveQueueChanged()
    {
        try { ArchiveQueueChanged?.Invoke(); }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Archive", $"归档队列通知失败：{ex.Message}");
        }
    }

    private void InitializeBacklogRecovery()
    {
        int threshold = _options.RecoveryBacklogThreshold > 0
            ? _options.RecoveryBacklogThreshold
            : Math.Max(1, _options.BatchSize);
        int failedCount;
        try
        {
            failedCount = _database.GetArchiveQueueSummary().FailedCount;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Archive", $"读取启动归档积压失败：{ex.Message}");
            return;
        }

        if (failedCount < threshold)
            return;

        lock (_recoverySync)
        {
            _recoveryMode = true;
            _recoveryBatchSize = Math.Clamp(
                Math.Max(1, _options.RecoveryInitialBatchSize),
                1,
                Math.Max(1, _options.RecoveryMaxBatchSize));
            _recoveryNextRoundAt = DateTime.Now;
        }
        RuntimeLog.Info(
            "Archive",
            $"检测到 {failedCount} 个失败积压，启动渐进恢复");
    }

    private string ResolveCurrentArchivePath(VideoRecord record, string? selectedRoot)
    {
        string currentPath = record.ArchivePath;
        if (record.ArchiveStatus is not (VideoArchiveStatus.Pending or VideoArchiveStatus.Failed)
            || string.IsNullOrWhiteSpace(selectedRoot))
        {
            return currentPath;
        }

        IReadOnlyList<StorageLocation>? candidates = GetArchiveCandidates();
        if (candidates == null || candidates.Count == 0)
            return currentPath;
        if (candidates.Any(location => IsPathUnderRoot(currentPath, location.Path)))
            return currentPath;

        string fileName = Path.GetFileName(record.FilePath);
        if (string.IsNullOrWhiteSpace(fileName))
            return currentPath;
        string newArchivePath = ArchivePathBuilder.BuildLocalRecordingArchivePath(
            selectedRoot,
            record.StartTime,
            fileName);
        int updated = _database.TryReroutePendingArchivePath(
            record.Id,
            currentPath,
            newArchivePath);
        if (updated == 1)
        {
            RuntimeLog.Info(
                "Archive",
                $"归档任务已改投当前备份位置 id={record.Id}, old={currentPath}, new={newArchivePath}");
            return newArchivePath;
        }

        VideoRecord? latest = _database.GetVideoById(record.Id);
        return latest is { ArchiveStatus: VideoArchiveStatus.Pending or VideoArchiveStatus.Failed }
            ? latest.ArchivePath
            : "";
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            return false;
        string normalizedPath = path.Trim().Replace('\\', '/').TrimEnd('/');
        string normalizedRoot = root.Trim().Replace('\\', '/').TrimEnd('/');
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(
                normalizedRoot + "/",
                StringComparison.OrdinalIgnoreCase);
    }

    private bool TryBeginRecoveryRound(out bool recoveryRound)
    {
        bool cooldownExpired;
        lock (_circuitSync)
        {
            if (_circuitRetryAfter > DateTime.Now)
            {
                recoveryRound = false;
                return false;
            }
            // 熔断时间已到但尚未完成第一次恢复探测：先进入恢复模式，
            // 让首条上传就使用 batch=1 与字节节流，避免大文件直接冲击实时路径。
            cooldownExpired = _circuitRetryAfter != default;
        }

        lock (_recoverySync)
        {
            if (cooldownExpired && !_recoveryMode)
            {
                _recoveryMode = true;
                _recoveryBatchSize = Math.Clamp(
                    Math.Max(1, _options.RecoveryInitialBatchSize),
                    1,
                    Math.Max(1, _options.RecoveryMaxBatchSize));
                _recoveryNextRoundAt = DateTime.Now;
            }
            recoveryRound = _recoveryMode;
            if (!recoveryRound)
                return true;
            if (_recoveryNextRoundAt > DateTime.Now)
            {
                SetWorkerSnapshot(new ArchiveWorkerSnapshot(
                    ArchiveWorkerPhase.WaitingForNextBatch,
                    _recoveryNextRoundAt));
                return false;
            }
            return true;
        }
    }

    private void CompleteRecoveryRound(bool recoveryRound, int selectedCount)
    {
        if (!recoveryRound)
            return;
        lock (_recoverySync)
        {
            if (!_recoveryMode)
                return;
            if (selectedCount == 0 || selectedCount < Math.Max(1, _recoveryBatchSize))
            {
                _recoveryMode = false;
                _recoveryBatchSize = 0;
                _recoveryNextRoundAt = default;
                RuntimeLog.Info("Archive", "NAS 恢复后的归档积压已完成渐进放量");
                SetWorkerSnapshot(new ArchiveWorkerSnapshot(ArchiveWorkerPhase.Idle));
                return;
            }
            int maxBatch = Math.Max(1, _options.RecoveryMaxBatchSize);
            _recoveryBatchSize = Math.Min(
                maxBatch,
                Math.Max(1, _recoveryBatchSize) * 2);
            _recoveryNextRoundAt = DateTime.Now + _options.RecoveryInterBatchDelay;
            SetWorkerSnapshot(new ArchiveWorkerSnapshot(
                ArchiveWorkerPhase.WaitingForNextBatch,
                _recoveryNextRoundAt));
        }
    }

    /// <summary>
    /// 没有解析出配置根时退化为共享根（UNC 的 \\server\share 或本地盘符），
    /// 保证同一备份目标下的连续失败可以正确累计。
    /// </summary>
    private static string ResolveUnreachableRootKey(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            if (!string.IsNullOrWhiteSpace(root))
                return root;
        }
        catch
        {
        }

        return path;
    }

    /// <summary>
    /// 记录一次“目标不可达”失败；同一根连续失败达到阈值后打开熔断，
    /// 冷却时间指数增长至上限，期间 Worker 不再发起网络探测。
    /// </summary>
    private DateTime? RecordUnreachableFailure(DateTime now, string root)
    {
        bool opened = false;
        TimeSpan cooldown = _options.UnreachableCooldown;
        DateTime retryAfter = default;
        lock (_circuitSync)
        {
            if (string.Equals(_unreachableRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                _consecutiveUnreachableFailures++;
            }
            else
            {
                _unreachableRoot = root;
                _consecutiveUnreachableFailures = 1;
                _nextUnreachableCooldown = _options.UnreachableCooldown;
            }

            int threshold = Math.Max(1, _options.UnreachableFailureThreshold);
            if (_consecutiveUnreachableFailures >= threshold)
            {
                opened = true;
                cooldown = _nextUnreachableCooldown;
                retryAfter = now + cooldown;
                _circuitRetryAfter = retryAfter;

                double baseSeconds = Math.Max(1, _options.UnreachableCooldown.TotalSeconds);
                double maxSeconds = Math.Max(
                    baseSeconds,
                    _options.MaxUnreachableCooldown.TotalSeconds);
                _nextUnreachableCooldown = TimeSpan.FromSeconds(
                    Math.Min(maxSeconds, cooldown.TotalSeconds * 2));
                _consecutiveUnreachableFailures = 0;
            }
        }

        lock (_recoverySync)
        {
            _recoveryMode = false;
            _recoveryBatchSize = 0;
            _recoveryNextRoundAt = default;
        }

        if (!opened)
            return null;

        RuntimeLog.Warn(
            "Archive",
            $"归档目标不可达，归档暂停约 {cooldown.TotalMinutes:F0} 分钟，等待网络位置恢复：{root}");
        BackupTargetAvailabilityChanged?.Invoke(false, root);
        return retryAfter;
    }

    /// <summary>
    /// 任一网络操作成功后调用：确认目标恢复可达并关闭熔断。
    /// 只有从“已熔断”切回可用时才触发事件与日志，正常连续成功不打扰。
    /// </summary>
    private void RecordTargetReachable()
    {
        bool wasOpen;
        lock (_circuitSync)
        {
            wasOpen = _circuitRetryAfter != default;
            _unreachableRoot = "";
            _consecutiveUnreachableFailures = 0;
            _circuitRetryAfter = default;
            _nextUnreachableCooldown = _options.UnreachableCooldown;
        }

        if (!wasOpen)
            return;

        lock (_recoverySync)
        {
            bool recoveryAlreadyStarted = _recoveryMode;
            _recoveryMode = true;
            if (!recoveryAlreadyStarted)
            {
                int initialBatchSize = Math.Clamp(
                    Math.Max(1, _options.RecoveryInitialBatchSize),
                    1,
                    Math.Max(1, _options.RecoveryMaxBatchSize));
                _recoveryBatchSize = Math.Min(
                    Math.Max(1, _options.RecoveryMaxBatchSize),
                    initialBatchSize * 2);
            }
            _recoveryNextRoundAt = DateTime.Now + _options.RecoveryInterBatchDelay;
        }

        RuntimeLog.Info("Archive", "归档目标已恢复可达，继续归档");
        BackupTargetAvailabilityChanged?.Invoke(true, "");
    }

    public void Dispose()
    {
        _cts.Cancel();
        Wake();
        try { _worker.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _wakeSignal.Dispose();
        if (_provider is IArchiveTransferThrottleAware throttleAware)
            throttleAware.SetTransferThrottle(null);
        _cts.Dispose();
    }
}
