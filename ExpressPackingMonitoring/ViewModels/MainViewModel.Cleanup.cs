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
                    bool hasNetworkLocation = Config.StorageLocations.Any(
                        location => StorageVolumeInfo.IsNetworkPath(location.Path));
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

                    if (hasNetworkLocation && !string.IsNullOrWhiteSpace(Config.LocalRecordingBufferPath))
                    {
                        string bufferPath = Path.IsPathRooted(Config.LocalRecordingBufferPath)
                            ? Config.LocalRecordingBufferPath
                            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Config.LocalRecordingBufferPath);
                        if (Directory.Exists(bufferPath))
                        {
                            long bufferCapacity = 0;
                            try
                            {
                                if (StorageVolumeInfo.TryGet(bufferPath, out StorageVolumeInfo volume)
                                    && scannedRoots.Add(volume.RootPath))
                                {
                                    long bufferVideoBytes = 0;
                                    foreach (var fi in EnumerateVideoFiles(bufferPath))
                                        bufferVideoBytes += fi.Length;
                                    totalCurrentBytes += bufferVideoBytes;
                                    long reserveBytes = StorageSpacePolicy.GetEffectiveReserveBytes(
                                        new StorageLocation { Path = bufferPath, ReserveGB = 0 },
                                        volume);
                                    bufferCapacity = Math.Max(0, volume.AvailableFreeSpace - reserveBytes) + bufferVideoBytes;
                                }
                            }
                            catch { }
                            totalCapacityBytes += bufferCapacity;
                        }
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

                if (fullScan && totalCapacityBytes > 0 && totalCurrentBytes > totalCapacityBytes)
                    CleanupOldVideos(totalCurrentBytes, totalCapacityBytes);

                UpdateDiskUsageText(totalCurrentBytes, totalCapacityBytes);
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _diskCleanupRunning, 0);
            }
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

        private void CleanupOldVideos(long totalCurrentBytes, long totalCapacityBytes)
        {
            long bytesToRelease = totalCurrentBytes - (long)(totalCapacityBytes * 0.9);
            if (bytesToRelease <= 0) return;
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

                        // 远端轻量确认（存在 + 大小一致），带超时，失败跳过本轮并保留本地副本。
                        if (!RemoteFileProbe.TryProbeFileWithSize(
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
                        }
                        releasedBytes += size;
                        count++;
                        _db?.MarkLocalCopyDeleted(video.Id, "全局配额清理");
                    }
                    catch { }
                }
            }

            if (skippedUnverified > 0)
            {
                RuntimeLog.Warn(
                    "Cleanup",
                    $"Capacity cleanup skipped unverified local copies count={skippedUnverified}");
            }

            if (count > 0)
            {
                _lastKnownDiskTotalBytes = Math.Max(0, _lastKnownDiskTotalBytes - releasedBytes);
                _ = Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    ShowToast($"清理：已从多盘回收 {count} 个旧视频");
                    RefreshTodayStats();
                });
            }
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
