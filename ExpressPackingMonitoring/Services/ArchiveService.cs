using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Logging;
using System.IO;

namespace ExpressPackingMonitoring.Services;

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
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly Task _worker;
    private static readonly TimeSpan BackfillInterval = TimeSpan.FromMinutes(5);
    private DateTime _lastBackfillAt = DateTime.MinValue;

    public ArchiveService(
        VideoDatabase database,
        IArchiveProvider provider,
        ArchiveWorkerOptions? options = null,
        Func<IReadOnlyList<StorageLocation>>? archiveTargetResolver = null,
        CancellationToken cancellationToken = default)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? new ArchiveWorkerOptions();
        _archiveTargetResolver = archiveTargetResolver;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Wake()
    {
        try
        {
            _lastBackfillAt = DateTime.MinValue; // 唤醒后立即允许历史回填扫描
            if (_wakeSignal.CurrentCount == 0)
                _wakeSignal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>处理一轮待归档记录（供测试与手动触发使用）。</summary>
    internal async Task<int> ProcessPendingOnceAsync(CancellationToken cancellationToken)
    {
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
            BackfillHistoricalArchives(archiveTarget);
        }

        int completed = 0;
        foreach (VideoRecord record in _database.GetPendingArchives(
                     _options.BatchSize,
                     DateTime.Now))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using IDisposable ownership = await VideoLifecycleCoordinator.EnterAsync(
                record.Id,
                cancellationToken);
            if (await TryArchiveAsync(record, cancellationToken).ConfigureAwait(false))
                completed++;
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
    /// 受 5 分钟间隔限制，Wake 时立即允许重扫；本地文件已不存在的记录跳过。
    /// </summary>
    private void BackfillHistoricalArchives(string archiveTarget)
    {
        if (DateTime.UtcNow - _lastBackfillAt < BackfillInterval)
            return;
        _lastBackfillAt = DateTime.UtcNow;

        int updated = 0;
        foreach (VideoRecord record in _database.GetBackfillCandidates(200))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(record.FilePath) || !File.Exists(record.FilePath))
                    continue;
                string archivePath = ArchivePathBuilder.BuildLocalRecordingArchivePath(
                    archiveTarget,
                    record.StartTime,
                    Path.GetFileName(record.FilePath));
                updated += _database.SetArchiveTarget(record.Id, archivePath);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Archive", $"Backfill failed id={record.Id}, error={ex.Message}");
            }
        }
        if (updated > 0)
            RuntimeLog.Info("Archive", $"Backfilled historical archive paths count={updated}");
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
        CancellationToken cancellationToken)
    {
        if (record.ArchiveStatus is VideoArchiveStatus.Verified or VideoArchiveStatus.LocalDeleted)
            return true;

        string localPath = record.FilePath;
        string networkPath = record.ArchivePath;
        DateTime attemptedAt = DateTime.Now;
        if (!File.Exists(localPath))
        {
            _database.UpdateArchiveState(
                record.Id,
                VideoArchiveStatus.Failed,
                error: "本地录像文件不存在，无法归档",
                attemptedAt: attemptedAt,
                incrementRetry: true,
                nextRetryAt: ComputeNextRetryAt(record.ArchiveRetryCount + 1));
            return false;
        }

        _database.UpdateArchiveState(record.Id, VideoArchiveStatus.Copying, attemptedAt: attemptedAt);
        try
        {
            long localSize = new FileInfo(localPath).Length;
            string sourceHash = string.IsNullOrWhiteSpace(record.ContentSha256)
                ? await NasArchiveProvider.ComputeSha256FileAsync(
                    localPath,
                    cancellationToken).ConfigureAwait(false)
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

            _database.UpdateArchiveState(record.Id, VideoArchiveStatus.Verifying, attemptedAt: attemptedAt);
            string publishedHash = await _provider.ComputeSha256Async(
                networkPath,
                cancellationToken).ConfigureAwait(false);
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

            _database.UpdateArchiveState(
                record.Id,
                VideoArchiveStatus.Failed,
                error: ex.Message,
                attemptedAt: attemptedAt,
                incrementRetry: true,
                nextRetryAt: ComputeNextRetryAt(record.ArchiveRetryCount + 1));
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
                await _wakeSignal.WaitAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false);
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

    public void Dispose()
    {
        _cts.Cancel();
        Wake();
        try { _worker.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _wakeSignal.Dispose();
        _cts.Dispose();
    }
}
