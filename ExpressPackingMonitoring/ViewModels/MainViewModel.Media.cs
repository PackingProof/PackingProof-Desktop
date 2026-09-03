#nullable disable
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Input;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Localization;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;
using AForge.Video;
using AForge.Video.DirectShow;
using ExpressPackingMonitoring.Services;
using System.Drawing;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        private void OpenStatsWindow()
        {
            if (ActivateExistingWindow(_statisticsWindow))
                return;

            var statsWindow = new StatisticsWindow(_db);
            _statisticsWindow = statsWindow;
            statsWindow.Closed += (_, _) =>
            {
                if (ReferenceEquals(_statisticsWindow, statsWindow))
                    _statisticsWindow = null;
            };
            statsWindow.Show();
        }

        private void OpenPlaybackWindow()
        {
            if (ActivateExistingWindow(_playbackWindow))
                return;

            string folderPath;
            try
            {
                folderPath = ResolveBestStoragePath();
                if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
            }
            catch (Exception ex)
            {
                AppDialog.Error(null, $"无法访问存储路径：{ex.Message}", "存储错误");
                return;
            }

            try
            {
                VideoFolderImportService importService = CreateVideoImportService(folderPath);
                var playbackWindow = new PlaybackWindow(
                    folderPath,
                    _db,
                    Config.ShowDeletedVideos,
                    importService,
                    Config.LastVideoImportFolder,
                    saveImportFolder: path =>
                    {
                        Config.LastVideoImportFolder = path;
                        SaveConfig();
                    },
                    videosImported: () =>
                    {
                        _recordingTransferService?.EnqueueCompletedRecordings();
                        RefreshRecordingTransferSummary();
                        RunRecordingCacheCleanup();
                    },
                    localComputerName: Config.NodeName);
                _playbackWindow = playbackWindow;
                playbackWindow.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_playbackWindow, playbackWindow))
                        _playbackWindow = null;
                };
                playbackWindow.Show();
            }
            catch (Exception ex)
            {
                _playbackWindow = null;
                AppDialog.Error(null, $"打开回放窗口失败：{ex.Message}", "回放错误");
            }
        }

        private static bool ActivateExistingWindow(System.Windows.Window window)
        {
            if (window == null)
                return false;

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
            return true;
        }

        /// <summary>
        /// 批量将数据库中的旧 MKV 文件转换为 MP4（无损容器转换）
        /// </summary>
        public async Task<MkvBatchConversionResult> BatchConvertMkvToMp4Async(
            IProgress<string> progress,
            CancellationToken token,
            bool forceRetry = false)
        {
            await _mkvBatchLock.WaitAsync(token);
            try
            {
                return await BatchConvertMkvToMp4CoreAsync(progress, token, forceRetry);
            }
            finally
            {
                _mkvBatchLock.Release();
            }
        }

        private async Task<MkvBatchConversionResult> BatchConvertMkvToMp4CoreAsync(
            IProgress<string> progress,
            CancellationToken token,
            bool forceRetry)
        {
            var batchResult = new MkvBatchConversionResult();
            if (_db == null) return batchResult;

            string ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                progress?.Report("未找到 FFmpeg，无法执行转换");
                return batchResult;
            }

            var mkvPaths = GetMkvConversionTargets();
            int total = mkvPaths.Count;
            var failedPaths = new List<string>();

            for (int i = 0; i < total; i++)
            {
                if (token.IsCancellationRequested) break;

                string mkvPath = mkvPaths[i];
                batchResult.MarkProcessedSource(mkvPath);
                string mp4Path = Path.ChangeExtension(mkvPath, ".mp4");
                string fileName = Path.GetFileName(mkvPath);
                MkvConversionFailureState failureState = _db.GetMkvConversionFailureState(mkvPath);

                // 如果 MKV 已不存在但 MP4 存在，只更新数据库
                if (!File.Exists(mkvPath))
                {
                    if (File.Exists(mp4Path)
                        && ValidateConvertedMp4(ffmpegPath, mp4Path, requireAudio: false, audioLogPath: null, cancellationToken: token))
                    {
                        DeleteAudioTempFile(Path.ChangeExtension(mkvPath, ".wav"));
                        _db.UpdateVideoFilePath(mkvPath, mp4Path);
                        _archiveService?.Wake();
                        batchResult.SuccessCount++;
                        batchResult.AddFinalFile(mkvPath, mp4Path);
                        progress?.Report($"[{i + 1}/{total}] 已更新数据库: {fileName}");
                    }
                    else
                    {
                        batchResult.SkippedCount++;
                        progress?.Report($"[{i + 1}/{total}] 文件不存在，跳过: {fileName}");
                    }
                    continue;
                }

                // 如果 MP4 已存在，直接删 MKV 并更新数据库；但带 WAV/audio.log 的 MKV 需要重新合并，避免误用录制中生成的半截 MP4。
                if (File.Exists(mp4Path)
                    && ValidateConvertedMp4(ffmpegPath, mp4Path, requireAudio: false, audioLogPath: null, cancellationToken: token))
                {
                    if (HasMuxRecoverySidecar(mkvPath))
                    {
                        RuntimeLog.Warn("MkvRecover", $"Existing MP4 ignored because MKV sidecar remains file={fileName}");
                        progress?.Report($"[{i + 1}/{total}] 发现疑似半截 MP4，重新合并: {fileName}");
                    }
                    else
                    {
                        try { File.Delete(mkvPath); } catch { }
                        DeleteAudioTempFile(Path.ChangeExtension(mkvPath, ".wav"));
                        _db.UpdateVideoFilePath(mkvPath, mp4Path);
                        _archiveService?.Wake();
                        batchResult.SuccessCount++;
                        batchResult.AddFinalFile(mkvPath, mp4Path);
                        progress?.Report($"[{i + 1}/{total}] MP4 已存在，已清理 MKV: {fileName}");
                        continue;
                    }
                }

                // 历史失败记录在完成文件对账后直接进入终态，不再后台重复转换。
                MkvAutomaticRetryDecision retryDecision =
                    MkvConversionRetryPolicy.GetAutomaticRetryDecision(failureState, DateTime.Now);
                if (!forceRetry && retryDecision == MkvAutomaticRetryDecision.Suppressed)
                {
                    batchResult.SuppressedCount++;
                    if (File.Exists(mkvPath))
                        batchResult.AddFinalFile(mkvPath, mkvPath);
                    _db.MarkArchivePendingByFilePath(mkvPath);
                    _archiveService?.Wake();
                    progress?.Report($"[{i + 1}/{total}] 未生成兼容 MP4，已保留原始录像: {fileName}");
                    continue;
                }

                progress?.Report($"[{i + 1}/{total}] 正在转换: {fileName}");

                MkvConversionResult conversionResult = await Task.Run(() =>
                {
                    var result = ConvertMkvToMp4ForPlayback(mkvPath, token);
                    if (!result.Success)
                        RuntimeLog.Warn("MkvRecover", $"Convert failed file={fileName}, error={result.ErrorMessage}");
                    return result;
                }, token);

                if (conversionResult.Success)
                {
                    try { File.Delete(mkvPath); } catch { }
                    _db.ClearMkvConversionFailure(mkvPath);
                    _db.UpdateVideoFilePath(mkvPath, mp4Path);
                    _archiveService?.Wake();
                    batchResult.SuccessCount++;
                    batchResult.AddFinalFile(
                        mkvPath,
                        string.IsNullOrWhiteSpace(conversionResult.FilePath)
                            ? mp4Path
                            : conversionResult.FilePath);
                    progress?.Report($"[{i + 1}/{total}] 转换成功: {fileName}");
                }
                else
                {
                    DateTime failedAt = DateTime.Now;
                    _db.RecordMkvConversionFailure(mkvPath, failedAt, conversionResult.ErrorMessage);
                    batchResult.FailureCount++;
                    string retainedPath = !string.IsNullOrWhiteSpace(conversionResult.FilePath)
                        && File.Exists(conversionResult.FilePath)
                            ? conversionResult.FilePath
                            : mkvPath;
                    if (File.Exists(retainedPath))
                        batchResult.AddFinalFile(mkvPath, retainedPath);
                    failedPaths.Add(mkvPath);
                    if (MkvConversionRetryPolicy.GetAutomaticRetryDecision(
                            _db.GetMkvConversionFailureState(mkvPath),
                            failedAt) == MkvAutomaticRetryDecision.Suppressed)
                    {
                        batchResult.SuppressedCount++;
                        _db.MarkArchivePendingByFilePath(mkvPath);
                        _archiveService?.Wake();
                    }
                    progress?.Report($"[{i + 1}/{total}] 转换失败: {fileName}");
                }
            }

            if (!forceRetry && failedPaths.Count > 0)
                batchResult.NotificationCount = _db.ClaimMkvFailureNotifications(failedPaths, DateTime.Now);

            return batchResult;
        }

        private void ShowMkvFailureToastIfNeeded(MkvBatchConversionResult result)
        {
            if (result?.ShouldNotify != true)
                return;

            _ = Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!_isDisposed)
                {
                    ShowToast(
                        $"有 {result.NotificationCount} 个录像未生成兼容 MP4，原始录像已保留，可在维护工具中查看",
                        ToastSeverity.Warning);
                }
            });
        }

        private void VerifyCompletedRecordingSpecifications(MkvBatchConversionResult result)
        {
            if (result == null || result.ProcessedSources.Count == 0)
                return;

            string ffmpegPath = FindFFmpeg();
            foreach (string sourcePath in result.ProcessedSources)
            {
                MkvFinalizedFile finalizedFile = result.FinalFiles.FirstOrDefault(item =>
                    string.Equals(item.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));
                if (finalizedFile == null)
                    continue;

                if (!CompletedVideoSpecificationProbe.TryRead(
                        ffmpegPath,
                        finalizedFile.FinalPath,
                        out CompletedVideoMetadata actual))
                {
                    continue;
                }

                _db?.UpdateVideoDurationByFilePath(sourcePath, actual.DurationSeconds);
                if (!string.Equals(sourcePath, finalizedFile.FinalPath, StringComparison.OrdinalIgnoreCase))
                    _db?.UpdateVideoDurationByFilePath(finalizedFile.FinalPath, actual.DurationSeconds);

                if (!_pendingRecordingSpecificationChecks.TryRemove(
                    sourcePath,
                    out ExpectedRecordingSpecification expected))
                {
                    continue;
                }

                // 尽力而为：下一单已经开录时直接跳过，不与实时录制竞争资源，也不轮询补做。
                if (IsRecording || _isDisposed)
                    continue;

                CompletedVideoSpecificationEvaluation evaluation =
                    CompletedVideoSpecificationProbe.Evaluate(expected, actual);
                if (!evaluation.ShouldEvaluate || evaluation.MeetsSpecification)
                    continue;

                RuntimeLog.Warn(
                    "RecordingProfile",
                    $"completed file={Path.GetFileName(finalizedFile.FinalPath)}, expected={expected.Width}x{expected.Height}@{expected.Fps}/{expected.DurationSeconds:F1}s, actual={actual.Width}x{actual.Height}@{actual.AverageFrameRate:F2}/{actual.DurationSeconds:F1}s, reason={evaluation.Reason}");
                _alertService?.Publish(new AlertRequest
                {
                    Message = "实际录制规格未达到设置值，可到“设置 → 高级设置 → 检测并推荐录制规格”获取推荐，或在录制设置中自行降低分辨率或帧率",
                    Priority = AlertPriority.Normal,
                    Sound = AlertSound.None,
                    DisplayDuration = TimeSpan.FromSeconds(5),
                    DeduplicationKey = "recording-specification-below-target",
                    DeduplicationWindow = TimeSpan.FromMinutes(30)
                });
            }
        }

        private List<string> GetMkvConversionTargets()
        {
            var paths = _db?.QueryActiveVideoFilePaths() ?? [];
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                if (path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                {
                    if (HasMkvConversionCandidate(path))
                        targets.Add(path);
                    continue;
                }

                if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    continue;

                string mkvPath = Path.ChangeExtension(path, ".mkv");
                if (File.Exists(mkvPath) && HasMuxRecoverySidecar(mkvPath))
                {
                    RuntimeLog.Warn("MkvRecover", $"Database points to MP4 but MKV sidecar remains, scheduling recovery file={Path.GetFileName(mkvPath)}");
                    targets.Add(mkvPath);
                }
            }

            return targets.ToList();
        }

        private ArchiveLoadState GetArchiveLoadState()
        {
            if (Volatile.Read(ref _isRecording))
                return ArchiveLoadState.Paused;

            long nowTicks = DateTime.UtcNow.Ticks;
            long heartbeatTicks = Interlocked.Read(ref _archiveUiHeartbeatUtcTicks);
            if (heartbeatTicks > 0
                && nowTicks - heartbeatTicks > UiHeartbeatStaleThreshold.Ticks)
            {
                return ArchiveLoadState.Degraded;
            }

            if (Volatile.Read(ref _archiveCameraActive) == 0)
                return ArchiveLoadState.Healthy;

            long frameTicks = Interlocked.Read(ref _archiveFrameUtcTicks);
            long previewTicks = Interlocked.Read(ref _archivePreviewUtcTicks);
            return (frameTicks > 0
                    && nowTicks - frameTicks > PreviewFreezeWarnThreshold.Ticks)
                || (previewTicks > 0
                    && nowTicks - previewTicks > PreviewFreezeWarnThreshold.Ticks)
                ? ArchiveLoadState.Degraded
                : ArchiveLoadState.Healthy;
        }

        internal static bool HasMkvConversionCandidate(string mkvPath)
        {
            if (string.IsNullOrWhiteSpace(mkvPath)
                || !mkvPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return File.Exists(mkvPath)
                || File.Exists(Path.ChangeExtension(mkvPath, ".mp4"));
        }

        private void InitializeSystem()
        {
            _cts = new CancellationTokenSource();
            _lastActivityTime = DateTime.Now;
            RuntimeLog.Info("System", "InitializeSystem");
            StartCamera();
            _videoTask = Task.Run(() => VideoProcessLoop(_cts.Token), _cts.Token);
            Task.Run(CheckDiskAndCleanup);
            _cameraIdleWatchdogTask = Task.Run(
                () => CameraIdleWatchdogAsync(_cts.Token),
                _cts.Token);
            // 正常启动时发现监听权限或防火墙规则缺失，会立即请求管理员授权。
            // 自动化临时运行模式禁用该系统级变更。
            _webServerStartupTask = RestartWebServerAsync(
                allowAccessSetup: ShouldRepairLanAccessAtStartup(Config, AllowLanAccessSetupOnStartup));

            // 启动时自动将上次断电残留的 MKV 转换为 MP4
            _mkvRecoveryTask = Task.Run(RecoverOrphanedMkvAsync);
        }

        internal static bool ShouldRepairLanAccessAtStartup(
            AppConfig config,
            bool allowLanAccessSetup = true)
        {
            if (config == null || !allowLanAccessSetup)
                return false;
            return config.EnableWebServer
                || string.Equals(
                    config.DeploymentPreset,
                    DeploymentPresets.RecordingWorkstation,
                    StringComparison.OrdinalIgnoreCase);
        }

        private async Task RecoverOrphanedMkvAsync()
        {
            try
            {
                var result = await BatchConvertMkvToMp4Async(
                    new Progress<string>(msg => Debug.WriteLine($"[MkvRecover] {msg}")),
                    CancellationToken.None);

                if (result.SuccessCount > 0 || result.FailureCount > 0)
                {
                    Debug.WriteLine($"[MkvRecover] 启动恢复完成: 成功={result.SuccessCount}, 失败={result.FailureCount}, 跳过={result.SkippedCount}, 暂缓={result.DeferredCount}, 静默={result.SuppressedCount}");
                    if (result.SuccessCount > 0)
                    {
                        _ = Application.Current.Dispatcher.BeginInvoke(() =>
                            ShowToast($"已恢复 {result.SuccessCount} 个断电残留视频"));
                    }
                    // 启动对账静默处理历史记录，不重复提醒用户旧版本失败。
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MkvRecover] 异常: {ex.Message}");
            }
        }

        public async Task<bool> SaveRecordingsBeforeShutdownAsync(IProgress<string> progress = null)
        {
            if (_isDisposed) return true;
            if (!await _shutdownLock.WaitAsync(0)) return false;

            bool previousBusy = IsBusy;
            string previousBusyText = BusyText;
            bool prepared = false;
            _shutdownRequested = true;
            IsShutdownInProgress = true;
            try
            {
                RuntimeLog.Info("Shutdown", $"Save before shutdown start recording={IsRecording}");
                progress?.Report("正在保存录像，请稍候...");
                IsBusy = true;
                BusyText = "正在关闭程序...";

                if (IsRecording)
                {
                    progress?.Report("正在停止当前录像...");
                    await _recorderLock.WaitAsync();
                    try
                    {
                        if (IsRecording)
                        {
                            _stopReason = "程序退出";
                            PauseSpeechForRecording();
                            await InternalStopRecordingAsync();
                        }
                    }
                    finally
                    {
                        if (!IsRecording)
                            ResumeSpeechWhenCameraIdle();
                        _recorderLock.Release();
                    }
                }

                if (_lastFinalizeTask != null)
                {
                    progress?.Report("正在写入录像记录...");
                    await _lastFinalizeTask;
                }

                if (_postStopMuxTask != null && !_postStopMuxTask.IsCompleted)
                {
                    progress?.Report("正在等待当前录像合成...");
                    await _postStopMuxTask;
                }

                if (_mkvRecoveryTask != null && !_mkvRecoveryTask.IsCompleted)
                {
                    progress?.Report("正在等待后台录像恢复...");
                    await _mkvRecoveryTask;
                }

                progress?.Report("正在合成 MP4 录像...");
                var result = await BatchConvertMkvToMp4Async(progress, CancellationToken.None);
                RuntimeLog.Info("Shutdown", $"Save before shutdown done success={result.SuccessCount}, fail={result.FailureCount}, skip={result.SkippedCount}, deferred={result.DeferredCount}, suppressed={result.SuppressedCount}");

                if (result.ShouldNotify)
                {
                    ShowMkvFailureToastIfNeeded(result);
                    RuntimeLog.Warn("Shutdown", $"Save before shutdown has failed historical conversions, allowing exit. failedConversions={result.FailureCount}");
                }

                _shutdownPrepared = true;
                prepared = true;
                return true;
            }
            catch (Exception ex)
            {
                ShowToast("录像保存失败，请检查日志", ToastSeverity.Error);
                RuntimeLog.Error("Shutdown", "Save before shutdown exception", ex);
                return false;
            }
            finally
            {
                if (!prepared)
                {
                    _shutdownRequested = false;
                    IsShutdownInProgress = false;
                    IsBusy = previousBusy && !_isDisposed;
                    BusyText = previousBusy ? previousBusyText : "";
                }
                else
                {
                    IsBusy = true;
                    BusyText = "正在关闭程序...";
                }
                _shutdownLock.Release();
            }
        }

    }
}
