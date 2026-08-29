using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.Localization;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using OpenCvSharp;
using AForge.Video.DirectShow;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        private async Task InternalStopRecordingAsync()
        {
            if (!IsRecording || _isDisposed) return;

            IsBusy = true;
            BusyText = _shutdownRequested ? "正在关闭程序..." : "正在停止...";
            IsRecording = false; // 1. 立即改变 UI 状态
            PublishPreRecordBufferStatus(force: true);
            _isScanning = false;
            _delayBeforeZooming = false;
            _zoomPhase = ZoomPhase.None;
            _autoStopWarned = false;
            _maxDurationWarned = false;

            CancellationTokenSource oldCts;
            BlockingCollection<Mat> oldQueue;
            Task oldWriteTask;
            Process? oldFfmpegProcess;
            string? audioFilePath;
            bool directAacForThisRecording;
            bool audioFailedForThisRecording;
            long audioBytesWrittenForThisRecording;

            lock (_videoLock)
            {
                oldCts = _writeCts;
                oldQueue = _videoWriteQueue;
                oldWriteTask = _writeTask;
                oldFfmpegProcess = _currentFfmpegProcess;
                _writeCts = null;
                _videoWriteQueue = null;
                _writeTask = null;
            }

            // 2. 停止生产
            try { oldQueue?.CompleteAdding(); } catch { }
            oldCts?.Cancel(); // 3. 通知 FFmpeg 线程停止
            directAacForThisRecording = _currentAudioUsesDirectAac;
            audioFilePath = StopAudioRecording();
            audioFailedForThisRecording = _audioFailedForCurrentRecording;
            audioBytesWrittenForThisRecording = _audioBytesWritten;

            // 4. 等待录制线程真正退出（FFmpeg 进程关闭）
            try
            {
                if (oldWriteTask != null)
                {
                    // 给 FFmpeg 3秒时间正常写入尾部信息并关闭
                    var completedTask = await Task.WhenAny(oldWriteTask, Task.Delay(3000));
                    if (completedTask != oldWriteTask)
                    {
                        Debug.WriteLine("[MainVM] FFmpeg 正常停止超时，执行强杀...");
                        try 
                        {
                            if (oldFfmpegProcess != null && !oldFfmpegProcess.HasExited)
                            {
                                oldFfmpegProcess.Kill();
                                Debug.WriteLine("[MainVM] 僵尸 FFmpeg 已强杀！");
                            }
                        } 
                        catch { }
                        
                        // 再等1秒确认彻底死亡
                        await Task.WhenAny(oldWriteTask, Task.Delay(1000));
                    }
                }
            }
            catch { }

            // 5. 彻底清空内存中的残余 Mat 对象 (防止泄漏的核心)
            if (oldQueue != null)
            {
                while (oldQueue.TryTake(out var mat)) mat?.Dispose();
                oldQueue.Dispose();
            }
            oldCts?.Dispose();

            // 6. 保存元数据到数据库
            var filePath = _currentVideoFilePath;
            var videoCodec = _currentVideoCodec;
            var videoEncoder = _currentVideoEncoder;
            var recordStart = _recordStartTime;
            var orderId = _recordingOrderId;
            var mode = _recordingMode;
            var stopReason = _stopReason;
            var scanRecord = _currentScanRecord;
            var recordId = _currentRecordId; 
            var audioLogPath = _currentAudioLogPath;
            if (Config.EnableAudioRecording
                && HasConfiguredAudioDevice()
                && (audioFailedForThisRecording
                    || (directAacForThisRecording
                        ? audioBytesWrittenForThisRecording <= 0
                        : string.IsNullOrWhiteSpace(audioFilePath))))
            {
                stopReason = string.IsNullOrWhiteSpace(stopReason) ? "音频异常" : $"{stopReason}（音频异常）";
                RuntimeLog.Warn("Audio", $"Recording audio unavailable id={recordId}, file={Path.GetFileName(filePath ?? "")}, failed={audioFailedForThisRecording}, bytes={audioBytesWrittenForThisRecording}");
            }
            RuntimeLog.Info("Recording", $"Stop requested id={recordId}, reason={stopReason}, file={Path.GetFileName(filePath ?? "")}");

            _recordStartTime = DateTime.MinValue;
            _activePreRecordSeconds = 0;
            _recordingGracePeriodStartTime = DateTime.MinValue;
            _currentScanRecord = null;
            _currentVideoFilePath = null;
            _currentVideoCodec = null;
            _currentVideoEncoder = null;
            _currentRecordId = 0;
            _currentArchivePath = "";
            if (ReferenceEquals(_currentFfmpegProcess, oldFfmpegProcess))
                _currentFfmpegProcess = null;
            _recordingOrderId = null;
            _recordingSessionId = null;
            _recordingWatermarkSnapshot = WatermarkSnapshot.Empty;

            _lastFinalizeTask = Task.Run(() => 
            {
                if (_isDisposed) return; // 销毁中不再执行数据库后的 UI 更新
                try
                {
                    long fileSize = GetCompletedRecordingSizeBytes(filePath, audioFilePath);
                    double recordDuration = (DateTime.Now - recordStart).TotalSeconds;
                    int durSec = Math.Max(1, (int)recordDuration);

                    // 文件过小和录制过短是两条独立规则，原因要分别写入数据库。
                    long minFileSizeBytes = GetMinVideoFileSizeBytes();
                    bool tooSmall = minFileSizeBytes > 0 && fileSize < minFileSizeBytes;
                    bool tooShort = Config.MinRecordingSeconds > 0 && recordDuration < Config.MinRecordingSeconds;
                    if (tooSmall || tooShort)
                    {
                        string deleteReason = tooSmall
                            ? $"文件过小，小于 {FormatMinVideoFileSize(Config.MinVideoFileSizeKB)}"
                            : $"录像过短，少于 {FormatSecondsForReason(Config.MinRecordingSeconds)}";
                        _db?.UpdateVideoRecordOnStop(recordId, DateTime.Now, durSec, fileSize, deleteReason, videoCodec, videoEncoder);

                        bool videoDeleted = DeleteVideoFileForRule(filePath, deleteReason);
                        DeleteAudioTempFile(audioFilePath);
                        DeleteEmbeddedAudioMarker(filePath);
                        if (videoDeleted && !string.IsNullOrWhiteSpace(filePath))
                            _db?.MarkVideoDeleted(filePath, deleteReason);

                        _ = Application.Current.Dispatcher.InvokeAsync(() => {
                            if (!_isDisposed) {
                                _allLogs.Remove(scanRecord);
                                FilteredLogs.Remove(scanRecord);
                                if (tooSmall)
                                {
                                    ShowToast("视频文件太小，已删除", ToastSeverity.Warning);
                                    SpeakWarning(DefaultSpeechCatalog.VideoFileTooSmall);
                                }
                                else if (tooShort)
                                {
                                    ShowToast($"录像过短({recordDuration:F1}s)，已丢弃", ToastSeverity.Warning);
                                    SpeakWarning(DefaultSpeechCatalog.RecordingTooShort);
                                }
                            }
                        });
                    }
                    else
                    {
                        double dur = (DateTime.Now - recordStart).TotalSeconds;
                        durSec = (int)dur;
                        if (durSec < 1) durSec = 1;
                        string durStr = durSec < 60 ? $"{durSec}s" : $"{(int)durSec / 60}m {durSec % 60}s";

                        _db?.UpdateVideoRecordOnStop(recordId, DateTime.Now, durSec, fileSize, stopReason, videoCodec, videoEncoder);
                        if (!string.IsNullOrWhiteSpace(filePath))
                        {
                            _pendingRecordingSpecificationChecks[filePath] =
                                new ExpectedRecordingSpecification(
                                    Config.FrameWidth,
                                    Config.FrameHeight,
                                    Config.Fps,
                                    dur);
                        }

                        // 自动将 MKV 转换为 MP4（无损容器转换）
                        RuntimeLog.Info("Recording", $"Recording finalized as MKV, queued for idle/web conversion: {Path.GetFileName(filePath)}");

                        _ = Application.Current.Dispatcher.InvokeAsync(() => {
                            if (!_isDisposed && scanRecord != null)
                            {
                                scanRecord.Duration = "已保存";
                                scanRecord.IsActive = false;
                                RefreshTodayStats();
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    WriteAudioDiagnostic($"Finalize exception: {ex.Message}", audioLogPath);
                    WriteAudioDiagnostic($"Finalize 异常: {ex.Message}");
                }
                finally
                {
                    if (string.Equals(_currentAudioLogPath, audioLogPath, StringComparison.OrdinalIgnoreCase))
                        _currentAudioLogPath = null;
                }
            });
            
            // 退出期间由关闭流程持续管理 Busy，避免按钮短暂变回“开始录制”并被再次点击。
            if (Application.Current?.MainWindow != null && !_isDisposed && !_shutdownRequested)
            {
                IsBusy = false;
            }

            if (_pendingCameraRestart && !_isDisposed)
            {
                _pendingCameraRestart = false;
                _consecutiveRestartFailures = 0;
                RestartCamera();
                ShowToast("摄像头配置已生效");
            }
        }

        private string ResolveBestStoragePath() =>
            ResolveBestStoragePlan().WorkingRootPath;

        private RecordingStoragePlan ResolveBestStoragePlan()
        {
            if (IsRecordingWorkstation)
            {
                StorageLocation location =
                    RecordingWorkstationCachePolicy.GetConfiguredLocation(Config)
                    ?? throw new IOException("尚未设置本地缓存位置");
                string resolved = StorageLocationResolver.Resolve(location);
                return new RecordingStoragePlan(resolved, "", false);
            }
            return StorageLocationResolver.ResolveRecordingPlan(Config, allowDefaultFallback: true);
        }

        private StorageLocationEvaluation TryEvaluateStorageLocation(StorageLocation loc)
        {
            string normalizedPath = NormalizeStoragePath(loc.Path);
            try
            {
                if (!Directory.Exists(normalizedPath))
                    Directory.CreateDirectory(normalizedPath);

                if (!IsDirectoryWritable(normalizedPath))
                    return StorageLocationEvaluation.Skip(normalizedPath, "not writable");

                string? root = Path.GetPathRoot(Path.GetFullPath(normalizedPath));
                if (string.IsNullOrEmpty(root))
                    return StorageLocationEvaluation.Skip(normalizedPath, "missing drive root");

                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                    return StorageLocationEvaluation.Skip(normalizedPath, "drive not ready");

                long reserveBytes = StorageSpacePolicy.GetEffectiveReserveBytes(loc, drive);
                long availableBytes = drive.AvailableFreeSpace;
                if (availableBytes <= reserveBytes)
                {
                    return StorageLocationEvaluation.Skip(
                        normalizedPath,
                        $"below reserve free={FormatBytesForLog(availableBytes)}, reserve={FormatBytesForLog(reserveBytes)}");
                }

                long usedBytes = GetVideoBytes(normalizedPath);
                long writableBytes = Math.Max(0, availableBytes - reserveBytes);
                long effectiveCapacityBytes = usedBytes + writableBytes;
                long remainingBytes = effectiveCapacityBytes - usedBytes;

                if (remainingBytes <= 0)
                {
                    return StorageLocationEvaluation.Skip(
                        normalizedPath,
                        $"reserved space reached used={FormatBytesForLog(usedBytes)}, reserve={FormatBytesForLog(reserveBytes)}, available={FormatBytesForLog(availableBytes)}");
                }

                return StorageLocationEvaluation.Use(
                    normalizedPath,
                    availableBytes,
                    reserveBytes,
                    usedBytes,
                    effectiveCapacityBytes,
                    remainingBytes);
            }
            catch (Exception ex)
            {
                return StorageLocationEvaluation.Skip(normalizedPath, $"exception={ex.Message}");
            }
        }

        private static string NormalizeStoragePath(string path)
        {
            return Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        }

        private static long GetVideoBytes(string folderPath)
        {
            long totalBytes = 0;
            foreach (var file in EnumerateVideoFiles(folderPath))
            {
                try
                {
                    totalBytes += file.Length;
                }
                catch
                {
                    // A file may disappear while cleanup or conversion is running.
                }
            }

            return totalBytes;
        }

        private static string FormatBytesForLog(long bytes)
        {
            if (bytes <= 0) return "0GB";
            return $"{bytes / (double)BytesPerGiB:F1}GB";
        }

        private const long BytesPerGiB = StorageSpacePolicy.BytesPerGiB;

        private readonly struct StorageLocationEvaluation
        {
            private StorageLocationEvaluation(
                bool canUse,
                string path,
                string reason,
                long availableBytes,
                long reserveBytes,
                long usedBytes,
                long capacityBytes,
                long remainingBytes)
            {
                CanUse = canUse;
                Path = path;
                Reason = reason;
                AvailableBytes = availableBytes;
                ReserveBytes = reserveBytes;
                UsedBytes = usedBytes;
                CapacityBytes = capacityBytes;
                RemainingBytes = remainingBytes;
            }

            public bool CanUse { get; }
            public string Path { get; }
            public string Reason { get; }
            public long AvailableBytes { get; }
            public long ReserveBytes { get; }
            public long UsedBytes { get; }
            public long CapacityBytes { get; }
            public long RemainingBytes { get; }

            public static StorageLocationEvaluation Use(
                string path,
                long availableBytes,
                long reserveBytes,
                long usedBytes,
                long capacityBytes,
                long remainingBytes) =>
                new(true, path, "", availableBytes, reserveBytes, usedBytes, capacityBytes, remainingBytes);

            public static StorageLocationEvaluation Skip(string path, string reason) =>
                new(false, path, reason, 0, 0, 0, 0, 0);
        }

        private long GetCompletedRecordingSizeBytes(string? videoFilePath, string? audioFilePath)
        {
            long totalBytes = 0;
            totalBytes += GetExistingFileSize(videoFilePath);
            totalBytes += GetExistingFileSize(audioFilePath);
            return totalBytes;
        }

        private static long GetExistingFileSize(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return 0;

            try
            {
                return new FileInfo(filePath).Length;
            }
            catch
            {
                return 0;
            }
        }

        private long GetMinVideoFileSizeBytes()
        {
            if (Config.MinVideoFileSizeKB <= 0)
                return 0;

            return (long)Config.MinVideoFileSizeKB * 1024L;
        }

        private static string FormatMinVideoFileSize(int sizeKb)
        {
            return $"{Math.Max(0, sizeKb)} KB";
        }

        private static string FormatSecondsForReason(double seconds)
        {
            return seconds % 1 == 0 ? $"{seconds:F0} 秒" : $"{seconds:F1} 秒";
        }

        private static bool DeleteVideoFileForRule(string? filePath, string reason)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            try
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);

                return !File.Exists(filePath);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Recording", $"Failed to delete video file by rule, reason={reason}, file={Path.GetFileName(filePath)}", ex);
                return false;
            }
        }

        private bool IsDirectoryWritable(string dirPath)
        {
            try
            {
                if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
                string testFile = Path.Combine(dirPath, ".write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return true;
            }
            catch { return false; }
        }

        private void EnsureDirectoryWritable(string dirPath)
        {
            try
            {
                if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Storage] 无法创建默认目录: {ex.Message}");
            }
        }

        private bool EnsureRecordingStorageHeadroomForNewRecording()
        {
            if (!IsRecordingWorkstation)
                return true;

            try
            {
                StorageLocation location =
                    RecordingWorkstationCachePolicy.GetConfiguredLocation(Config)
                    ?? throw new IOException("尚未设置本地缓存位置");
                string cachePath = Path.GetFullPath(location.Path);
                string? root = Path.GetPathRoot(cachePath);
                if (string.IsNullOrWhiteSpace(root))
                    throw new IOException("无法确定本地缓存所在磁盘");
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                    throw new IOException("本地缓存所在磁盘未就绪");
                long reserveBytes =
                    StorageSpacePolicy.GetEffectiveReserveBytes(location, drive);
                if (RecordingWorkstationCachePolicy.HasRequiredPhysicalHeadroom(
                        drive.AvailableFreeSpace,
                        reserveBytes,
                        RecordingWorkstationCachePolicy
                            .RecordingAndPackagingHeadroomBytes))
                {
                    return true;
                }
                RuntimeLog.Warn(
                    "Recording",
                    $"Recording start rejected by physical storage gate available={drive.AvailableFreeSpace}, reserve={reserveBytes}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn(
                    "Recording",
                    $"Recording start storage gate unavailable: {ex.Message}");
            }

            RunRecordingCacheCleanup();

            if (_recordingCacheBlockedDialogShown)
                return false;

            _recordingCacheBlockedDialogShown = true;
            SpeakWarning(DefaultSpeechCatalog.StoragePathNotWritable);
            bool manageHost = AppDialog.Confirm(
                Application.Current?.MainWindow,
                "本地缓存暂时没有足够空间开始下一段录像。系统已先清理可安全删除的已上传录像，其他录像仍完整保留",
                "本地缓存空间不足",
                AppDialogSeverity.Warning,
                confirmText: "管理保存主机",
                cancelText: "更改缓存位置");
            if (manageHost)
                ChangeBoundHost(Application.Current?.MainWindow);
            else
                OpenSettings(selectRecordingCache: true);
            return false;
        }

        private async Task InternalStartRecordingAsync()
        {
            var startupWatch = Stopwatch.StartNew();
            IsBusy = true;
            BusyText = "正在启动...";

            try
            {
                // 0. 环境预检查 (摄像头、麦克风)
                if (!IsVideoSourceRunning())
                {
                    // 尝试重启一次摄像头，以防万一用户刚插上
                    RestartCamera();
                    await Task.Delay(1000); // 给一点点启动时间

                    if (!IsVideoSourceRunning())
                    {
                        ShowToast("摄像头未就绪，请检查连接", ToastSeverity.Warning);
                        SpeakWarning(DefaultSpeechCatalog.CameraNotReady);
                        return;
                    }
                }

                if (!await WaitForCameraFrameAsync(TimeSpan.FromSeconds(3)))
                {
                    RuntimeLog.Warn("Recording", "Camera is running but no valid frame arrived within 3 seconds");
                    ShowToast("摄像头唤醒后没有画面，未开始录制", ToastSeverity.Warning);
                    SpeakWarning(DefaultSpeechCatalog.CameraNotReady);
                    return;
                }

                if (!EnsureRecordingStorageHeadroomForNewRecording())
                {
                    return;
                }
                RuntimeLog.Info("Recording", $"Start storage gate completed elapsedMs={startupWatch.ElapsedMilliseconds}");

                bool startAudioAfterVideo = Config.EnableAudioRecording && HasConfiguredAudioDevice();
                bool useDirectAac = startAudioAfterVideo && Config.EnableDirectAacRecording;

                // 1. 初始化路径和文件名
                string baseFolder;
                try
                {
                    RecordingStoragePlan storagePlan = ResolveBestStoragePlan();
                    baseFolder = storagePlan.WorkingRootPath;
                    _currentArchivePath = storagePlan.RequiresNetworkArchive
                        ? storagePlan.ArchiveTarget
                        : "";
                    if (!IsDirectoryWritable(baseFolder))
                    {
                        ShowToast("存储路径不可写，请检查磁盘", ToastSeverity.Warning);
                        SpeakWarning(DefaultSpeechCatalog.StoragePathNotWritable);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _currentArchivePath = "";
                    ShowToast($"存储初始化失败: {ex.Message}", ToastSeverity.Error);
                    return;
                }

                string dateFolder = Path.Combine(baseFolder, DateTime.Now.ToString("yyyy-MM-dd"));
                try
                {
                    if (!Directory.Exists(dateFolder)) Directory.CreateDirectory(dateFolder);
                }
                catch (Exception ex)
                {
                    ShowToast($"无法创建日期目录: {ex.Message}", ToastSeverity.Error);
                    return;
                }

                string fileName = $"{CurrentOrderId}_{DateTime.Now:yyyyMMdd_HHmmss}_{CurrentMode}.mkv";
                string filePath = Path.Combine(dateFolder, fileName);
                string archivePath = string.IsNullOrWhiteSpace(_currentArchivePath)
                    ? ""
                    : ArchivePathBuilder.BuildLocalRecordingArchivePath(
                        _currentArchivePath,
                        DateTime.Now,
                        fileName);
                string audioFilePath = Path.ChangeExtension(filePath, ".wav");
                string audioLogPath = Path.ChangeExtension(filePath, ".audio.log");
                RuntimeLog.Info("Recording", $"Start requested order={CurrentOrderId}, mode={CurrentMode}, file={fileName}, codec={Config.VideoCodec}");
                _currentAudioLogPath = audioLogPath;
                _audioFailedForCurrentRecording = false;
                _currentVideoFilePath = filePath;
                _stopReason = "手动";
                _recordingOrderId = CurrentOrderId;
                _recordingMode = CurrentMode;
                _currentVideoCodec = Config.VideoCodec?.Trim().ToLowerInvariant() ?? "h264";
                _currentVideoEncoder = ResolveEncoder();

                if (string.IsNullOrWhiteSpace(_currentVideoEncoder))
                {
                    RuntimeLog.Error("Recording", $"Encoder selection rejected. gpu={Config.GpuEncoder}, codec={Config.VideoCodec}");
                    ShowToast("当前选择未通过编码器检测", ToastSeverity.Error);
                    AppDialog.Error(null, "当前选择未通过编码器检测，请重新检测或选择设置列表中的可用编码器", "无法开始录制");
                    ClearCurrentAudioLogPath(audioLogPath);
                    return;
                }

                string ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    ShowToast("未找到 FFmpeg，无法录制", ToastSeverity.Error);
                    ClearCurrentAudioLogPath(audioLogPath);
                    return;
                }

                if (useDirectAac && !PrepareDirectAudioPipe())
                {
                    ShowToast("实时 AAC 音频管道初始化失败，已取消开录", ToastSeverity.Error);
                    ClearCurrentAudioLogPath(audioLogPath);
                    return;
                }

                // 3. 开启新的生产者-消费者通道
                lock (_videoLock)
                {
                    int recordingWidth = _actualCameraWidth > 0 ? _actualCameraWidth : Config.FrameWidth;
                    int recordingHeight = _actualCameraHeight > 0 ? _actualCameraHeight : Config.FrameHeight;
                    int recordingFps = GetEffectiveRecordingFps();
                    int queueCapacity = RecordingBufferPolicy.CalculateVideoQueueCapacity(
                        recordingWidth,
                        recordingHeight,
                        recordingFps);
                    if (_pendingPreRecordFrames is { Count: > 0 })
                        queueCapacity = Math.Max(queueCapacity, _pendingPreRecordFrames.Count + 6);
                    _videoWriteQueue = new BlockingCollection<Mat>(queueCapacity);
                    _writeCts = new CancellationTokenSource();
                    _lastRecordingQueueWarnAt = DateTime.MinValue;

                    long bufferedBytes = (long)queueCapacity * recordingWidth * recordingHeight * 3;
                    RuntimeLog.Info(
                        "Recording",
                        $"Video frame queue capacity={queueCapacity}, estimatedRawBuffer={bufferedBytes / 1024d / 1024d:F1}MB");
                }

                // 4. 启动录制任务
                _recordingStartTimestamp = Stopwatch.GetTimestamp();
                _firstRecordingFrameWritten = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                _writeTask = Task.Run(() => BackgroundFFmpegRecordingLoop(
                    filePath,
                    ffmpegPath,
                    _writeCts.Token,
                    useDirectAac,
                    useDirectAac ? _currentAudioPipeName : null));

                // 先建立实时录制保护基准，再公开 IsRecording 状态并向队列灌入预录帧，
                // 避免超时线程在状态切换或大缓冲入队期间使用预录起点误停录。
                _recordingGracePeriodStartTime = DateTime.Now;
                _lastMotionTime = _recordingGracePeriodStartTime;
                IsRecording = true;
                PublishPreRecordBufferStatus(force: true);
                _pendingPreRecordStartTime = null;
                List<Mat>? preRecordFrames = _pendingPreRecordFrames;
                List<DateTime>? preRecordTimestamps = _pendingPreRecordTimestamps;
                _pendingPreRecordFrames = null;
                _pendingPreRecordTimestamps = null;
                int timelineFps = GetEffectiveRecordingFps();
                int usablePreRecordFrameCount = preRecordFrames?.Count ?? 0;
                _activePreRecordSeconds = usablePreRecordFrameCount > 0 && timelineFps > 0
                    ? Math.Clamp(usablePreRecordFrameCount / (double)timelineFps, 0, 5)
                    : 0;
                // 数据库时间线必须匹配最终视频的帧数/FPS，不能使用环形缓存中
                // 低处理频率帧的墙上时间跨度，否则会把 5 秒视频记成 12 秒以上。
                _recordStartTime = DateTime.Now - TimeSpan.FromSeconds(_activePreRecordSeconds);
                lock (_recordingFrameOrderLock)
                {
                    if (preRecordFrames != null)
                    {
                        for (int preFrameIndex = 0; preFrameIndex < preRecordFrames.Count; preFrameIndex++)
                        {
                            Mat preFrame = preRecordFrames[preFrameIndex];
                            try
                            {
                                if (Config.EnableWatermark)
                                {
                                    // 预录帧按采集时刻绘制水印，不能使用注入时刻，否则整段预录画面的时间/动态水印会静止。
                                    DateTime watermarkTime = DateTime.Now;
                                    // 时间戳与帧一一对应，由快照阶段保存到并行列表。
                                    if (preRecordTimestamps != null && preFrameIndex < preRecordTimestamps.Count)
                                        watermarkTime = preRecordTimestamps[preFrameIndex];
                                    ApplyWatermarkToFrame(preFrame, watermarkTime, _recordingOrderId, Array.Empty<string>());
                                }
                                if (!TryEnqueueFrameForRecording(preFrame))
                                    preFrame.Dispose();
                            }
                            catch
                            {
                                preFrame.Dispose();
                            }
                        }
                        RuntimeLog.Info("Recording", $"Pre-record frames queued count={preRecordFrames.Count}");
                    }
                    EnqueueLatestFrameForRecording();
                }
                _lastMotionTime = DateTime.Now;
                _autoStopWarned = false;
                _maxDurationWarned = false;
                _previousCheckFrame?.Dispose();
                _previousCheckFrame = new Mat();

                // 5. 快速确认首帧是否已经写入 FFmpeg，避免固定等待拖慢开录。
                var firstFrameTask = _firstRecordingFrameWritten.Task;
                var startupCheck = await Task.WhenAny(firstFrameTask, _writeTask, Task.Delay(200));
                if (startupCheck == firstFrameTask)
                    Debug.WriteLine($"[RecordingStartup] first frame written in {firstFrameTask.Result} ms (total {startupWatch.ElapsedMilliseconds} ms)");
                else if (startupCheck != _writeTask)
                    Debug.WriteLine($"[RecordingStartup] first frame not confirmed within 200 ms (total {startupWatch.ElapsedMilliseconds} ms)");
                if (_writeTask.IsCompleted) 
                {
                    RuntimeLog.Warn("Recording", $"Recording writer completed during startup, file={Path.GetFileName(filePath)}");
                    DeleteAudioTempFile(StopAudioRecording());
                    ClearCurrentAudioLogPath(audioLogPath);
                    IsRecording = false;
                    Debug.WriteLine("[MainVM] 启动检测：_writeTask 已结束，FFmpeg 启动失败");
                    // 启动阶段已经提前进入录制状态，失败时要回滚 UI 状态。
                    return; 
                }

                if (startAudioAfterVideo)
                {
                    WriteAudioDiagnostic($"准备启动麦克风录制: name={Config.AudioDeviceName}, moniker={(string.IsNullOrWhiteSpace(Config.AudioDeviceMoniker) ? "(empty)" : Config.AudioDeviceMoniker)}");
                    if (!StartAudioRecording(useDirectAac ? null : audioFilePath, useDirectAac))
                    {
                        WriteAudioDiagnostic("麦克风录音启动失败");
                        ShowToast("音频录制启动失败", ToastSeverity.Error);
                        SpeakWarning(DefaultSpeechCatalog.AudioRecordingStartFailed);
                        try
                        {
                            lock (_videoLock)
                            {
                                _videoWriteQueue?.CompleteAdding();
                                _writeCts?.Cancel();
                            }
                            await Task.WhenAny(_writeTask, Task.Delay(3000));
                        }
                        catch { }
                        IsRecording = false;
                        try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                        DeleteEmbeddedAudioMarker(filePath);
                        ClearCurrentAudioLogPath(audioLogPath);
                        return;
                    }
                }

                // 6. 在数据库中创建记录占位符
                _recordingSessionId = Guid.NewGuid().ToString("N");
                _recordingWatermarkSnapshot = new WatermarkSnapshot(_recordingSessionId, Array.Empty<string>());
                var orderInfoSnapshot = _webServer?.GetOrderInfo(_recordingOrderId);
                _currentRecordId = _db?.InsertVideoRecord(
                    _recordingOrderId,
                    _recordingMode,
                    _currentVideoCodec,
                    _currentVideoEncoder,
                    filePath,
                    _recordStartTime,
                    orderInfoSnapshot,
                    Config.MobileBackupComputerId,
                    Environment.MachineName,
                    archivePath,
                    _recordingSessionId) ?? 0;
                RuntimeLog.Info("Recording", $"Database record inserted id={_currentRecordId}, file={Path.GetFileName(filePath)}");
                if (Config.EnableEventRecordingBuffer && !string.IsNullOrWhiteSpace(_recordingSessionId))
                {
                    _db?.UpsertRecordingExtensionFields(
                        _recordingSessionId,
                        "packingproof.event-recording",
                        "",
                        "",
                        0,
                        new Dictionary<string, string>
                        {
                            ["preRecordSeconds"] = _activePreRecordSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                            ["configuredPreRecordBufferMB"] = Config.PreRecordBufferMB.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["configuredPreRecordSeconds"] = EstimatePreRecordSeconds().ToString("F1", System.Globalization.CultureInfo.InvariantCulture),
                            ["sameCodePostRecordSeconds"] = Config.SameCodePostRecordSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                        },
                        DateTime.UtcNow);
                }

                ShowToast("开始录像", ToastSeverity.Information);
                Speak(DefaultSpeechCatalog.StartRecording, cancelPrevious: false);
                if (_currentScanRecord != null)
                {
                    _currentScanRecord.IsActive = false;
                    _currentScanRecord.Duration = "失败";
                }
                int initialDisplayedSeconds = Math.Max(0, (int)Math.Floor(_activePreRecordSeconds));
                _currentScanRecord = new ScanRecord(_recordingOrderId, $"{initialDisplayedSeconds}s", DateTime.Now.ToString("HH:mm:ss"), _recordingMode, true);
                AddRecord(_currentScanRecord);
            }
            finally
            {
                // 启动失败或首帧检查回滚时，不能把已复制的预录帧留在待注入列表中
                if (!IsRecording)
                    ClearPendingEventRecordingFrames();
                IsBusy = false;
            }
        }

        private void BackgroundFFmpegRecordingLoop(
            string filePath,
            string ffmpegPath,
            CancellationToken token,
            bool withDirectAudio = false,
            string? audioPipeName = null)
        {
            int w = _actualCameraWidth > 0 ? _actualCameraWidth : Config.FrameWidth;
            int h = _actualCameraHeight > 0 ? _actualCameraHeight : Config.FrameHeight;
            int fps = GetEffectiveRecordingFps();
            string encoder = ResolveEncoder();
            if (string.IsNullOrWhiteSpace(encoder))
            {
                RuntimeLog.Error("FFmpeg", $"Encoder selection rejected. gpu={Config.GpuEncoder}, codec={Config.VideoCodec}");
                return;
            }
            bool hasAudio = withDirectAudio;
            string requestedEncoder = encoder;

            var (ok, err) = RunFFmpegPipeline(filePath, ffmpegPath, token, w, h, fps, encoder, hasAudio, audioPipeName);
            
            if (ok)
            {
                _currentVideoEncoder = encoder;
                _currentVideoCodec = EncodingHelper.GetCodecFromEncoder(encoder);
                RuntimeLog.Info("FFmpeg", $"Recording pipeline completed ok, encoder={encoder}, file={Path.GetFileName(filePath)}");
                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    ShowToast($"编码器 {EncodingHelper.GetEncoderLabel(encoder)}"));
                return;
            }

            // 如果失败，强制重置 UI 状态
            if (!token.IsCancellationRequested)
            {
                DeleteAudioTempFile(StopAudioRecording());
                try { if (File.Exists(filePath) && new FileInfo(filePath).Length == 0) File.Delete(filePath); } catch { }
                string errorDetail = err;

                RuntimeLog.Error("FFmpeg", $"Recording failed. requested={requestedEncoder}, final={encoder}, file={Path.GetFileName(filePath)}, error={errorDetail}");

                _ = Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    MarkCurrentRecordingFailed("编码失败", errorDetail, filePath, EncodingHelper.GetCodecFromEncoder(encoder), encoder);
                    if (_currentScanRecord != null)
                    {
                        _currentScanRecord.IsActive = false;
                        _currentScanRecord.Duration = "失败";
                        _currentScanRecord = null;
                    }
                    RefreshTodayStats();
                    _cameraStartFailedSuppression.RecordFailure(
                        CurrentOrderId ?? "",
                        DateTimeOffset.UtcNow);
                    IsRecording = false;
                    IsBusy = false; // 释放 Busy 状态
                    CurrentOrderId = "";
                    ScanInputText = "";

                    lock (_videoLock)
                    {
                        if (_videoWriteQueue != null)
                        {
                            _videoWriteQueue.CompleteAdding();
                            while (_videoWriteQueue.TryTake(out var m)) m?.Dispose();
                            _videoWriteQueue.Dispose();
                            _videoWriteQueue = null;
                        }
                    }

                    ShowToast("录制启动失败", ToastSeverity.Error);
                    SpeakWarning(DefaultSpeechCatalog.RecordingFailed);
                    AppDialog.Error(
                        null,
                        $"当前设置的编码器无法完成录制，视频未保存。\n\n请求编码器: {EncodingHelper.GetEncoderLabel(requestedEncoder)}\n错误详情: {errorDetail}\n\n请重新检测编码器、检查显卡驱动，或选择设置列表中的其他可用编码器",
                        "录制失败");
                });
            }
        }

        private void MarkCurrentRecordingFailed(string stopReason, string errorDetail, string filePath, string videoCodec, string videoEncoder)
        {
            try
            {
                long recordId = _currentRecordId;
                if (recordId <= 0) return;

                long fileSize = 0;
                try { fileSize = File.Exists(filePath) ? new FileInfo(filePath).Length : 0; } catch { }

                double duration = _recordStartTime == DateTime.MinValue
                    ? 0
                    : Math.Max(0, (DateTime.Now - _recordStartTime).TotalSeconds);

                _db?.UpdateVideoRecordOnStop(recordId, DateTime.Now, duration, fileSize, stopReason, videoCodec, videoEncoder);
                RuntimeLog.Warn("Recording", $"Marked record failed id={recordId}, reason={stopReason}, size={fileSize}, duration={duration:F1}, error={errorDetail}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Recording", "Failed to mark recording failure in database", ex);
            }
        }

        private void EnqueueLatestFrameForRecording()
        {
            lock (_recordingFrameOrderLock)
            {
                EnqueueLatestFrameForRecordingCore();
            }
        }

        private void EnqueueLatestFrameForRecordingCore()
        {
            try
            {
                BlockingCollection<Mat>? queue = _videoWriteQueue;
                if (queue == null || queue.IsAddingCompleted) return;

                Mat? frame = null;
                lock (_frameLock)
                {
                    if (_latestFrame != null && !_latestFrame.IsDisposed && !_latestFrame.Empty())
                        frame = _latestFrame.Clone();
                }

                if (frame == null) return;
                if (Config.EnableWatermark)
                {
                    IReadOnlyList<string> extensionLines = Config.EnableThirdPartyWatermark
                        && _recordingWatermarkSnapshot.RecordingSessionId == _recordingSessionId
                        ? _recordingWatermarkSnapshot.Lines
                        : Array.Empty<string>();
                    ApplyWatermarkToFrame(frame, DateTimeOffset.Now, _recordingOrderId, extensionLines);
                }
                if (!queue.TryAdd(frame, 5))
                    frame.Dispose();
            }
            catch { }
        }

        private void UpdatePreRecordBuffer(Mat frame)
        {
            if (!Config.EnableEventRecordingBuffer || frame == null || frame.IsDisposed || frame.Empty())
                return;
            if (GetPreRecordBufferMaxBytes() <= 0)
                return;
            try
            {
                Mat clone = frame.Clone();
                long bytes = (long)clone.Rows * clone.Cols * Math.Max(1, clone.ElemSize());
                lock (_eventBufferLock)
                {
                    if ((_preRecordWidth > 0 && _preRecordHeight > 0)
                        && (_preRecordWidth != clone.Cols || _preRecordHeight != clone.Rows))
                    {
                        foreach (PreRecordFrame oldFrame in _preRecordFrames)
                            oldFrame.Frame.Dispose();
                        _preRecordFrames.Clear();
                        _preRecordBytes = 0;
                        _preRecordDisplayCapacityFrames = 0;
                        _preRecordDroppedFrames = 0;
                        _preRecordBufferHasWrapped = false;
                        _preRecordRollingTransitionPending = false;
                        RuntimeLog.Info("Recording", $"Pre-record buffer reset after frame size change {_preRecordWidth}x{_preRecordHeight}->{clone.Cols}x{clone.Rows}");
                    }
                    _preRecordWidth = clone.Cols;
                    _preRecordHeight = clone.Rows;
                    _preRecordFrames.AddLast(new PreRecordFrame
                    {
                        Frame = clone,
                        Timestamp = DateTime.Now,
                        Bytes = bytes,
                        Sequence = Interlocked.Increment(ref _preRecordSequence)
                    });
                    _preRecordBytes += bytes;
                    long maxBytes = GetPreRecordBufferMaxBytes();
                    if (_preRecordDisplayCapacityFrames <= 0 && bytes > 0 && maxBytes > 0)
                        _preRecordDisplayCapacityFrames = Math.Max(
                            1,
                            (int)Math.Min(int.MaxValue, maxBytes / bytes));
                    while (_preRecordFrames.First != null && _preRecordBytes > maxBytes)
                    {
                        PreRecordFrame old = _preRecordFrames.First.Value;
                        _preRecordFrames.RemoveFirst();
                        _preRecordBytes -= old.Bytes;
                        old.Frame.Dispose();
                        _preRecordDroppedFrames++;
                        _preRecordBufferHasWrapped = true;
                    }
                }
                PublishPreRecordBufferStatus();
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Recording", $"Pre-record frame capture skipped: {ex.Message}");
            }
        }

        private List<Mat> SnapshotPreRecordFrames(DateTime eventTime, out DateTime? firstTimestamp, out List<DateTime> timestamps)
        {
            firstTimestamp = null;
            timestamps = new List<DateTime>();
            var result = new List<Mat>();
            long snapshotBytes = 0;
            if (!Config.EnableEventRecordingBuffer || Config.PreRecordBufferMB <= 0)
                return result;
            lock (_eventBufferLock)
            {
                // 转移事件前帧的所有权，避免为 1GB 原始帧缓冲再复制一份造成瞬时内存峰值。
                // 事件后新帧会继续进入环形缓冲，下一次扫码仍可获得最新预录窗口。
                LinkedListNode<PreRecordFrame>? node = _preRecordFrames.First;
                while (node != null)
                {
                    LinkedListNode<PreRecordFrame>? next = node.Next;
                    PreRecordFrame item = node.Value;
                    if (item.Timestamp <= eventTime)
                    {
                        _preRecordFrames.Remove(node);
                        _preRecordBytes -= item.Bytes;
                        result.Add(item.Frame);
                        timestamps.Add(item.Timestamp);
                        firstTimestamp ??= item.Timestamp;
                        snapshotBytes += item.Bytes;
                    }
                    node = next;
                }
                if (result.Count > 0)
                {
                    RuntimeLog.Info(
                        "Recording",
                        $"Pre-record snapshot frames={result.Count}, bytes={snapshotBytes}, coverageSeconds={(firstTimestamp.HasValue ? (eventTime - firstTimestamp.Value).TotalSeconds : 0):F2}, dropped={_preRecordDroppedFrames}");
                    _preRecordDroppedFrames = 0;
                    _preRecordBufferHasWrapped = false;
                    _preRecordRollingTransitionPending = false;
                }
            }
            if (result.Count > 0)
                PublishPreRecordBufferStatus(force: true);
            return result;
        }

        private void ClearPreRecordBuffer()
        {
            lock (_eventBufferLock)
            {
                foreach (PreRecordFrame item in _preRecordFrames) item.Frame.Dispose();
                _preRecordFrames.Clear();
                _preRecordBytes = 0;
                _preRecordWidth = 0;
                _preRecordHeight = 0;
                _preRecordDisplayCapacityFrames = 0;
                _preRecordDroppedFrames = 0;
                _preRecordBufferHasWrapped = false;
                _preRecordRollingTransitionPending = false;
            }
            PublishPreRecordBufferStatus(force: true);
        }

        private void RefreshPreRecordBufferCapacityAfterConfigChange()
        {
            long maxBytes = GetPreRecordBufferMaxBytes();
            int removedFrames = 0;
            int capacityFrames;
            lock (_eventBufferLock)
            {
                long bytesPerFrame = _preRecordFrames.First?.Value.Bytes ?? 0;
                _preRecordDisplayCapacityFrames = bytesPerFrame > 0 && maxBytes > 0
                    ? Math.Max(1, (int)Math.Min(int.MaxValue, maxBytes / bytesPerFrame))
                    : CalculatePreRecordBufferCapacityFrames();
                _preRecordDroppedFrames = 0;
                _preRecordBufferHasWrapped = false;
                _preRecordRollingTransitionPending = false;

                while (_preRecordFrames.First != null && _preRecordBytes > maxBytes)
                {
                    PreRecordFrame old = _preRecordFrames.First.Value;
                    _preRecordFrames.RemoveFirst();
                    _preRecordBytes -= old.Bytes;
                    old.Frame.Dispose();
                    removedFrames++;
                }

                if (removedFrames > 0)
                {
                    _preRecordDroppedFrames = removedFrames;
                    _preRecordBufferHasWrapped = true;
                }
                capacityFrames = _preRecordDisplayCapacityFrames;
            }
            RuntimeLog.Info(
                "Recording",
                $"Pre-record capacity refreshed after config change capacityFrames={capacityFrames}, removedFrames={removedFrames}, maxBytes={maxBytes}");
            PublishPreRecordBufferStatus(force: true);
        }

        private void PublishPreRecordBufferStatus(bool force = false)
        {
            long version = force
                ? Interlocked.Increment(ref _preRecordUiPublishVersion)
                : Volatile.Read(ref _preRecordUiPublishVersion);
            if (!force
                && Stopwatch.GetElapsedTime(Volatile.Read(ref _lastPreRecordUiPublishTicks)).TotalMilliseconds < 250)
                return;
            long now = Stopwatch.GetTimestamp();
            if (force)
                Interlocked.Exchange(ref _lastPreRecordUiPublishTicks, now);
            else
            {
                long last = Volatile.Read(ref _lastPreRecordUiPublishTicks);
                if (Interlocked.CompareExchange(ref _lastPreRecordUiPublishTicks, now, last) != last)
                    return;
            }
            if (force)
                Interlocked.Exchange(ref _preRecordUiPublishQueued, 0);
            if (Interlocked.Exchange(ref _preRecordUiPublishQueued, 1) != 0)
                return;

            int frameCount;
            long bytes;
            lock (_eventBufferLock)
            {
                frameCount = _preRecordFrames.Count;
                bytes = _preRecordBytes;
                if (_preRecordDisplayCapacityFrames <= 0)
                    _preRecordDisplayCapacityFrames = CalculatePreRecordBufferCapacityFrames();
                if (frameCount > _preRecordDisplayCapacityFrames)
                    _preRecordDisplayCapacityFrames = frameCount;
            }
            int capacity = _preRecordDisplayCapacityFrames;
            long maxBytes = GetPreRecordBufferMaxBytes();
            bool enabled = Config?.EnableEventRecordingBuffer == true && maxBytes > 0;
            bool full = enabled && _preRecordBufferHasWrapped;
            int rollingThresholdFrames = capacity > 0
                ? Math.Max(1, (int)Math.Ceiling(capacity * 0.6))
                : 0;
            bool thresholdReached = enabled && rollingThresholdFrames > 0 && frameCount >= rollingThresholdFrames;
            bool rolling = enabled && (full || _preRecordRollingTransitionPending);
            if (thresholdReached && !rolling && !full)
                _preRecordRollingTransitionPending = true;
            double progress = enabled && capacity > 0
                ? Math.Clamp(frameCount * 100d / rollingThresholdFrames, 0, 100)
                : 0;
            int configuredFps = Config?.Fps ?? 0;
            if (configuredFps <= 0)
                configuredFps = _actualCameraFps;
            double bufferedSeconds = enabled
                ? CalculatePreRecordBufferedSeconds(frameCount, configuredFps)
                : 0;
            bool capturing = enabled && ShouldCaptureEventRecordingBufferFrame();
            string status = !enabled
                ? AppLanguage.Get("Main.PreRecord.Disabled")
                : capturing
                    ? AppLanguage.Format("Main.PreRecord.Status", bufferedSeconds)
                    : AppLanguage.Get("Main.PreRecord.Waiting");
            string frameSummary = enabled
                ? AppLanguage.Format("Main.PreRecord.FrameSummary", frameCount, capacity)
                : AppLanguage.Format("Main.PreRecord.FrameSummary", 0, 0);

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                Interlocked.Exchange(ref _preRecordUiPublishQueued, 0);
                return;
            }
            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_isDisposed || version != Volatile.Read(ref _preRecordUiPublishVersion)) return;
                    PreRecordBufferFrameCount = frameCount;
                    PreRecordBufferCapacityFrames = capacity;
                    PreRecordBufferProgress = progress;
                    IsPreRecordBufferFull = full;
                    IsPreRecordBufferRolling = rolling;
                    PreRecordBufferStatusText = status;
                    PreRecordBufferFrameSummaryText = frameSummary;
                }
                finally
                {
                    Interlocked.Exchange(ref _preRecordUiPublishQueued, 0);
                }
            });
        }

        private int CalculatePreRecordBufferCapacityFrames()
        {
            long maxBytes = GetPreRecordBufferMaxBytes();
            int width = _actualCameraWidth > 0 ? _actualCameraWidth : Config.FrameWidth;
            int height = _actualCameraHeight > 0 ? _actualCameraHeight : Config.FrameHeight;
            long bytesPerFrame = (long)Math.Max(1, width) * Math.Max(1, height) * 3;
            return maxBytes <= 0 ? 0 : Math.Max(1, (int)Math.Min(int.MaxValue, maxBytes / bytesPerFrame));
        }

        internal static double CalculatePreRecordBufferedSeconds(int frameCount, int configuredFps)
        {
            if (frameCount <= 0 || configuredFps <= 0)
                return 0;
            return frameCount / (double)configuredFps;
        }

        private void ClearPendingEventRecordingFrames()
        {
            List<Mat>? pending = _pendingPreRecordFrames;
            _pendingPreRecordFrames = null;
            _pendingPreRecordTimestamps = null;
            _pendingPreRecordStartTime = null;
            if (pending == null) return;
            foreach (Mat frame in pending)
            {
                try { frame.Dispose(); } catch { }
            }
        }

        private long GetPreRecordBufferMaxBytes()
        {
            return CalculatePreRecordBufferMaxBytes(
                Config.PreRecordBufferMB,
                PreRecordBufferPolicy.GetPhysicalMemoryBytes());
        }

        internal static long CalculatePreRecordBufferMaxBytes(int configuredMb, ulong physicalMemoryBytes)
        {
            int maximumMb = PreRecordBufferPolicy.GetRamMaximumMb(physicalMemoryBytes);
            long configuredBytes = (long)Math.Clamp(configuredMb, 0, maximumMb) * 1024L * 1024L;
            return Math.Clamp(configuredBytes, 0, PreRecordBufferHardMaxBytes);
        }

        private double EstimatePreRecordSeconds()
        {
            double width = _actualCameraWidth > 0 ? _actualCameraWidth : Config.FrameWidth;
            double height = _actualCameraHeight > 0 ? _actualCameraHeight : Config.FrameHeight;
            double fps = GetEffectiveRecordingFps();
            if (width <= 0 || height <= 0 || fps <= 0)
                return 0;
            return GetPreRecordBufferMaxBytes() / (width * height * 3d * fps);
        }

    }
}
