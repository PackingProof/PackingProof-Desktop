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
        private void RestartCamera()
        {
            if (_isSetupWizardActive || _isDisposed || _shutdownRequested)
            {
                RuntimeLog.Info("Camera", "RestartCamera skipped while setup wizard owns camera");
                return;
            }

            // 阻止并发重启
            if (_isRestartingCamera) return;
            _isRestartingCamera = true;
            try
            {
                RuntimeLog.Warn("Camera", $"RestartCamera start recording={IsRecording}, failures={_consecutiveRestartFailures}");
                if (!StopCamera())
                {
                    RuntimeLog.Warn("Camera", "RestartCamera aborted because previous camera did not stop");
                    ShowToast("摄像头停止失败，请重新插拔后重试", ToastSeverity.Error);
                    return;
                }
                StartCamera();
                _lastRestartAttempt = DateTime.Now;
                RuntimeLog.Info("Camera", $"RestartCamera done running={IsVideoSourceRunning()}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Camera", "RestartCamera failed", ex);
                throw;
            }
            finally
            {
                _isRestartingCamera = false;
            }
        }

        private async Task RestartCameraWithRecordingStopAsync(string trigger)
        {
            if (_isSetupWizardActive || _isDisposed || _shutdownRequested)
            {
                RuntimeLog.Info("Camera", "RestartCameraWithRecordingStop skipped while setup wizard owns camera");
                return;
            }

            if (_isRestartingCamera) return;
            _isRestartingCamera = true;
            try
            {
                RuntimeLog.Warn("Camera", $"RestartCameraWithRecordingStop start trigger={trigger}, recording={IsRecording}, failures={_consecutiveRestartFailures}");
                if (IsRecording)
                {
                    _stopReason = "摄像头重连";
                    RuntimeLog.Warn("Camera", "Camera reconnect requested while recording, stopping current recording before restart");
                    await SafeStopRecordingAsync();

                    if (!StopCamera())
                    {
                        ShowToast("摄像头停止失败，未继续重连", ToastSeverity.Error);
                        return;
                    }
                    StartCamera();
                    _lastRestartAttempt = DateTime.Now;

                    if (IsCameraStreamReady())
                    {
                        _consecutiveRestartFailures = 0;
                        RuntimeLog.Info("Camera", "Camera reconnected after stopping interrupted recording");
                        ShowToast("摄像头已重连，当前录像已保存，请重新扫码继续");
                        Speak(DefaultSpeechCatalog.CameraConnected);
                    }
                    else
                    {
                        _consecutiveRestartFailures++;
                        if (_consecutiveRestartFailures >= MaxConsecutiveRestartFailures)
                        {
                            RuntimeLog.Warn("Camera", $"Camera reconnect failed {_consecutiveRestartFailures} times after interrupted recording");
                            ShowToast($"摄像头连续 {MaxConsecutiveRestartFailures} 次重连失败，录制已停止。请重新插拔后在设置中手动重启", ToastSeverity.Error);
                            SpeakWarning(DefaultSpeechCatalog.ReconnectCamera, 3);
                            Debug.WriteLine($"[Camera] 录制中连续 {_consecutiveRestartFailures} 次重连失败，停止录制和自动重连");
                        }
                        else
                        {
                            SpeakWarning(DefaultSpeechCatalog.CameraDisconnected);
                        }
                    }
                }
                else
                {
                    // 非录制状态：原有逻辑
                    if (!StopCamera())
                    {
                        ShowToast("摄像头停止失败，未继续重连", ToastSeverity.Error);
                        return;
                    }
                    StartCamera();
                    _lastRestartAttempt = DateTime.Now;

                    if (IsCameraStreamReady())
                    {
                        _consecutiveRestartFailures = 0;
                        RuntimeLog.Info("Camera", "Camera reconnected while idle");
                    }
                    else
                    {
                        _consecutiveRestartFailures++;
                        RuntimeLog.Warn("Camera", $"Camera reconnect failed while idle, failures={_consecutiveRestartFailures}");
                        if (_consecutiveRestartFailures >= MaxConsecutiveRestartFailures)
                        {
                            ShowToast($"摄像头连续 {MaxConsecutiveRestartFailures} 次重连失败，已停止自动重连。请重新插拔后在设置中手动重启", ToastSeverity.Error);
                            SpeakWarning(DefaultSpeechCatalog.ReconnectCamera, 3);
                            Debug.WriteLine($"[Camera] 连续 {_consecutiveRestartFailures} 次重连失败，停止自动重连");
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                RuntimeLog.Error("Camera", $"Camera restart failed, trigger={trigger}", exception);
            }
            finally
            {
                _isRestartingCamera = false;
            }
        }

        /// <summary>用户手动触发摄像头重置（在设置或 UI 按钮调用）</summary>
        public void ManualRestartCamera()
        {
            _consecutiveRestartFailures = 0;
            RestartCamera();
        }

        /// <summary>
        /// 注册用户活跃信号（扫码/鼠标/键盘/按钮等），如果摄像头休眠中则唤醒
        /// </summary>
        public void NotifyUserActivity()
        {
            _lastActivityTime = DateTime.Now;
            if (_isSetupWizardActive)
                return;

            if (_isCameraSleeping)
            {
                IsCameraSleeping = false;
                _consecutiveRestartFailures = 0;
                RuntimeLog.Info("Camera", "Wake requested by user activity");
                StartCamera();
                ShowToast("摄像头已唤醒");
                Debug.WriteLine("[Idle] 用户活跃，摄像头唤醒");
            }
            else if (_consecutiveRestartFailures >= MaxConsecutiveRestartFailures)
            {
                // 用户活动时如果摄像头已停止自动重连，重置并再试一次
                _consecutiveRestartFailures = 0;
                Debug.WriteLine("[Camera] 用户活动，重置重连计数器并重试");
                RestartCamera();
            }
        }

        private async Task CameraIdleWatchdogAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!_isDisposed)
                {
                    await Task.Delay(10_000, cancellationToken); // 每10秒检查一次
                    if (_isDisposed || _shutdownRequested) break;
                    if (!Config.EnableCameraIdle || Config.CameraIdleMinutes <= 0) continue;
                    if (_isSetupWizardActive) continue;
                    if (IsRecording || _isCameraSleeping) continue;

                    double idleMinutes = (DateTime.Now - _lastActivityTime).TotalMinutes;
                    if (idleMinutes >= Config.CameraIdleMinutes && !Config.IsCameraIdleNoSleepTime(DateTime.Now))
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() => {
                            if (_isDisposed
                                || _shutdownRequested
                                || _isCameraSleeping
                                || IsRecording
                                || _isSetupWizardActive
                                || Config.IsCameraIdleNoSleepTime(DateTime.Now)) return; // 再次检查防止竞态和跨入保护时段
                            if (!StopCamera())
                            {
                                ShowToast("摄像头未能进入休眠，请重新插拔后重试", ToastSeverity.Warning);
                                return;
                            }
                            IsCameraSleeping = true; // SetProperty 会同时更新字段并触发 PropertyChanged
                            VideoFrame = null;
                            ShowToast(AppLanguage.Format(
                                "摄像头已休眠（空闲 {0} 分钟），可在设置左侧开启“高级模式”，再到录像设置关闭“长时间不用时关闭摄像头”",
                                Config.CameraIdleMinutes), ToastSeverity.Information);
                            Debug.WriteLine($"[Idle] 摄像头休眠: 空闲{idleMinutes:F1}分钟");
                            RuntimeLog.Info("MkvRecover", "Camera idle, start pending MKV conversion");
                            _mkvRecoveryTask = Task.Run(RecoverOrphanedMkvAsync);
                        });
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                RuntimeLog.Error("Camera", "Camera idle watchdog failed", exception);
            }
        }

        private DateTime _lastFrameTime = DateTime.MinValue;

        private int BeginPreviewSession(bool clearFrame)
        {
            int sessionId = _previewSessionGate.BeginSession();
            _lastPreviewFrameAt = DateTime.MinValue;
            _lastPreviewPublishedAt = DateTime.Now;
            Interlocked.Exchange(ref _archivePreviewUtcTicks, DateTime.UtcNow.Ticks);
            _lastPreviewFreezeLogAt = DateTime.Now;

            if (clearFrame)
            {
                _cameraFrameReady.BeginSession();
                _cameraFrameRateGate.Reset();
                Interlocked.Exchange(ref _cameraSourceLastTimestamp, 0);
                Volatile.Write(ref _cameraSourceFpsEstimate, 0);
                Interlocked.Exchange(ref _cameraSourceSampleCount, 0);
                var dispatcher = Application.Current?.Dispatcher;
                void ClearPreview()
                {
                    if (!_previewSessionGate.IsCurrent(sessionId)) return;
                    _previewWriteableBitmap = null;
                    VideoFrame = null;
                }

                if (dispatcher == null || dispatcher.CheckAccess())
                    ClearPreview();
                else
                    _ = dispatcher.BeginInvoke(new Action(ClearPreview));
            }

            return sessionId;
        }

        private void ReleasePreviewUpdate(int sessionId)
        {
            _previewSessionGate.Release(sessionId);
        }

        private async Task<bool> WaitForCameraFrameAsync(TimeSpan timeout)
        {
            if (_isDisposed)
                return false;

            // 摄像头已在持续采集时直接复用最新帧，避免扫码启动被一次性的就绪信号误判为超时
            lock (_frameLock)
            {
                if (_latestFrame != null && !_latestFrame.IsDisposed && !_latestFrame.Empty())
                    return true;
            }

            if (!await _cameraFrameReady.WaitAsync(timeout))
                return false;

            lock (_frameLock)
            {
                return _latestFrame != null && !_latestFrame.IsDisposed && !_latestFrame.Empty();
            }
        }

        private void StartCamera()
        {
            try
            {
                if (_isSetupWizardActive || _isDisposed || _shutdownRequested)
                {
                    RuntimeLog.Info("Camera", "StartCamera skipped while setup wizard owns camera");
                    return;
                }

                if (_videoSource != null || _networkCameraSource != null)
                {
                    RuntimeLog.Warn("Camera", $"StartCamera skipped because previous source still exists, running={IsVideoSourceRunning()}");
                    return;
                }

                int previewSessionId = BeginPreviewSession(clearFrame: true);
                ClearPreRecordBuffer();
                ClearPendingEventRecordingFrames();

                if (IsNetworkCameraConfigured())
                {
                    StartNetworkCamera(previewSessionId);
                    return;
                }

                var videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                if (videoDevices.Count == 0)
                {
                    RuntimeLog.Warn("Camera", "StartCamera found no video devices");
                    ShowToast("未检测到任何摄像头", ToastSeverity.Warning);
                    SpeakWarning(DefaultSpeechCatalog.CameraNotDetected);
                    return;
                }

                string targetMoniker = Config.CameraMonikerString;
                int targetIndex = -1;

                // 1. 优先通过 MonikerString 查找（精确匹配目标设备）
                if (!string.IsNullOrEmpty(targetMoniker))
                {
                    for (int i = 0; i < videoDevices.Count; i++)
                    {
                        if (videoDevices[i].MonikerString == targetMoniker)
                        {
                            targetIndex = i;
                            break;
                        }
                    }

                    // 目标摄像头已配置但未找到：不切换到其他设备
                    if (targetIndex == -1)
                    {
                        Debug.WriteLine($"[Camera] 目标摄像头未找到: {targetMoniker}，不切换到其他设备");
                        RuntimeLog.Warn("Camera", $"Configured camera missing, moniker={targetMoniker}");
                        ShowToast("目标摄像头未连接，等待重新插入", ToastSeverity.Warning);
                        return;
                    }
                }

                // 2. 首次使用（未配置 MonikerString）：使用索引选择并记录 MonikerString
                if (targetIndex == -1)
                {
                    if (Config.CameraIndex >= 0 && Config.CameraIndex < videoDevices.Count)
                    {
                        targetIndex = Config.CameraIndex;
                        Config.CameraMonikerString = videoDevices[targetIndex].MonikerString;
                    }
                    else
                    {
                        targetIndex = 0;
                        Config.CameraMonikerString = videoDevices[0].MonikerString;
                    }
                }

                _videoSource = new VideoCaptureDevice(videoDevices[targetIndex].MonikerString);
                RuntimeLog.Info("Camera", $"StartCamera selected index={targetIndex}, name={videoDevices[targetIndex].Name}");

                // 加载该摄像头的独立配置
                if (Config.CameraConfigs.TryGetValue(videoDevices[targetIndex].MonikerString, out var settings))
                {
                    Config.FrameWidth = settings.FrameWidth;
                    Config.FrameHeight = settings.FrameHeight;
                    Config.Fps = settings.Fps;
                        Config.AudioDeviceName = settings.AudioDeviceName ?? "";
                        Config.AudioSyncOffsetMs = settings.AudioSyncOffsetMs;
                        Config.CameraRotate180 = settings.Rotate180;
                }

                // 设置错误处理器（摄像头拔掉时 AForge 会触发此事件）
                _videoSource.VideoSourceError += (s, e) => {
                    Debug.WriteLine($"[Camera] 视频源错误: {e.Description}");
                    RuntimeLog.Error("Camera", $"VideoSourceError: {e.Description}");
                    if (_isSetupWizardActive || _isDisposed || _shutdownRequested)
                        return;
                    _ = Application.Current.Dispatcher.InvokeAsync(() => {
                        if (_isSetupWizardActive || _isDisposed || _shutdownRequested)
                            return;
                        ShowToast("摄像头连接发生错误，尝试重连...", ToastSeverity.Warning);
                        _ = RestartCameraWithRecordingStopAsync("video-source-error");
                    });
                };

                // 从摄像头能力中选择最匹配用户配置（分辨率+帧率）的模式
                if (_videoSource.VideoCapabilities.Length > 0)
                {
                    var caps = _videoSource.VideoCapabilities;
                    VideoCapabilities best = caps[0];
                    int bestScore = int.MaxValue;
                    foreach (var cap in caps)
                    {
                        // 分辨率差值权重高，帧率差值权重低
                        int resDiff = Math.Abs(cap.FrameSize.Width - Config.FrameWidth) + Math.Abs(cap.FrameSize.Height - Config.FrameHeight);
                        int fpsDiff = Math.Abs(cap.AverageFrameRate - Config.Fps);
                        int score = resDiff * 10 + fpsDiff;
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = cap;
                        }
                    }
                    _videoSource.VideoResolution = best;
                    _actualCameraWidth = best.FrameSize.Width;
                    _actualCameraHeight = best.FrameSize.Height;
                    _actualCameraFps = best.AverageFrameRate > 0 ? best.AverageFrameRate : Config.Fps;
                }
                else
                {
                    _actualCameraWidth = Config.FrameWidth;
                    _actualCameraHeight = Config.FrameHeight;
                    _actualCameraFps = Config.Fps > 0 ? Config.Fps : 15;
                }
                _videoSource.NewFrame += VideoSource_NewFrame; _videoSource.Start();
                _lastFrameTime = DateTime.Now; // 防止 VideoProcessLoop 启动时误判无帧
                _lastPreviewPublishedAt = DateTime.Now;
                long cameraReadyTicks = DateTime.UtcNow.Ticks;
                Interlocked.Exchange(ref _archiveFrameUtcTicks, cameraReadyTicks);
                Interlocked.Exchange(ref _archivePreviewUtcTicks, cameraReadyTicks);
                Volatile.Write(ref _archiveCameraActive, 1);
                _cameraEverConnected = true;
                RuntimeLog.Info("Camera", $"StartCamera success {_actualCameraWidth}x{_actualCameraHeight}@{_actualCameraFps}, configured={Config.FrameWidth}x{Config.FrameHeight}@{Config.Fps}, running={_videoSource.IsRunning}, previewSession={previewSessionId}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Camera", "StartCamera failed", ex);
                ShowToast("摄像头启动失败", ToastSeverity.Error);
            }
        }

        private bool IsNetworkCameraConfigured()
        {
            return string.Equals(Config.CameraSourceKind, "network", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(Config.NetworkCameraUrl);
        }

        private void StartNetworkCamera(int previewSessionId)
        {
            if (!NetworkCameraUrlPolicy.TryNormalize(Config.NetworkCameraUrl, out string url, out string error))
            {
                RuntimeLog.Warn("Camera", $"Network camera URL rejected: {error}");
                ShowToast($"网络摄像头地址无效：{error}", ToastSeverity.Error);
                return;
            }

            var source = new NetworkCameraSource(
                url,
                Config.NetworkCameraRtspTransport,
                Config.Fps > 0 ? Config.Fps : 15);
            source.StreamInfoReady += NetworkCameraSource_StreamInfoReady;
            source.FrameReady += NetworkCameraSource_FrameReady;
            source.SourceError += NetworkCameraSource_SourceError;

            bool started = source.Start();
            if (!started)
            {
                RuntimeLog.Warn("Camera", $"StartNetworkCamera failed: {source.LastError}");
                ShowToast($"网络摄像头连接失败：{source.LastError}", ToastSeverity.Error);
                source.Dispose();
                return;
            }

            _networkCameraSource = source;
            _networkCameraStartedAt = DateTime.Now;
            _lastFrameTime = DateTime.Now;
            _lastPreviewPublishedAt = DateTime.Now;
            long networkCameraReadyTicks = DateTime.UtcNow.Ticks;
            Interlocked.Exchange(ref _archiveFrameUtcTicks, networkCameraReadyTicks);
            Interlocked.Exchange(ref _archivePreviewUtcTicks, networkCameraReadyTicks);
            Volatile.Write(ref _archiveCameraActive, 1);
            _cameraEverConnected = true;
            RuntimeLog.Info(
                "Camera",
                $"StartNetworkCamera url={NetworkCameraUrlPolicy.SanitizeForLog(url)}, transport={Config.NetworkCameraRtspTransport}, previewSession={previewSessionId}");
        }

        private void NetworkCameraSource_StreamInfoReady(object sender, NetworkCameraStreamInfoEventArgs e)
        {
            _actualCameraWidth = e.Width;
            _actualCameraHeight = e.Height;
            _actualCameraFps = e.Fps;
            RuntimeLog.Info("Camera", $"Network camera stream ready {e.Width}x{e.Height}@{e.Fps}");
        }

        private void NetworkCameraSource_SourceError(object sender, NetworkCameraErrorEventArgs e)
        {
            RuntimeLog.Error("Camera", $"NetworkCameraSourceError: {e.Description}");
            if (_isSetupWizardActive || _isDisposed || _shutdownRequested)
                return;
            if ((DateTime.Now - _lastRestartAttempt).TotalSeconds < MinRestartIntervalSeconds)
                return;

            _ = Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_isSetupWizardActive || _isDisposed || _shutdownRequested)
                    return;
                ShowToast("网络摄像头连接异常，尝试重连...", ToastSeverity.Warning);
                _ = RestartCameraWithRecordingStopAsync("network-source-error");
            });
        }

        private bool StopCamera()
        {
            if (!_isDisposed)
                ResetCameraBarcodeRecognition();

            NetworkCameraSource networkSource = _networkCameraSource;
            if (networkSource != null)
            {
                networkSource.StreamInfoReady -= NetworkCameraSource_StreamInfoReady;
                networkSource.FrameReady -= NetworkCameraSource_FrameReady;
                networkSource.SourceError -= NetworkCameraSource_SourceError;
                try
                {
                    networkSource.Stop();
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("Camera", $"Network camera stop failed: {ex.Message}");
                }
                if (ReferenceEquals(_networkCameraSource, networkSource))
                    _networkCameraSource = null;
                lock (_frameLock) { _latestFrame?.Dispose(); _latestFrame = null; }
                ClearPreRecordBuffer();
                ClearPendingEventRecordingFrames();
                BeginPreviewSession(clearFrame: true);
                RuntimeLog.Info("Camera", "StopNetworkCamera completed");
                return true;
            }

            VideoCaptureDevice source = _videoSource;
            if (source != null)
            {
                RuntimeLog.Info("Camera", $"StopCamera running={source.IsRunning}");
                try { source.NewFrame -= VideoSource_NewFrame; } catch { }
                try
                {
                    if (source.IsRunning)
                    {
                        source.SignalToStop();
                        for (int i = 0; i < 50 && source.IsRunning; i++)
                            Thread.Sleep(100);
                    }
                }
                catch (SEHException) { /* AForge COM cleanup on some laptops */ }
                catch (Exception ex) { RuntimeLog.Warn("Camera", $"Graceful camera stop failed: {ex.Message}"); }

                if (source.IsRunning)
                {
                    RuntimeLog.Warn("Camera", "Graceful camera stop timed out, forcing stop");
                    if (_cameraForceStopTask == null || _cameraForceStopTask.IsCompleted)
                        _cameraForceStopTask = Task.Run(() => source.Stop());

                    try { _cameraForceStopTask.Wait(2000); }
                    catch (Exception ex) { RuntimeLog.Warn("Camera", $"Forced camera stop failed: {ex.GetBaseException().Message}"); }
                }

                if (source.IsRunning)
                {
                    RuntimeLog.Error("Camera", "Camera source is still running after forced stop");
                    return false;
                }

                if (ReferenceEquals(_videoSource, source))
                    _videoSource = null;
                _cameraForceStopTask = null;
            }
            lock (_frameLock) { _latestFrame?.Dispose(); _latestFrame = null; }
            ClearPreRecordBuffer();
            ClearPendingEventRecordingFrames();
            BeginPreviewSession(clearFrame: true);
            Volatile.Write(ref _archiveCameraActive, 0);
            RuntimeLog.Info("Camera", "StopCamera completed");
            return true;
        }

        private void VideoSource_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            _lastFrameTime = DateTime.Now;
            Interlocked.Exchange(ref _archiveFrameUtcTicks, DateTime.UtcNow.Ticks);
            UpdateCameraSourceFpsEstimate();
            bool acceptedForPreview = _cameraFrameRateGate.ShouldAccept(Volatile.Read(ref _isRecording), _actualCameraFps);
            if (!acceptedForPreview && !Config.EnableEventRecordingBuffer)
                return;

            try
            {
                Mat frame = BitmapToMat(eventArgs.Frame);
                if (ShouldCaptureEventRecordingBufferFrame())
                    UpdatePreRecordBuffer(frame);
                if (acceptedForPreview)
                    HandleCameraFrame(frame);
                else
                    frame.Dispose();
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Camera", "NewFrame conversion failed", ex);
            }
        }

        private void NetworkCameraSource_FrameReady(object sender, NetworkCameraFrameEventArgs e)
        {
            _lastFrameTime = DateTime.Now;
            Interlocked.Exchange(ref _archiveFrameUtcTicks, DateTime.UtcNow.Ticks);
            UpdateCameraSourceFpsEstimate();
            bool acceptedForPreview = _cameraFrameRateGate.ShouldAccept(Volatile.Read(ref _isRecording), _actualCameraFps);
            if (!acceptedForPreview && !Config.EnableEventRecordingBuffer)
            {
                e.Frame.Dispose();
                return;
            }

            if (ShouldCaptureEventRecordingBufferFrame())
                UpdatePreRecordBuffer(e.Frame);
            if (acceptedForPreview)
                HandleCameraFrame(e.Frame);
            else
                e.Frame.Dispose();
        }

        private void UpdateCameraSourceFpsEstimate()
        {
            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Exchange(ref _cameraSourceLastTimestamp, now);
            if (previous <= 0) return;

            double interval = (now - previous) / (double)Stopwatch.Frequency;
            if (interval < 0.01 || interval > 1.0) return;

            double sampleFps = 1.0 / interval;
            double current = Volatile.Read(ref _cameraSourceFpsEstimate);
            double next = current <= 0 ? sampleFps : current * 0.8 + sampleFps * 0.2;
            Volatile.Write(ref _cameraSourceFpsEstimate, next);
            Interlocked.Increment(ref _cameraSourceSampleCount);
        }

        private bool ShouldCaptureEventRecordingBufferFrame()
        {
            if (!Config.EnableEventRecordingBuffer)
                return false;

            // 连续扫码始终维护预录；同码停录仅在未开始录制或已触发收尾时维护，
            // 收尾阶段从触发时刻立即积累，后续扫码可获得这段真实画面。
            return !Config.EnableSameBarcodeStopRecording
                || !Volatile.Read(ref _isRecording)
                || _sameCodePostRollCts is { IsCancellationRequested: false };
        }

        private int GetEffectiveRecordingFps()
        {
            double estimated = Volatile.Read(ref _cameraSourceFpsEstimate);
            int samples = Volatile.Read(ref _cameraSourceSampleCount);
            if (samples >= 5 && estimated >= 1 && estimated <= 120)
                return Math.Clamp((int)Math.Round(estimated), 1, 120);
            return _actualCameraFps > 0 ? _actualCameraFps : Config.Fps;
        }

        private void HandleCameraFrame(Mat frame)
        {
            try
            {
                CameraFrameOrientation.Apply(frame, Config.CameraRotate180);
                lock (_frameLock)
                {
                    _latestFrame?.Dispose();
                    _latestFrame = frame;
                    Interlocked.Increment(ref _latestFrameSequence);
                }
                _cameraFrameReady.Signal();
            }
            catch (Exception ex)
            {
                frame.Dispose();
                RuntimeLog.Error("Camera", "NewFrame processing failed", ex);
            }
        }

        private Mat BitmapToMat(Bitmap bitmap)
        {
            if (bitmap.PixelFormat == PixelFormat.Format24bppRgb)
            {
                var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
                var bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, bitmap.PixelFormat);
                try
                {
                    return Mat.FromPixelData(bitmap.Height, bitmap.Width, MatType.CV_8UC3, bmpData.Scan0, bmpData.Stride).Clone();
                }
                finally
                {
                    bitmap.UnlockBits(bmpData);
                }
            }

            using var solidBitmap = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb);
            using (Graphics gr = Graphics.FromImage(solidBitmap))
                gr.DrawImage(bitmap, new System.Drawing.Rectangle(0, 0, solidBitmap.Width, solidBitmap.Height));

            var solidRect = new System.Drawing.Rectangle(0, 0, solidBitmap.Width, solidBitmap.Height);
            var solidData = solidBitmap.LockBits(solidRect, ImageLockMode.ReadOnly, solidBitmap.PixelFormat);
            try
            {
                return Mat.FromPixelData(solidBitmap.Height, solidBitmap.Width, MatType.CV_8UC3, solidData.Scan0, solidData.Stride).Clone();
            }
            finally
            {
                solidBitmap.UnlockBits(solidData);
            }
        }

        private async Task VideoProcessLoop(CancellationToken token)
        {
            int frameTickCounter = 0;
            long lastProcessedFrameSequence = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    // 录制时跟随硬件实际帧率；空闲时只保留预览和条码识别所需的处理频率。
                    int processingFps = CameraFrameProcessingPolicy.GetProcessingFps(IsRecording, _actualCameraFps);
                    double frameDurationMs = 1000.0 / processingFps;
                    DateTime startTime = DateTime.Now; Mat currentFrame = null;
                    long currentFrameSequence;
                    lock (_frameLock)
                    {
                        currentFrameSequence = _latestFrameSequence;
                        if (_latestFrame != null && !_latestFrame.IsDisposed)
                            currentFrame = _latestFrame.Clone();
                    }

                    // _latestFrame 可能在摄像头下一帧到来前被循环多次读取。
                    // 录像只处理真正新到达的帧，避免把同一画面重复写入造成卡顿/闪烁。
                    if (currentFrame != null && currentFrameSequence == lastProcessedFrameSequence)
                    {
                        currentFrame.Dispose();
                        await Task.Delay(Math.Max(1, (int)Math.Round(frameDurationMs)), token);
                        continue;
                    }
                    if (currentFrame != null)
                        lastProcessedFrameSequence = currentFrameSequence;

                    // 检测摄像头是否已断开：_latestFrame 是旧帧不会自动清除，必须用 _lastFrameTime 判断
                    if (currentFrame != null && _cameraEverConnected && !_isCameraSleeping)
                    {
                        double sinceLastNewFrame = (DateTime.Now - _lastFrameTime).TotalSeconds;
                        if (sinceLastNewFrame > 1.5)
                        {
                            currentFrame.Dispose();
                            currentFrame = null;
                            lock (_frameLock) { _latestFrame?.Dispose(); _latestFrame = null; }
                        }
                    }

                    if (currentFrame != null && !currentFrame.Empty())
                    {
                        TrySubmitCameraPairingQrFrame(currentFrame);
                        TrySubmitCameraBarcodeFrame(currentFrame);
                        Mat processedFrame = currentFrame;
                        CameraFrameSize = new System.Windows.Size(currentFrame.Width, currentFrame.Height);

                        if (Config.EnableSmartZoom || PreviewZoomScale.HasValue)
                        {
                            double effectiveScale = PreviewZoomScale ?? Config.ZoomScale;
                            int zoomW = (int)(currentFrame.Width / effectiveScale);
                            int zoomH = (int)(currentFrame.Height / effectiveScale);
                            if (zoomW <= 0 || zoomW > currentFrame.Width) zoomW = currentFrame.Width;
                            if (zoomH <= 0 || zoomH > currentFrame.Height) zoomH = currentFrame.Height;

                            var currentZoomRect = new OpenCvSharp.Rect((currentFrame.Width - zoomW) / 2, (currentFrame.Height - zoomH) / 2, zoomW, zoomH)
                                .Intersect(new OpenCvSharp.Rect(0, 0, currentFrame.Width, currentFrame.Height));

                            if (currentZoomRect.Width > 0 && currentZoomRect.Height > 0 && _zoomPhase == ZoomPhase.None)
                            {
                                LastZoomRect = new System.Windows.Rect(currentZoomRect.X, currentZoomRect.Y, currentZoomRect.Width, currentZoomRect.Height);
                            }

                            if (_isScanning)
                            {
                                if (_delayBeforeZooming && (DateTime.Now - _lastScanTime).TotalMilliseconds >= Config.ZoomDelaySeconds * 1000.0)
                                {
                                    _delayBeforeZooming = false;
                                    _zoomPhase = ZoomPhase.ZoomingIn;
                                    _zoomPhaseStartTime = DateTime.Now;
                                    LastZoomRect = System.Windows.Rect.Empty;
                                    IsZoomingActive = true;
                                    Debug.WriteLine($"[Zoom] 缩放触发: Delay={Config.ZoomDelaySeconds}s, Scale={Config.ZoomScale}");
                                }

                                // 根据缩放阶段计算动画倍率
                                double animDuration = Config.EnableZoomAnimation ? Config.ZoomAnimationDurationMs : 0;
                                double animatedScale = 1.0;
                                bool applyZoom = false;

                                if (_zoomPhase == ZoomPhase.ZoomingIn)
                                {
                                    double elapsed = (DateTime.Now - _zoomPhaseStartTime).TotalMilliseconds;
                                    double t = animDuration > 0 ? Math.Min(elapsed / animDuration, 1.0) : 1.0;
                                    animatedScale = 1.0 + (effectiveScale - 1.0) * SmoothStep(t);
                                    applyZoom = true;
                                    if (t >= 1.0)
                                    {
                                        _zoomPhase = ZoomPhase.Holding;
                                        _zoomPhaseStartTime = DateTime.Now;
                                    }
                                }
                                else if (_zoomPhase == ZoomPhase.Holding)
                                {
                                    animatedScale = effectiveScale;
                                    applyZoom = true;
                                    if ((DateTime.Now - _zoomPhaseStartTime).TotalMilliseconds >= Config.ZoomDurationSeconds * 1000.0)
                                    {
                                        _zoomPhase = ZoomPhase.ZoomingOut;
                                        _zoomPhaseStartTime = DateTime.Now;
                                    }
                                }
                                else if (_zoomPhase == ZoomPhase.ZoomingOut)
                                {
                                    double elapsed = (DateTime.Now - _zoomPhaseStartTime).TotalMilliseconds;
                                    double t = animDuration > 0 ? Math.Min(elapsed / animDuration, 1.0) : 1.0;
                                    animatedScale = effectiveScale - (effectiveScale - 1.0) * SmoothStep(t);
                                    applyZoom = true;
                                    if (t >= 1.0)
                                    {
                                        _zoomPhase = ZoomPhase.None;
                                        _isScanning = false;
                                        IsZoomingActive = false;
                                        Debug.WriteLine("[Zoom] 缩放动画结束，恢复原样");
                                    }
                                }

                                if (applyZoom && animatedScale > 1.001)
                                {
                                    int animW = (int)(currentFrame.Width / animatedScale);
                                    int animH = (int)(currentFrame.Height / animatedScale);
                                    if (animW > 0 && animH > 0 && animW <= currentFrame.Width && animH <= currentFrame.Height)
                                    {
                                        var animRect = new OpenCvSharp.Rect(
                                            (currentFrame.Width - animW) / 2, (currentFrame.Height - animH) / 2, animW, animH)
                                            .Intersect(new OpenCvSharp.Rect(0, 0, currentFrame.Width, currentFrame.Height));
                                        if (animRect.Width > 0 && animRect.Height > 0)
                                        {
                                            var zoomed = currentFrame.Clone(animRect);
                                            processedFrame = new Mat();
                                            Cv2.Resize(zoomed, processedFrame, new OpenCvSharp.Size(Config.FrameWidth, Config.FrameHeight));
                                            zoomed.Dispose();
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            if (LastZoomRect != System.Windows.Rect.Empty) LastZoomRect = System.Windows.Rect.Empty;
                            if (_isScanning)
                            {
                                _isScanning = false;
                                Debug.WriteLine($"[Zoom] 扫码已触发但未执行缩放: EnableSmartZoom={Config.EnableSmartZoom}");
                            }
                        }

                        bool previewFrameDue = IsPreviewFrameDue();

                        // 非录制状态只为真正要发布的预览帧绘制水印，避免按摄像头满帧率克隆整帧。
                        if (Config.EnableWatermark && (IsRecording || previewFrameDue))
                        {
                            try
                            {
                                if (processedFrame == currentFrame)
                                {
                                    processedFrame = currentFrame.Clone();
                                }
                                string orderId = IsRecording ? _recordingOrderId : CurrentOrderId;
                                IReadOnlyList<string> extensionLines = Config.EnableThirdPartyWatermark && IsRecording
                                    && string.Equals(_recordingWatermarkSnapshot.RecordingSessionId, _recordingSessionId, StringComparison.Ordinal)
                                    ? _recordingWatermarkSnapshot.Lines
                                    : Array.Empty<string>();
                                ApplyWatermarkToFrame(processedFrame, DateTimeOffset.Now, orderId, extensionLines);
                            }
                            catch { }
                        }

                        if (IsRecording && frameTickCounter % 30 == 0) TryPerformMotionDetection(currentFrame);
                        if (previewFrameDue)
                            PublishPreviewFrameIfDue(processedFrame);

                        bool handedToRecorder;
                        lock (_recordingFrameOrderLock)
                            handedToRecorder = IsRecording && TryEnqueueFrameForRecording(processedFrame);
                        if (processedFrame != currentFrame)
                        {
                            if (!handedToRecorder) processedFrame.Dispose();
                            currentFrame.Dispose();
                        }
                        else if (!handedToRecorder)
                        {
                            currentFrame.Dispose();
                        }

                        CheckPreviewWatchdog();
                        LogResourceHealthIfDue("video-loop");
                    }
                    else
                    {
                        // 休眠期间不做任何自动重连操作
                        if (_isCameraSleeping || _isSetupWizardActive)
                        {
                        }
                        // 如果已达重连上限或正在重启中，不再尝试
                        else if (_isRestartingCamera || _consecutiveRestartFailures >= MaxConsecutiveRestartFailures)
                        {
                        }
                        // 冷却期间不尝试重连（退避机制）
                        else if ((DateTime.Now - _lastRestartAttempt).TotalSeconds < MinRestartIntervalSeconds * Math.Max(1, _consecutiveRestartFailures))
                        {
                        }
                        // 摄像头掉线检测：使用时间差（避免 200ms 循环间隔导致帧计数不准）
                        else if (IsVideoSourceRunning())
                        {
                            double noFrameSeconds = (DateTime.Now - _lastFrameTime).TotalSeconds;
                            if (IsNetworkCameraGracePeriod())
                            {
                                // 网络源等待首个关键帧期间不判信号丢失。
                            }
                            else if (noFrameSeconds > 1.5)
                            {
                                Debug.WriteLine($"[Camera] 信号丢失 {noFrameSeconds:F1}s，尝试重连 (失败次数={_consecutiveRestartFailures})");
                                _ = Application.Current.Dispatcher.InvokeAsync(() => {
                                    ShowToast("摄像头信号丢失，尝试重连...", ToastSeverity.Warning);
                                    SpeakWarning(DefaultSpeechCatalog.CameraReconnecting);
                                    _ = RestartCameraWithRecordingStopAsync("camera-frame-timeout");
                                });
                            }
                        }
                        else if (_cameraEverConnected)
                        {
                            // 摄像头曾连接过但现在不可用（断连/拔掉）：持续尝试重连
                            double missingSeconds = (DateTime.Now - _lastFrameTime).TotalSeconds;
                            if (missingSeconds > 2.0)
                            {
                                Debug.WriteLine($"[Camera] 摄像头断开，尝试重连 (失败次数={_consecutiveRestartFailures})");
                                _ = Application.Current.Dispatcher.InvokeAsync(() => {
                                    ShowToast("摄像头已断开，等待重新连接...", ToastSeverity.Warning);
                                    SpeakWarning(DefaultSpeechCatalog.CameraReconnecting);
                                    _ = RestartCameraWithRecordingStopAsync("camera-source-stopped");
                                });
                            }
                        }

                        // 摄像头休眠后无需高频轮询；用户活动仍会通过 NotifyUserActivity 立即 StartCamera。
                        int idleDelayMs = _isCameraSleeping ? 1000 : 200;
                        await Task.Delay(idleDelayMs, token);
                        frameTickCounter++;
                        continue;
                    }

                    if (IsRecording)
                    {
                        double elapsedSec = (DateTime.Now - _recordStartTime).TotalSeconds;
                        double activeElapsedSec = _recordingGracePeriodStartTime == DateTime.MinValue
                            ? elapsedSec
                            : (DateTime.Now - _recordingGracePeriodStartTime).TotalSeconds;
                        double motionIdleSec = (DateTime.Now - _lastMotionTime).TotalSeconds;
                        double warnSec = Config.TimeoutWarningSeconds;

                        // 录制前 5 秒为采集期，跳过超时与预警检测
                        bool inGracePeriod = activeElapsedSec < 5.0;
                        bool sameCodePostRollPending = _sameCodePostRollCts is { IsCancellationRequested: false };

                        double autoStopTotalSec = Config.AutoStopMinutes * 60.0;
                        double maxDurTotalSec = Config.MaxDurationMinutes * 60.0;

                        if (!inGracePeriod)
                        {
                            // 有活跃运动时重置预警标记（滞后重置，防止反复播报）
                            if (_autoStopWarned && motionIdleSec < warnSec)
                            {
                                _autoStopWarned = false;
                                Speak(DefaultSpeechCatalog.MotionDetected);
                            }

                            // 即将超时语音提示（确保预警阈值合理：超时总时长 + 5s）
                            if (!_autoStopWarned && Config.EnableAutoStop
                                && autoStopTotalSec > warnSec + 5
                                && motionIdleSec >= autoStopTotalSec - warnSec)
                            {
                                _autoStopWarned = true;
                                SpeakWarning(DefaultSpeechCatalog.MotionTimeoutWarning);
                            }
                            if (!_maxDurationWarned && Config.EnableMaxDuration
                                && maxDurTotalSec > warnSec * 2
                                && elapsedSec >= maxDurTotalSec - warnSec)
                            {
                                _maxDurationWarned = true;
                                SpeakWarning(DefaultSpeechCatalog.RecordingDurationWarning);
                            }
                        }

                        if (frameTickCounter % 15 == 0 && _currentScanRecord != null)
                        {
                            _ = Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                if (_currentScanRecord != null)
                                {
                                    int maxSec = (int)(Config.MaxDurationMinutes * 60);
                                    _currentScanRecord.Duration = Config.EnableMaxDuration ? $"{(int)elapsedSec}s / {maxSec}s" : $"{(int)elapsedSec}s";
                                }
                            });
                        }

                        if (!inGracePeriod && !sameCodePostRollPending && Config.EnableAutoStop && (DateTime.Now - _lastMotionTime).TotalSeconds >= Config.AutoStopMinutes * 60.0)
                        {
                            _stopReason = "静止超时";
                            _ = Application.Current.Dispatcher.InvokeAsync(async () => {
                                if (_isDisposed) return;
                                await SafeStopRecordingAsync();
                                ShowToast("画面静止超时，自动停录", ToastSeverity.Warning);
                                SpeakWarning(DefaultSpeechCatalog.MotionTimeoutStopped);
                                CurrentOrderId = "";
                                ScanInputText = "";
                            });
                        }

                        if (!inGracePeriod && !sameCodePostRollPending && Config.EnableMaxDuration && elapsedSec >= Config.MaxDurationMinutes * 60.0)
                        {
                            _stopReason = "时长超时";
                            _ = Application.Current.Dispatcher.InvokeAsync(async () => {
                                if (_isDisposed) return;
                                await SafeStopRecordingAsync();
                                ShowToast("已达最大录像限制时长", ToastSeverity.Information);
                                SpeakWarning(DefaultSpeechCatalog.RecordingDurationStopped);
                                CurrentOrderId = "";
                                ScanInputText = "";
                            });
                        }
                    }

                    frameTickCounter++;
                    int sleepMs = (int)Math.Max(0, frameDurationMs - (DateTime.Now - startTime).TotalMilliseconds);
                    if (sleepMs > 0) await Task.Delay(sleepMs, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                RuntimeLog.Error("VideoProcess", "VideoProcessLoop crashed, restarting", ex);
                if (!token.IsCancellationRequested && !_isDisposed)
                {
                    try { await Task.Delay(500, token); } catch (OperationCanceledException) { return; }
                    if (!token.IsCancellationRequested && !_isDisposed)
                    {
                        _videoTask = Task.Run(() => VideoProcessLoop(token), token);
                    }
                }
            }
        }

        private void CheckPreviewWatchdog()
        {
            if (_isDisposed || _isCameraSleeping || SuppressVideoPreviewUpdates) return;
            if (!IsVideoSourceRunning() || !_cameraEverConnected) return;
            if (_lastFrameTime == DateTime.MinValue || _lastPreviewPublishedAt == DateTime.MinValue) return;
            if (IsNetworkCameraGracePeriod()) return;

            DateTime now = DateTime.Now;
            TimeSpan sinceLastFrame = now - _lastFrameTime;
            TimeSpan sinceLastPreview = now - _lastPreviewPublishedAt;

            if (sinceLastFrame > PreviewFreezeWarnThreshold)
            {
                if (now - _lastPreviewFreezeLogAt > PreviewFreezeWarnThreshold)
                {
                    _lastPreviewFreezeLogAt = now;
                    RuntimeLog.Warn("Preview", $"No new camera frame for {sinceLastFrame.TotalSeconds:F1}s, preview age={sinceLastPreview.TotalSeconds:F1}s, recording={IsRecording}");
                    LogResourceHealthIfDue("preview-no-frame", force: true);
                }
                return;
            }

            if (sinceLastPreview < PreviewFreezeWarnThreshold) return;
            TimeSpan uiHeartbeatAge = now - _lastUiHeartbeatAt;
            if (_lastUiHeartbeatAt != DateTime.MinValue && uiHeartbeatAge > UiHeartbeatStaleThreshold)
            {
                if (now - _lastPreviewFreezeLogAt > PreviewFreezeWarnThreshold)
                {
                    _lastPreviewFreezeLogAt = now;
                    RuntimeLog.Warn("Preview", $"Preview publish delayed because UI dispatcher is busy for {uiHeartbeatAge.TotalSeconds:F1}s, frame age={sinceLastFrame.TotalSeconds:F1}s, preview age={sinceLastPreview.TotalSeconds:F1}s, recording={IsRecording}");
                    LogResourceHealthIfDue("preview-ui-busy", force: true);
                }
                return;
            }

            int queueCount = -1;
            try { queueCount = _videoWriteQueue?.Count ?? -1; } catch { }
            string writeTaskStatus = _writeTask == null ? "null" : _writeTask.Status.ToString();

            if (now - _lastPreviewFreezeLogAt > PreviewFreezeWarnThreshold)
            {
                _lastPreviewFreezeLogAt = now;
                RuntimeLog.Warn("Preview", $"Preview stale for {sinceLastPreview.TotalSeconds:F1}s while frames are fresh ({sinceLastFrame.TotalSeconds:F1}s), pending={(_previewSessionGate.IsPending ? 1 : 0)}, recording={IsRecording}, queue={queueCount}, writeTask={writeTaskStatus}");
                LogResourceHealthIfDue("preview-stale", force: true);
            }

            if (sinceLastPreview < PreviewFreezeRestartThreshold) return;
            if (_isRestartingCamera) return;
            if (now - _lastPreviewWatchdogRestartAt < PreviewFreezeRestartCooldown) return;

            _lastPreviewWatchdogRestartAt = now;
            if (CameraReconnectPolicy.GetPreviewFreezeRecovery(sinceLastFrame, PreviewFreezeWarnThreshold)
                == PreviewFreezeRecoveryAction.ResetPreviewPipeline)
            {
                RuntimeLog.Warn("Preview", $"Preview frozen for {sinceLastPreview.TotalSeconds:F1}s while camera frames remain fresh; resetting preview pipeline without camera restart. recording={IsRecording}, queue={queueCount}, writeTask={writeTaskStatus}");
                LogResourceHealthIfDue("preview-reset", force: true);
                _previewSessionGate.ClearCurrentPending();
                return;
            }

            RuntimeLog.Warn("Preview", $"Preview frozen for {sinceLastPreview.TotalSeconds:F1}s with stale camera frames, restarting camera. recording={IsRecording}, queue={queueCount}, writeTask={writeTaskStatus}");
            LogResourceHealthIfDue("preview-restart", force: true);
            _previewSessionGate.ClearCurrentPending();
            _ = Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_isDisposed || _isCameraSleeping || SuppressVideoPreviewUpdates) return;
                ShowToast("预览画面卡住，正在重连摄像头...", ToastSeverity.Warning);
                _ = RestartCameraWithRecordingStopAsync("preview-freeze-with-stale-camera-frame");
            });
        }

        private static double SmoothStep(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return t * t * (3 - 2 * t);
        }

        private bool IsPreviewFrameDue()
        {
            return !SuppressVideoPreviewUpdates
                && !_isDisposed
                && DateTime.UtcNow - _lastPreviewFrameAt >= PreviewFrameInterval
                && !_previewSessionGate.IsPending;
        }

        private void PublishPreviewFrameIfDue(Mat frame)
        {
            if (SuppressVideoPreviewUpdates || _isDisposed) return;

            DateTime now = DateTime.UtcNow;
            if (now - _lastPreviewFrameAt < PreviewFrameInterval) return;

            if (!_previewSessionGate.TryAcquire(out int previewSessionId)) return;
            _lastPreviewFrameAt = now;

            Mat previewFrame = null;
            try
            {
                previewFrame = frame.Clone();

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    previewFrame.Dispose();
                    ReleasePreviewUpdate(previewSessionId);
                    return;
                }

                Mat frameToPublish = previewFrame;
                previewFrame = null;
                _ = dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!_isDisposed
                            && !SuppressVideoPreviewUpdates
                            && _previewSessionGate.IsCurrent(previewSessionId))
                        {
                            if (_previewWriteableBitmap == null
                                || _previewWriteableBitmap.PixelWidth != frameToPublish.Width
                                || _previewWriteableBitmap.PixelHeight != frameToPublish.Height)
                            {
                                _previewWriteableBitmap = new WriteableBitmap(
                                    frameToPublish.Width,
                                    frameToPublish.Height,
                                    96,
                                    96,
                                    System.Windows.Media.PixelFormats.Bgr24,
                                    null);
                                VideoFrame = _previewWriteableBitmap;
                            }

                            int stride = checked((int)frameToPublish.Step());
                            int bufferSize = checked(stride * frameToPublish.Height);
                            _previewWriteableBitmap.WritePixels(
                                new Int32Rect(0, 0, frameToPublish.Width, frameToPublish.Height),
                                frameToPublish.Data,
                                bufferSize,
                                stride);
                            _lastPreviewPublishedAt = DateTime.Now;
                            Interlocked.Exchange(ref _archivePreviewUtcTicks, DateTime.UtcNow.Ticks);
                        }
                    }
                    finally
                    {
                        frameToPublish.Dispose();
                        ReleasePreviewUpdate(previewSessionId);
                    }
                }), System.Windows.Threading.DispatcherPriority.Render);
            }
            catch
            {
                previewFrame?.Dispose();
                ReleasePreviewUpdate(previewSessionId);
                if (DateTime.Now - _lastPreviewConvertErrorLogAt > TimeSpan.FromSeconds(30))
                {
                    _lastPreviewConvertErrorLogAt = DateTime.Now;
                    RuntimeLog.Warn("Preview", $"Preview bitmap conversion failed, {BuildResourceHealthSnapshot()}");
                }
            }
        }

        private void LogResourceHealthIfDue(string reason, bool force = false)
        {
            DateTime now = DateTime.Now;
            if (!force && now - _lastResourceHealthLogAt < ResourceHealthLogInterval)
                return;

            _lastResourceHealthLogAt = now;
            RuntimeLog.Info("Health", $"{reason}: {BuildResourceHealthSnapshot()}");
        }

        private string BuildResourceHealthSnapshot()
        {
            int videoQueueCount = -1;
            int audioQueueCount = -1;
            try { videoQueueCount = _videoWriteQueue?.Count ?? -1; } catch { }
            try { audioQueueCount = _audioWriteQueue?.Count ?? -1; } catch { }

            double frameAge = _lastFrameTime == DateTime.MinValue ? -1 : (DateTime.Now - _lastFrameTime).TotalSeconds;
            double previewAge = _lastPreviewPublishedAt == DateTime.MinValue ? -1 : (DateTime.Now - _lastPreviewPublishedAt).TotalSeconds;
            double uiAge = _lastUiHeartbeatAt == DateTime.MinValue ? -1 : (DateTime.Now - _lastUiHeartbeatAt).TotalSeconds;

            try
            {
                using var process = Process.GetCurrentProcess();
                long managedMb = GC.GetTotalMemory(false) / 1024 / 1024;
                long workingSetMb = process.WorkingSet64 / 1024 / 1024;
                long privateMb = process.PrivateMemorySize64 / 1024 / 1024;
                return $"ws={workingSetMb}MB, private={privateMb}MB, managed={managedMb}MB, handles={process.HandleCount}, threads={process.Threads.Count}, gc0={GC.CollectionCount(0)}, gc1={GC.CollectionCount(1)}, gc2={GC.CollectionCount(2)}, frameAge={frameAge:F1}s, previewAge={previewAge:F1}s, uiAge={uiAge:F1}s, pending={(_previewSessionGate.IsPending ? 1 : 0)}, recording={IsRecording}, videoQueue={videoQueueCount}, audioQueue={audioQueueCount}";
            }
            catch (Exception ex)
            {
                return $"health unavailable: {ex.Message}, frameAge={frameAge:F1}s, previewAge={previewAge:F1}s, uiAge={uiAge:F1}s, pending={(_previewSessionGate.IsPending ? 1 : 0)}, recording={IsRecording}, videoQueue={videoQueueCount}, audioQueue={audioQueueCount}";
            }
        }

        private bool IsVideoSourceRunning()
        {
            var networkSource = _networkCameraSource;
            if (networkSource != null)
            {
                try
                {
                    return networkSource.IsRunning;
                }
                catch
                {
                    return false;
                }
            }

            var source = _videoSource;
            if (source == null) return false;

            try
            {
                return source.IsRunning;
            }
            catch (Exception ex) when (ex is ThreadStateException || ex is InvalidOperationException || ex is ObjectDisposedException)
            {
                if (DateTime.Now - _lastCameraStateErrorLogAt > TimeSpan.FromSeconds(30))
                {
                    _lastCameraStateErrorLogAt = DateTime.Now;
                    RuntimeLog.Warn("Camera", $"Read camera running state failed: {ex.GetType().Name}: {ex.Message}");
                }
                return false;
            }
        }

        private bool IsCameraStreamReady()
        {
            var networkSource = _networkCameraSource;
            if (networkSource != null)
                return networkSource.ActualWidth > 0 && networkSource.ActualHeight > 0;
            return IsVideoSourceRunning();
        }

        private bool IsNetworkCameraGracePeriod()
        {
            return _networkCameraSource != null
                && (DateTime.Now - _networkCameraStartedAt).TotalSeconds < NetworkCameraConnectGraceSeconds;
        }

        private void TryPerformMotionDetection(Mat currentFrame)
        {
            try
            {
                if (currentFrame == null || currentFrame.IsDisposed || currentFrame.Empty()) return;
                PerformMotionDetection(currentFrame);
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex is OpenCvSharpException || ex is AccessViolationException)
            {
                if (DateTime.Now - _lastVideoFrameErrorLogAt > TimeSpan.FromSeconds(30))
                {
                    _lastVideoFrameErrorLogAt = DateTime.Now;
                    RuntimeLog.Warn("VideoProcess", $"Motion detection skipped one frame: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private void PerformMotionDetection(Mat currentFrame)
        {
            if (_previousCheckFrame.Empty()) { currentFrame.CopyTo(_previousCheckFrame); _lastMotionTime = DateTime.Now; return; }
            var motionSize = new OpenCvSharp.Size(320, 240);
            Cv2.Resize(currentFrame, _motionCurrentSmall, motionSize);
            Cv2.Resize(_previousCheckFrame, _motionPreviousSmall, motionSize);
            Cv2.CvtColor(_motionCurrentSmall, _motionCurrentGray, ColorConversionCodes.BGR2GRAY);
            Cv2.CvtColor(_motionPreviousSmall, _motionPreviousGray, ColorConversionCodes.BGR2GRAY);
            Cv2.Absdiff(_motionCurrentGray, _motionPreviousGray, _motionDiff);
            Cv2.Threshold(_motionDiff, _motionThreshold, Config.MotionDetectThreshold, 255, ThresholdTypes.Binary);
            double changeRatio = (double)Cv2.CountNonZero(_motionThreshold) / (_motionThreshold.Width * _motionThreshold.Height);
            if (changeRatio > 0.01) { _lastMotionTime = DateTime.Now; }
            currentFrame.CopyTo(_previousCheckFrame);
        }

        private bool TryEnqueueFrameForRecording(Mat frame)
        {
            try
            {
                if (frame == null || frame.IsDisposed) return false;
                if (_writeTask != null && _writeTask.IsCompleted) return false;

                var queue = _videoWriteQueue;
                bool added = queue != null && !queue.IsAddingCompleted && queue.TryAdd(frame, 5);
                if (!added && DateTime.Now - _lastRecordingQueueWarnAt > TimeSpan.FromSeconds(5))
                {
                    _lastRecordingQueueWarnAt = DateTime.Now;
                    RuntimeLog.Warn("Recording", $"Video frame enqueue failed, queueNull={queue == null}, addingCompleted={queue?.IsAddingCompleted}, queueCount={queue?.Count}, writeTask={_writeTask?.Status}");
                }
                return added;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Recording", "Video frame enqueue exception", ex);
                return false;
            }
        }

    }
}
