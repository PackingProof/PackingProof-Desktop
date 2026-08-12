using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        private void ForceCheckDiskAndCleanup()
        {
            _ = Task.Run(() => RunDiskCleanupCore(forceFullScan: true));
        }

        private async Task CheckDiskAndCleanup()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                RunDiskCleanupCore(forceFullScan: false);
                int interval = IsRecording ? 10000 : 60000;
                try { await Task.Delay(interval, _cts.Token); } catch { break; }
            }
        }

        private int _diskCleanupRunning;
        private int _manualCleanupRunning;
        private DateTime _lastFullDiskCleanup = DateTime.MinValue;
        private DateTime _lastNetworkArchiveSpaceWarnAt = DateTime.MinValue;
        private DateTime _lastUnarchivedCleanupWarnAt = DateTime.MinValue;
        private DateTime _lastNasReconcileAt = DateTime.MinValue;
        private long _lastKnownDiskTotalBytes;
        private long _lastKnownDiskCapacityBytes;
        private NasCircularCleanupService? _nasCircularCleanup;
        private static readonly TimeSpan NasReconcileInterval = TimeSpan.FromDays(1);

        private void RunDiskCleanupCore(bool forceFullScan)
        {
            if (Interlocked.Exchange(ref _diskCleanupRunning, 1) == 1) return;
            try
            {
                if (Volatile.Read(ref _manualCleanupRunning) != 0)
                    return; // 手动清理期间暂停自动 GC

                if (IsRecordingWorkstation)
                {
                    RecordingCacheMaintenanceResult result =
                        RunRecordingCacheMaintenance(
                            RecordingWorkstationCachePolicy
                                .RecordingAndPackagingHeadroomBytes);
                    if (IsRecording
                        && (!result.IsAvailable
                            || result.Snapshot.RemainingBytes
                            < RecordingWorkstationCachePolicy
                                .HardStopHeadroomBytes))
                    {
                        QueueRecordingCacheEmergencyStop();
                    }
                    return;
                }

                if (Config.StorageLocations == null || Config.StorageLocations.Count == 0) return;

                bool fullScan = forceFullScan
                    || _lastFullDiskCleanup == DateTime.MinValue
                    || (DateTime.Now - _lastFullDiskCleanup).TotalSeconds >= (IsRecording ? 60 : 180);

                long totalCurrentBytes = fullScan ? 0 : _lastKnownDiskTotalBytes;
                long totalCapacityBytes = fullScan ? 0 : _lastKnownDiskCapacityBytes;

                if (fullScan)
                {
                    var scannedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var loc in Config.StorageLocations)
                    {
                        if (string.IsNullOrWhiteSpace(loc.Path)) continue;
                        string normalizedPath = Path.IsPathRooted(loc.Path) ? loc.Path : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, loc.Path);
                        if (StorageVolumeInfo.IsNetworkPath(normalizedPath)) continue;
                        if (!Directory.Exists(normalizedPath)) continue;

                        long storageCapacity = 0;
                        try
                        {
                            if (StorageVolumeInfo.TryGet(normalizedPath, out StorageVolumeInfo volume)
                                && scannedRoots.Add(volume.RootPath))
                            {
                                long locVideoBytes = 0;
                                foreach (var fi in EnumerateVideoFiles(normalizedPath))
                                    locVideoBytes += fi.Length;
                                totalCurrentBytes += locVideoBytes;
                                long reserveBytes = StorageSpacePolicy.GetEffectiveReserveBytes(loc, volume);
                                storageCapacity = Math.Max(0, volume.AvailableFreeSpace - reserveBytes) + locVideoBytes;
                            }
                        }
                        catch { }
                        totalCapacityBytes += storageCapacity;
                    }

                    _lastFullDiskCleanup = DateTime.Now;
                    _lastKnownDiskTotalBytes = totalCurrentBytes;
                    _lastKnownDiskCapacityBytes = totalCapacityBytes;
                }

                if (IsRecording && !string.IsNullOrEmpty(_currentVideoFilePath))
                {
                    try
                    {
                        if (File.Exists(_currentVideoFilePath))
                            totalCurrentBytes += new FileInfo(_currentVideoFilePath).Length;
                    }
                    catch { }
                }

                long releasedByNormalCleanup = 0;
                if (fullScan && totalCapacityBytes > 0 && totalCurrentBytes > totalCapacityBytes)
                    releasedByNormalCleanup = CleanupOldVideos(totalCurrentBytes, totalCapacityBytes);

                TryEmergencyUnarchivedCleanup(
                    totalCurrentBytes,
                    totalCapacityBytes,
                    releasedByNormalCleanup);
                CheckNetworkArchiveSpace();
                RunNasReconcileIfDue();
                RefreshArchiveBackupSummary();

                UpdateDiskUsageText(totalCurrentBytes, totalCapacityBytes);
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _diskCleanupRunning, 0);
            }
        }

        /// <summary>
        /// 网络归档空间检查（高频、只查真实卷空间，不做文件探测）：
        /// 对每个低于预留值的网络位置先执行循环清理，仍不足才限频提示；
        /// 空间恢复后把 NASFull 记录重新置为等待归档。
        /// </summary>
        private void CheckNetworkArchiveSpace()
        {
            try
            {
                if (Config.StorageLocations == null)
                    return;

                string? firstNetworkPath = null;
                bool anyUsable = false;
                bool anyBelowReserve = false;
                foreach (StorageLocation location in Config.StorageLocations)
                {
                    if (string.IsNullOrWhiteSpace(location.Path))
                        continue;
                    string normalizedPath = Path.IsPathRooted(location.Path)
                        ? location.Path
                        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, location.Path);
                    if (!StorageVolumeInfo.IsNetworkPath(normalizedPath))
                        continue;
                    firstNetworkPath ??= normalizedPath;
                    if (!StorageVolumeInfo.TryGet(normalizedPath, out StorageVolumeInfo volume))
                        continue; // 离线/不可达：继续看下一个
                    long reserveBytes = StorageSpacePolicy.GetEffectiveReserveBytes(
                        location,
                        volume);
                    if (NetworkArchiveSpacePolicy.IsBelowReserve(
                            volume.AvailableFreeSpace,
                            reserveBytes))
                    {
                        _nasCircularCleanup?.RunForRoot(normalizedPath, reserveBytes);
                        if (StorageVolumeInfo.TryGet(
                                normalizedPath,
                                out StorageVolumeInfo afterCleanup)
                            && afterCleanup.AvailableFreeSpace > reserveBytes)
                        {
                            anyUsable = true;
                        }
                        else
                        {
                            anyBelowReserve = true;
                        }
                        continue;
                    }
                    anyUsable = true;
                }
                if (string.IsNullOrWhiteSpace(firstNetworkPath))
                    return;

                if (anyBelowReserve && !anyUsable)
                {
                    DateTime now = DateTime.Now;
                    if (!NetworkArchiveSpacePolicy.ShouldWarn(
                            _lastNetworkArchiveSpaceWarnAt,
                            now))
                    {
                        return;
                    }

                    _lastNetworkArchiveSpaceWarnAt = now;
                    RuntimeLog.Warn(
                        "Cleanup",
                        $"All network archive locations below reserve path={firstNetworkPath}");
                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_isDisposed)
                            return;
                        ShowToast(
                            "NAS 空间不足，已循环清理最旧归档后仍不足，请清理 NAS 或调整备份位置",
                            ToastSeverity.Warning);
                    });
                    return;
                }

                int released = _db?.ReleaseNasFullRecords() ?? 0;
                if (released > 0)
                {
                    RuntimeLog.Info(
                        "Cleanup",
                        $"Network archive space recovered, requeued NASFull records count={released}");
                    _archiveService?.Wake();
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Cleanup", $"Network archive space check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 低频 NAS 归档缺失对账：启动后首次 GC 与每日一次；只对明确不存在的归档修复状态，
        /// 不可达保持原状态。空间检查不触发对账，两者互不耦合。
        /// </summary>
        private void RunNasReconcileIfDue()
        {
            DateTime now = DateTime.Now;
            if (_lastNasReconcileAt != DateTime.MinValue
                && now - _lastNasReconcileAt < NasReconcileInterval)
            {
                return;
            }
            _lastNasReconcileAt = now;
            int repaired = _nasCircularCleanup?.RunReconcileBatch(200) ?? 0;
            if (repaired > 0)
            {
                RuntimeLog.Info(
                    "Cleanup",
                    $"NAS reconcile repaired count={repaired}");
            }
        }

        /// <summary>设置页手动清理预览：按当前配置的全部本地存储位置统计。</summary>
        public Task<ManualCleanupPreview> PreviewManualCleanupAsync(
            ManualCleanupOptions options)
        {
            if (_db == null)
            {
                return Task.FromException<ManualCleanupPreview>(
                    new InvalidOperationException("数据库不可用，无法清理"));
            }
            var service = new ManualCleanupService(_db);
            IReadOnlyList<string> roots = GetManagedLocalRoots();
            return Task.Run(() => service.Preview(options, roots));
        }

        /// <summary>
        /// 设置页手动清理：两阶段执行（先已备份副本，再按确认清理未备份录像），
        /// 执行期间暂停自动 GC。
        /// </summary>
        public Task<ManualCleanupResult> RunManualCleanupAsync(
            ManualCleanupOptions options,
            Func<ManualCleanupPrompt, bool> unarchivedDecider)
        {
            if (_db == null)
            {
                return Task.FromException<ManualCleanupResult>(
                    new InvalidOperationException("数据库不可用，无法清理"));
            }
            if (Interlocked.CompareExchange(ref _manualCleanupRunning, 1, 0) != 0)
            {
                return Task.FromException<ManualCleanupResult>(
                    new InvalidOperationException("手动清理正在进行，请稍后再试"));
            }

            var service = new ManualCleanupService(_db);
            IReadOnlyList<string> roots = GetManagedLocalRoots();
            return Task.Run(() =>
            {
                try
                {
                    return service.Run(options, roots, unarchivedDecider);
                }
                finally
                {
                    Interlocked.Exchange(ref _manualCleanupRunning, 0);
                    ForceCheckDiskAndCleanup();
                }
            });
        }

        private IReadOnlyList<string> GetManagedLocalRoots()
        {
            var roots = new List<string>();
            if (Config.StorageLocations == null)
                return roots;
            foreach (StorageLocation location in Config.StorageLocations)
            {
                if (string.IsNullOrWhiteSpace(location.Path))
                    continue;
                string normalized = Path.IsPathRooted(location.Path)
                    ? location.Path
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, location.Path);
                if (StorageVolumeInfo.IsNetworkPath(normalized))
                    continue;
                roots.Add(normalized);
            }
            return roots;
        }

        /// <summary>
        /// 硬循环兜底（最后降级策略）：正常 GC 后仍无法满足保留要求、
        /// 本地主存储可用空间低于保护线、且没有归档目标或归档目标不可达时，
        /// 删除超过保护期的最旧未归档录像。
        /// Conflict 不参与；NAS 可达时仅唤醒归档并跳过本轮。
        /// </summary>
        private void TryEmergencyUnarchivedCleanup(
            long totalCurrentBytes,
            long totalCapacityBytes,
            long releasedByNormalCleanup)
        {
            try
            {
                string workingRoot;
                try
                {
                    workingRoot = StorageLocationResolver.Resolve(
                        Config,
                        allowDefaultFallback: false);
                }
                catch
                {
                    return;
                }
                if (!StorageVolumeInfo.TryGet(workingRoot, out StorageVolumeInfo volume))
                    return;

                long reserveBytes = StorageSpacePolicy.GetEffectiveReserveBytes(
                    new StorageLocation { Path = workingRoot, ReserveGB = 0 },
                    volume);
                if (volume.AvailableFreeSpace >= reserveBytes)
                    return; // 仍满足存储空间策略保留要求
                if (volume.AvailableFreeSpace
                    >= LocalCopyCleanupPolicy.EmergencyCleanupThresholdBytes)
                {
                    return; // 未触及 5GiB 保护线
                }

                string? archiveRoot = GetConfiguredArchiveRoot();
                long bytesToRelease = LocalCopyCleanupPolicy.ComputeEmergencyReleaseTarget(
                    totalCurrentBytes,
                    totalCapacityBytes,
                    releasedByNormalCleanup);
                if (string.IsNullOrWhiteSpace(archiveRoot))
                {
                    // 没有配置网络归档目标时按“不可达”处理，执行硬循环删除。
                    long releasedBytes = EmergencyCleanupUnarchived(bytesToRelease);
                    EmergencyCleanupVerifiedLocalCopies(bytesToRelease, ref releasedBytes);
                    return;
                }

                bool archiveReachable =
                    RemoteFileProbe.TryProbeDirectoryState(
                        archiveRoot,
                        TimeSpan.FromSeconds(3))
                    == RemoteFileProbe.DirectoryProbeState.Reachable;
                if (!LocalCopyCleanupPolicy.ShouldTriggerEmergencyCleanup(
                        archiveRoot,
                        archiveReachable))
                {
                    _archiveService?.Wake(); // NAS 可达：优先归档而不是删除
                    return;
                }

                long emergencyReleased = EmergencyCleanupUnarchived(bytesToRelease);
                EmergencyCleanupVerifiedLocalCopies(bytesToRelease, ref emergencyReleased);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Cleanup", $"Emergency cleanup check failed: {ex.Message}");
            }
        }

        /// <summary>硬循环删除未归档本地录像（Failed → Pending → LocalOnly → NasDeleted），返回释放字节数。</summary>
        private long EmergencyCleanupUnarchived(long bytesToRelease)
        {
            if (bytesToRelease <= 0 || _db == null)
                return 0;

            DateTime cutoff = DateTime.Now - LocalCopyCleanupPolicy.EmergencyDeleteGracePeriod;
            long releasedBytes = 0;
            int count = DeleteUnarchivedForCapacity(
                bytesToRelease,
                ref releasedBytes,
                cutoff,
                "硬循环清理（本地空间不足）",
                RecordingDeletionReasonCode.CapacityEmergencyCleanupUnarchived);

            if (count > 0)
            {
                _lastKnownDiskTotalBytes = Math.Max(0, _lastKnownDiskTotalBytes - releasedBytes);
                RuntimeLog.Warn(
                    "Cleanup",
                    $"Emergency cleanup deleted unarchived count={count}, bytes={releasedBytes}");
                ShowUnarchivedCleanupWarning(count);
            }
            return releasedBytes;
        }

        /// <summary>
        /// 硬循环补充：NAS 不可达且远端确认已过期时，允许按本地策略删除 Verified 本地副本，
        /// 保证 5GiB 保护线不被历史 Verified 撑破；Unavailable 不改归档状态，
        /// 删除时打未确认原因码，由对账兜底校正。
        /// </summary>
        private void EmergencyCleanupVerifiedLocalCopies(
            long bytesToRelease,
            ref long releasedBytes)
        {
            if (_db == null || bytesToRelease <= 0)
                return;

            DateTime now = DateTime.Now;
            foreach (VideoRecord video in _db.GetOldestVideos(500))
            {
                try
                {
                    if (releasedBytes >= bytesToRelease)
                        return;
                    if (video.ArchiveStatus != VideoArchiveStatus.Verified
                        || string.IsNullOrWhiteSpace(video.FilePath)
                        || !File.Exists(video.FilePath))
                    {
                        continue;
                    }
                    if (!LocalCopyCleanupPolicy.IsRemoteConfirmationStale(video, now))
                        continue; // 远端确认仍新鲜：继续保留本地副本

                    long size;
                    bool deleted;
                    using (VideoLifecycleCoordinator.EnterAsync(
                               video.Id,
                               CancellationToken.None).GetAwaiter().GetResult())
                    {
                        deleted = TryDeleteLocalCopyUnderLock(video, now, out size);
                    }
                    if (deleted)
                    {
                        releasedBytes += size;
                        _lastKnownDiskTotalBytes = Math.Max(
                            0,
                            _lastKnownDiskTotalBytes - size);
                    }
                }
                catch { }
            }
        }

        private string? GetConfiguredArchiveRoot()
        {
            if (Config.StorageLocations == null)
                return null;
            return Config.StorageLocations
                .Where(location => StorageVolumeInfo.IsNetworkPath(location.Path))
                .Select(location => Path.IsPathRooted(location.Path)
                    ? location.Path
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, location.Path))
                .FirstOrDefault();
        }

        /// <summary>
        /// 按分档顺序删除未归档本地录像：Failed → Pending → LocalOnly，档内最旧优先。
        /// 删除文件与数据库标记在同一所有权锁内完成。
        /// </summary>
        private int DeleteUnarchivedForCapacity(
            long bytesToRelease,
            ref long releasedBytes,
            DateTime cutoff,
            string reason,
            string reasonCode)
        {
            if (_db == null)
                return 0;
            int count = 0;
            foreach (string status in LocalCopyCleanupPolicy.UnarchivedCleanupTiers)
            {
                foreach (VideoRecord record in _db.GetEmergencyCleanupCandidates(
                             cutoff,
                             200,
                             status))
                {
                    try
                    {
                        if (releasedBytes >= bytesToRelease)
                            return count;
                        if (string.IsNullOrWhiteSpace(record.FilePath)
                            || !File.Exists(record.FilePath))
                        {
                            RemoteFileProbe.FileProbeState probe =
                                string.IsNullOrWhiteSpace(record.ArchivePath)
                                    ? RemoteFileProbe.FileProbeState.ConfirmedMissing
                                    : RemoteFileProbe.TryProbeFileState(
                                        record.ArchivePath,
                                        record.FileSizeBytes,
                                        TimeSpan.FromSeconds(3));
                            LocalMissingRepair.Apply(_db, record, probe);
                            continue;
                        }
                        if (!LocalCopyCleanupPolicy.IsEligibleForEmergencyCleanup(
                                record,
                                DateTime.Now,
                                out _))
                        {
                            continue;
                        }

                        long size = new FileInfo(record.FilePath).Length;
                        using (VideoLifecycleCoordinator.EnterAsync(
                                   record.Id,
                                   CancellationToken.None).GetAwaiter().GetResult())
                        {
                            if (!File.Exists(record.FilePath))
                                continue;
                            File.Delete(record.FilePath);
                            try
                            {
                                if (record.ArchiveStatus == VideoArchiveStatus.NasDeleted)
                                {
                                    _db.MarkNasCleanedRecordDeleted(
                                        record.Id,
                                        record.ArchivePath,
                                        reason,
                                        reasonCode);
                                }
                                else
                                {
                                    _db.MarkVideoDeleted(record.FilePath, reason, reasonCode);
                                }
                            }
                            catch (Exception dbEx)
                            {
                                RuntimeLog.Warn(
                                    "Cleanup",
                                    $"Unarchived cleanup DB mark failed id={record.Id}, error={dbEx.Message}");
                            }
                        }
                        releasedBytes += size;
                        count++;
                    }
                    catch { }
                }
            }
            return count;
        }

        /// <summary>未归档录像删除提示节流：日志全量记录，UI 警告受冷却时间限制。</summary>
        private void ShowUnarchivedCleanupWarning(int count)
        {
            DateTime now = DateTime.Now;
            if (now - _lastUnarchivedCleanupWarnAt
                < LocalCopyCleanupPolicy.UnarchivedCleanupWarningCooldown)
            {
                return;
            }
            _lastUnarchivedCleanupWarnAt = now;
            _ = Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_isDisposed)
                    return;
                ShowToast(
                    $"NAS 不可用或未配置，已删除最旧未归档录像 {count} 条",
                    ToastSeverity.Warning);
            });
        }

        private void QueueRecordingCacheEmergencyStop()
        {
            if (Interlocked.Exchange(
                    ref _recordingCacheEmergencyStopRequested,
                    1) != 0)
            {
                return;
            }

            _ = Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                if (_isDisposed || !IsRecording)
                    return;
                _stopReason = "本地缓存空间不足";
                RecordingCacheStatusText =
                    "本地缓存已接近磁盘安全线，正在安全停止当前录像";
                IsRecordingCacheWarning = true;
                ShowToast("本地缓存空间不足，正在安全保存当前录像", ToastSeverity.Warning);
                SpeakWarning(DefaultSpeechCatalog.StoragePathNotWritable);
                await SafeStopRecordingAsync(isManual: false, mergeAfterStop: true);
            });
        }

        private long CleanupOldVideos(long totalCurrentBytes, long totalCapacityBytes)
        {
            long bytesToRelease = totalCurrentBytes - (long)(totalCapacityBytes * 0.9);
            if (bytesToRelease <= 0) return 0;
            long releasedBytes = 0;
            int count = 0;
            int skippedUnverified = 0;
            DateTime now = DateTime.Now;

            var oldestRecords = _db?.GetOldestVideos(500);
            if (oldestRecords != null)
            {
                foreach (var video in oldestRecords)
                {
                    try
                    {
                        if (releasedBytes >= bytesToRelease) break;

                        if (!LocalCopyCleanupPolicy.IsEligibleForCapacityCleanup(video, now, out _))
                        {
                            if (video.ArchiveStatus is VideoArchiveStatus.LocalOnly
                                or VideoArchiveStatus.Pending
                                or VideoArchiveStatus.Copying
                                or VideoArchiveStatus.Verifying)
                            {
                                skippedUnverified++;
                            }
                            continue;
                        }

                        long size;
                        bool deleted;
                        using (VideoLifecycleCoordinator.EnterAsync(
                                   video.Id,
                                   CancellationToken.None).GetAwaiter().GetResult())
                        {
                            deleted = TryDeleteLocalCopyUnderLock(video, now, out size);
                        }
                        if (deleted)
                        {
                            releasedBytes += size;
                            count++;
                        }
                    }
                    catch { }
                }
            }

            int unarchivedCount = 0;
            if (releasedBytes < bytesToRelease)
            {
                string? archiveRoot = GetConfiguredArchiveRoot();
                RemoteFileProbe.DirectoryProbeState archiveState =
                    string.IsNullOrWhiteSpace(archiveRoot)
                        ? RemoteFileProbe.DirectoryProbeState.Unreachable
                        : RemoteFileProbe.TryProbeDirectoryState(
                            archiveRoot,
                            TimeSpan.FromSeconds(3));
                if (LocalCopyCleanupPolicy.ShouldFallbackToUnarchivedCleanup(
                        archiveRoot,
                        archiveState))
                {
                    unarchivedCount = DeleteUnarchivedForCapacity(
                        bytesToRelease,
                        ref releasedBytes,
                        now - LocalCopyCleanupPolicy.EmergencyDeleteGracePeriod,
                        "容量清理（NAS 不可用或未配置）",
                        RecordingDeletionReasonCode.CapacityCleanupUnarchived);
                }
            }

            if (skippedUnverified > 0)
            {
                RuntimeLog.Warn(
                    "Cleanup",
                    $"Capacity cleanup skipped unverified local copies count={skippedUnverified}");
            }

            if (count > 0 || unarchivedCount > 0)
            {
                _lastKnownDiskTotalBytes = Math.Max(0, _lastKnownDiskTotalBytes - releasedBytes);
                if (count > 0)
                {
                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_isDisposed) return;
                        ShowToast($"清理：已从多盘回收 {count} 个旧视频");
                        RefreshTodayStats();
                    });
                }
                if (unarchivedCount > 0)
                {
                    RuntimeLog.Warn(
                        "Cleanup",
                        $"Capacity cleanup deleted unarchived count={unarchivedCount}");
                    ShowUnarchivedCleanupWarning(unarchivedCount);
                }
            }
            return releasedBytes;
        }

        /// <summary>
        /// 在所有权锁内处理单条本地容量清理候选（调用方必须已持有 VideoLifecycleCoordinator 锁）。
        /// NasDeleted：无需远端确认，删除本地后终态化；
        /// Verified：确认新鲜直接删；确认过期时三态探测——Exists 确认后删、
        /// ConfirmedMissing 先对账再按结果继续、Unavailable 仅在确认过期时允许未确认删除。
        /// </summary>
        private bool TryDeleteLocalCopyUnderLock(
            VideoRecord snapshot,
            DateTime now,
            out long releasedBytes)
        {
            releasedBytes = 0;
            VideoRecord? record = _db?.GetVideoById(snapshot.Id);
            if (record == null
                || record.IsDeleted
                || string.IsNullOrWhiteSpace(record.FilePath)
                || !File.Exists(record.FilePath))
            {
                return false;
            }

            if (record.ArchiveStatus == VideoArchiveStatus.NasDeleted)
            {
                long size = new FileInfo(record.FilePath).Length;
                File.Delete(record.FilePath);
                try
                {
                    _db?.MarkNasCleanedRecordDeleted(
                        record.Id,
                        record.ArchivePath,
                        "本地循环清理（NAS 副本已循环清理）",
                        RecordingDeletionReasonCode.NasCapacityCleanup);
                }
                catch (Exception dbEx)
                {
                    RuntimeLog.Warn(
                        "Cleanup",
                        $"NasDeleted local cleanup DB mark failed id={record.Id}, error={dbEx.Message}");
                }
                releasedBytes = size;
                return true;
            }

            if (record.ArchiveStatus != VideoArchiveStatus.Verified)
                return false;

            if (!LocalCopyCleanupPolicy.ShouldProbeBeforeLocalCleanup(record, now))
            {
                return DeleteVerifiedLocalCopy(
                    record,
                    "全局配额清理",
                    RecordingDeletionReasonCode.CapacityCleanupVerified,
                    out releasedBytes);
            }

            switch (RemoteFileProbe.TryProbeFileState(
                        record.ArchivePath,
                        record.FileSizeBytes,
                        TimeSpan.FromSeconds(3)))
            {
                case RemoteFileProbe.FileProbeState.Exists
                    when ArchiveBackupStatePolicy.HasCurrentTrustedRemoteCopy(
                        record,
                        RemoteFileProbe.FileProbeState.Exists):
                    _db?.UpdateLastArchiveProbeAt(record.Id, DateTime.Now);
                    return DeleteVerifiedLocalCopy(
                        record,
                        "全局配额清理",
                        RecordingDeletionReasonCode.CapacityCleanupVerified,
                        out releasedBytes);
                case RemoteFileProbe.FileProbeState.Exists:
                    return false; // 存在但无完成证据：不可进入已确认删除语义
                case RemoteFileProbe.FileProbeState.ConfirmedMissing:
                    // 明确不存在：先对账（调用方已持锁），本地仍在 → NasDeleted，本轮继续清理本地副本
                    if (_nasCircularCleanup?.ReconcileRecordCore(record) == true)
                    {
                        VideoRecord? after = _db?.GetVideoById(record.Id);
                        if (after != null
                            && !after.IsDeleted
                            && after.ArchiveStatus == VideoArchiveStatus.NasDeleted
                            && !string.IsNullOrWhiteSpace(after.FilePath)
                            && File.Exists(after.FilePath))
                        {
                            long size = new FileInfo(after.FilePath).Length;
                            File.Delete(after.FilePath);
                            try
                            {
                                _db?.MarkNasCleanedRecordDeleted(
                                    after.Id,
                                    after.ArchivePath,
                                    "本地循环清理（NAS 副本已循环清理）",
                                    RecordingDeletionReasonCode.NasCapacityCleanup);
                            }
                            catch (Exception dbEx)
                            {
                                RuntimeLog.Warn(
                                    "Cleanup",
                                    $"Reconciled NasDeleted local cleanup DB mark failed id={after.Id}, error={dbEx.Message}");
                            }
                            releasedBytes = size;
                            return true;
                        }
                    }
                    return false;
                default:
                    // Unavailable：确认过期才允许未确认删除；不改归档状态，由对账兜底校正
                    if (!LocalCopyCleanupPolicy.IsRemoteConfirmationStale(record, now))
                        return false;
                    RuntimeLog.Warn(
                        "Cleanup",
                        $"Local cleanup without remote confirmation id={record.Id}, path={record.ArchivePath}");
                    return DeleteVerifiedLocalCopy(
                        record,
                        "全局配额清理（NAS 不可达，本地副本未经远端确认）",
                        RecordingDeletionReasonCode.CapacityCleanupUnconfirmedRemote,
                        out releasedBytes);
            }
        }

        private bool DeleteVerifiedLocalCopy(
            VideoRecord record,
            string reason,
            string reasonCode,
            out long releasedBytes)
        {
            releasedBytes = 0;
            if (string.IsNullOrWhiteSpace(record.FilePath)
                || !File.Exists(record.FilePath))
            {
                return false;
            }
            long size = new FileInfo(record.FilePath).Length;
            File.Delete(record.FilePath);
            try
            {
                _db?.MarkLocalCopyDeleted(record.Id, reason, reasonCode);
            }
            catch (Exception dbEx)
            {
                RuntimeLog.Warn(
                    "Cleanup",
                    $"Capacity cleanup DB mark failed id={record.Id}, error={dbEx.Message}");
            }
            releasedBytes = size;
            return true;
        }

        private void UpdateDiskUsageText(long totalCurrentBytes, long totalCapacityBytes)
        {
            double totalUsedGB = totalCurrentBytes / 1073741824.0;
            double totalCapacityGB = totalCapacityBytes / 1073741824.0;
            string estimateText = "";
            try
            {
                var (dbTotalBytes, dbTotalSec) = _db?.GetGlobalSizeAndDuration() ?? (0, 0);
                if (dbTotalBytes > 0 && dbTotalSec > 0)
                {
                    double bytesPerSec = dbTotalBytes / dbTotalSec;
                    if (totalCapacityBytes > 0)
                    {
                        double retentionHours = totalCapacityBytes / bytesPerSec / 3600.0;
                        estimateText = retentionHours >= 1
                            ? $"，预计循环可录 {retentionHours:F0} 小时"
                            : $"，预计循环可录 {retentionHours * 60:F0} 分钟";
                    }
                }
            }
            catch { }

            _ = Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_isDisposed) return;
                DiskUsagePercent = totalCapacityGB > 0 ? Math.Min(100.0, (totalUsedGB / totalCapacityGB) * 100.0) : 0;
                DiskUsageText = $"{totalUsedGB:F1} / {totalCapacityGB:F1} GB{estimateText}";
            });
        }

        private static readonly string[] _videoExtensions = [".mkv", ".mp4"];

        private static IEnumerable<FileInfo> EnumerateVideoFiles(string folderPath)
        {
            var dir = new DirectoryInfo(folderPath);
            if (!dir.Exists) yield break;
            foreach (var file in dir.EnumerateFiles("*.*", SearchOption.AllDirectories))
            {
                if (_videoExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
                    yield return file;
            }
        }
    }
}
