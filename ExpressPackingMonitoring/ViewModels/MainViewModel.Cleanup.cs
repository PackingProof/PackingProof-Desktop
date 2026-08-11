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
        private DateTime _lastFullDiskCleanup = DateTime.MinValue;
        private DateTime _lastNetworkArchiveSpaceWarnAt = DateTime.MinValue;
        private DateTime _lastUnarchivedCleanupWarnAt = DateTime.MinValue;
        private long _lastKnownDiskTotalBytes;
        private long _lastKnownDiskCapacityBytes;

        private void RunDiskCleanupCore(bool forceFullScan)
        {
            if (Interlocked.Exchange(ref _diskCleanupRunning, 1) == 1) return;
            try
            {
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

                UpdateDiskUsageText(totalCurrentBytes, totalCapacityBytes);
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _diskCleanupRunning, 0);
            }
        }

        /// <summary>
        /// 网络归档空间检查：低于预留值时限频提示；空间恢复后把 NASFull 记录重新置为等待归档。
        /// NAS 卷状态只影响归档任务，不参与本地录像路径选择、本地 GC 与硬循环触发。
        /// </summary>
        private void CheckNetworkArchiveSpace()
        {
            try
            {
                if (Config.StorageLocations == null)
                    return;

                string? networkPath = null;
                StorageLocation? networkLocation = null;
                foreach (StorageLocation location in Config.StorageLocations)
                {
                    string normalizedPath = Path.IsPathRooted(location.Path)
                        ? location.Path
                        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, location.Path);
                    if (!StorageVolumeInfo.IsNetworkPath(normalizedPath))
                        continue;
                    networkPath = normalizedPath;
                    networkLocation = location;
                    break;
                }
                if (string.IsNullOrWhiteSpace(networkPath) || networkLocation == null)
                    return;

                if (!StorageVolumeInfo.TryGet(networkPath, out StorageVolumeInfo volume))
                    return; // NAS 离线不提示，由归档重试机制处理

                long reserveBytes = StorageSpacePolicy.GetEffectiveReserveBytes(
                    networkLocation,
                    volume);
                if (NetworkArchiveSpacePolicy.IsBelowReserve(
                        volume.AvailableFreeSpace,
                        reserveBytes))
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
                        $"Network archive space below reserve path={networkPath}, free={volume.AvailableFreeSpace / (double)StorageSpacePolicy.BytesPerGiB:F1}GB, reserve={reserveBytes / (double)StorageSpacePolicy.BytesPerGiB:F1}GB");
                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_isDisposed)
                            return;
                        ShowToast(
                            "NAS 空间不足，录像仍保存在本地，归档已暂停；请清理 NAS 或调整归档位置",
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
                if (string.IsNullOrWhiteSpace(archiveRoot))
                {
                    // 没有配置网络归档目标时按“不可达”处理，执行硬循环删除。
                    EmergencyCleanupUnarchived(
                        LocalCopyCleanupPolicy.ComputeEmergencyReleaseTarget(
                            totalCurrentBytes,
                            totalCapacityBytes,
                            releasedByNormalCleanup));
                    return;
                }

                bool archiveReachable = RemoteFileProbe.TryProbeDirectory(
                    archiveRoot,
                    TimeSpan.FromSeconds(3));
                if (!LocalCopyCleanupPolicy.ShouldTriggerEmergencyCleanup(
                        archiveRoot,
                        archiveReachable))
                {
                    _archiveService?.Wake(); // NAS 可达：优先归档而不是删除
                    return;
                }

                EmergencyCleanupUnarchived(
                    LocalCopyCleanupPolicy.ComputeEmergencyReleaseTarget(
                        totalCurrentBytes,
                        totalCapacityBytes,
                        releasedByNormalCleanup));
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Cleanup", $"Emergency cleanup check failed: {ex.Message}");
            }
        }

        private void EmergencyCleanupUnarchived(long bytesToRelease)
        {
            if (bytesToRelease <= 0 || _db == null)
                return;

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
                                _db.MarkVideoDeleted(record.FilePath, reason, reasonCode);
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

                        // 远端轻量确认（存在 + 大小一致），带超时；24 小时内已探测过则直接删除。
                        bool needProbe = LocalCopyCleanupPolicy.ShouldProbeBeforeLocalCleanup(
                            video,
                            now);
                        if (needProbe
                            && !RemoteFileProbe.TryProbeFileWithSize(
                                video.ArchivePath,
                                video.FileSizeBytes,
                                TimeSpan.FromSeconds(3)))
                        {
                            RuntimeLog.Warn(
                                "Cleanup",
                                $"Skip local cleanup because remote unavailable id={video.Id}, path={video.ArchivePath}");
                            continue;
                        }

                        long size = new FileInfo(video.FilePath).Length;
                        using (VideoLifecycleCoordinator.EnterAsync(
                                   video.Id,
                                   CancellationToken.None).GetAwaiter().GetResult())
                        {
                            if (!File.Exists(video.FilePath)) continue;
                            File.Delete(video.FilePath);
                            try
                            {
                                if (needProbe)
                                    _db?.UpdateLastArchiveProbeAt(video.Id, DateTime.Now);
                                _db?.MarkLocalCopyDeleted(
                                    video.Id,
                                    "全局配额清理",
                                    RecordingDeletionReasonCode.CapacityCleanupVerified);
                            }
                            catch (Exception dbEx)
                            {
                                RuntimeLog.Warn(
                                    "Cleanup",
                                    $"Capacity cleanup DB mark failed id={video.Id}, error={dbEx.Message}");
                            }
                        }
                        releasedBytes += size;
                        count++;
                    }
                    catch { }
                }
            }

            int unarchivedCount = 0;
            if (releasedBytes < bytesToRelease)
            {
                string? archiveRoot = GetConfiguredArchiveRoot();
                bool archiveReachable = !string.IsNullOrWhiteSpace(archiveRoot)
                    && RemoteFileProbe.TryProbeDirectory(
                        archiveRoot,
                        TimeSpan.FromSeconds(3));
                if (LocalCopyCleanupPolicy.ShouldTriggerEmergencyCleanup(
                        archiveRoot,
                        archiveReachable))
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
                    ShowUnarchivedCleanupWarning(unarchivedCount);
            }
            return releasedBytes;
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
