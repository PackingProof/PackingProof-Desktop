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
using ExpressPackingMonitoring.Services.MediaFoundation;
using ExpressPackingMonitoring.Services.Gpu;
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
                    _stopReason = string.Equals(trigger, "recording-frame-stalled", StringComparison.Ordinal)
                        ? "录像画面中断"
                        : "摄像头重连";
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

        /// <summary>
        /// 用户明确要求重连（设置里保存摄像头配置、手动重启、唤醒休眠）时清零重连计数。
        /// 启动失败计数也必须一起清零，否则换到别的摄像头后第一次报错就会直接判定"连续失败"。
        /// </summary>
        private void ResetCameraRestartCounters()
        {
            _consecutiveRestartFailures = 0;
            Interlocked.Exchange(ref _consecutiveStartupFailures, 0);
            _cameraAutoReconnectSuspended = false;
        }

        /// <summary>
        /// 摄像头"启动成功却立刻报错"：按退避重试，连续失败到上限就停止自动重连。
        ///
        /// 以前这条路径没有冷却，错误回调直接触发重启，而重启只看 IsRunning 就判定成功，
        /// 于是同一台坏设备每秒被重开上百次，把界面、日志和句柄一起拖死。
        /// </summary>
        private void ReportCameraStartupFailure(string detail)
        {
            int failures = Interlocked.Increment(ref _consecutiveStartupFailures);
            if (CameraStartupFailurePolicy.ShouldStopAutoReconnect(failures, MaxConsecutiveRestartFailures))
            {
                _cameraAutoReconnectSuspended = true;
                // 这台设备每次启动都立刻报错，重试不可能自己变好：停下来等用户换设备。
                // 只提示一次，不做"用户一动就重试"的兜底，否则变成每 20 秒重开一轮、
                // 每轮再提示一次的刷屏，和原来的死循环只差一个数量级。
                RuntimeLog.Error(
                    "Camera",
                    $"摄像头连续 {failures} 次启动失败，已停止自动重连。detail={detail}");
                ShowToast($"摄像头连续 {MaxConsecutiveRestartFailures} 次启动失败，已停止自动重连。请更换摄像头或重新插拔后在设置中重启", ToastSeverity.Error);
                SpeakWarning(DefaultSpeechCatalog.ReconnectCamera, 3);
                // 停掉这台只会报错的设备，避免日志和句柄继续增长
                if (!_isRestartingCamera)
                    StopCamera();
                return;
            }

            TimeSpan backoff = CameraStartupFailurePolicy.GetRestartBackoff(failures);
            RuntimeLog.Warn(
                "Camera",
                $"摄像头启动失败 {failures}/{MaxConsecutiveRestartFailures} 次，{backoff.TotalSeconds:F0}s 后重试。detail={detail}");
            ScheduleCameraStartupRetry(backoff);
        }

        private void ScheduleCameraStartupRetry(TimeSpan delay)
        {
            if (Interlocked.CompareExchange(ref _cameraStartupRetryPending, 1, 0) != 0)
                return; // 已经排了下一次退避重试，不再叠加

            _ = RunCameraStartupRetryAsync(delay);
        }

        private async Task RunCameraStartupRetryAsync(TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Interlocked.Exchange(ref _cameraStartupRetryPending, 0);
            if (_isSetupWizardActive || _isDisposed || _shutdownRequested) return;
            if (_cameraAutoReconnectSuspended) return;
            if (_isRestartingCamera) return;
            if (Volatile.Read(ref _consecutiveStartupFailures) == 0) return; // 期间已经恢复出帧

            await RestartCameraWithRecordingStopAsync("camera-startup-failure");
        }

        /// <summary>用户手动触发摄像头重置（在设置或 UI 按钮调用）</summary>
        public void ManualRestartCamera()
        {
            ResetCameraRestartCounters();
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
                ResetCameraRestartCounters();
                RuntimeLog.Info("Camera", "Wake requested by user activity");
                // 先启动再放开休眠标记：休眠期间设备本来就停着、帧时间也是旧的，
                // 提前放开会让看门狗在启动的这一秒多里排队重连，把刚起来的摄像头又关掉。
                StartCamera();
                IsCameraSleeping = false;
                ShowToast("摄像头已唤醒");
                Debug.WriteLine("[Idle] 用户活跃，摄像头唤醒");
            }
            else if (_consecutiveRestartFailures >= MaxConsecutiveRestartFailures && !_cameraAutoReconnectSuspended)
            {
                // 用户活动时如果摄像头已停止自动重连，重置并再试一次。
                // 启动失败导致的停止不在这里兜底：那台设备每次都会报同样的错，重试只会刷屏。
                ResetCameraRestartCounters();
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
            _latestPreviewFrame.Reset(sessionId);
            _lastPreviewPublishedAt = DateTime.Now;
            Interlocked.Exchange(ref _archivePreviewUtcTicks, DateTime.UtcNow.Ticks);
            _lastPreviewFreezeLogAt = DateTime.Now;

            if (clearFrame)
            {
                _cameraFrameReady.BeginSession();
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
            // 启动窗口内看门狗不得判定掉线：设备还没就绪，判定只会把刚起来的摄像头再关掉
            _isCameraStarting = true;
            try
            {
                if (_isSetupWizardActive || _isDisposed || _shutdownRequested)
                {
                    RuntimeLog.Info("Camera", "StartCamera skipped while setup wizard owns camera");
                    return;
                }

                if (_videoSource != null || _networkCameraSource != null || _mfCameraSource != null)
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

                string selectedMoniker = videoDevices[targetIndex].MonikerString;
                // 两个后端必须先恢复同一台摄像头的设置，MF 成功后会直接返回。
                if (Config.CameraConfigs.TryGetValue(selectedMoniker, out var settings))
                {
                    Config.FrameWidth = settings.FrameWidth;
                    Config.FrameHeight = settings.FrameHeight;
                    Config.Fps = settings.Fps;
                    Config.AudioDeviceName = settings.AudioDeviceName ?? "";
                    Config.AudioSyncOffsetMs = settings.AudioSyncOffsetMs;
                    Config.CameraRotate180 = settings.Rotate180;
                }

                // 先试新采集后端：它直接拿摄像头原生 YUY2/NV12 自己转 BGR，
                // 不经由 DirectShow 固定按 BT.601 的系统转换器（高清源发灰的根因），
                // 还能读出设备声明的色彩空间而不必按分辨率猜。
                // 任何一步不成立都回退下面的 AForge 路径 —— 绝不能因为后端问题录不了像。
                if (TryStartMediaFoundationCamera(selectedMoniker, previewSessionId))
                    return;

                _videoSource = new VideoCaptureDevice(selectedMoniker);
                RuntimeLog.Info("Camera", $"StartCamera selected index={targetIndex}, name={videoDevices[targetIndex].Name}");

                // 设置错误处理器（摄像头拔掉时 AForge 会触发此事件）
                _videoSource.VideoSourceError += (s, e) =>
                {
                    string description = e.Description;
                    Debug.WriteLine($"[Camera] 视频源错误: {description}");
                    if (_isSetupWizardActive || _isDisposed || _shutdownRequested)
                        return;
                    if (_cameraAutoReconnectSuspended)
                        return; // 已停止自动重连：不再重试，也不再刷日志

                    // 时间差在事件线程上取，界面被拖慢的情况下也要能认出"启动后立刻报错"
                    bool startupFailure = CameraStartupFailurePolicy.IsStartupFailure(DateTime.Now - _lastCameraStartAt);
                    RuntimeLog.Error("Camera", $"VideoSourceError: {description}");
                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_isSetupWizardActive || _isDisposed || _shutdownRequested)
                            return;
                        if (_cameraAutoReconnectSuspended)
                            return;

                        if (startupFailure)
                        {
                            // 同一轮启动的重复错误只统计一次，退避重试由 ReportCameraStartupFailure 统一安排
                            if (Interlocked.CompareExchange(ref _cameraStartupFailureRecorded, 1, 0) != 0)
                                return;
                            ReportCameraStartupFailure(description);
                            return;
                        }

                        // 运行中报错：冷却期内不重试，避免同一台设备被反复重开
                        if ((DateTime.Now - _lastRestartAttempt).TotalSeconds < MinRestartIntervalSeconds)
                            return;
                        ShowToast("摄像头连接发生错误，尝试重连...", ToastSeverity.Warning);
                        _ = RestartCameraWithRecordingStopAsync("video-source-error");
                    });
                };

                // 从摄像头能力中选择最匹配用户配置（分辨率+帧率）的模式
                if (_videoSource.VideoCapabilities.Length > 0)
                {
                    var caps = _videoSource.VideoCapabilities;
                    LogCameraCapabilities(caps);
                    VideoCapabilities best = caps[0];
                    int bestScore = int.MaxValue;
                    foreach (var cap in caps)
                    {
                        // 分辨率差值权重高，帧率差值权重低
                        int resDiff = Math.Abs(cap.FrameSize.Width - Config.FrameWidth) + Math.Abs(cap.FrameSize.Height - Config.FrameHeight);
                        int fpsDiff = Math.Abs(cap.AverageFrameRate - Config.Fps);
                        // 同分辨率同帧率时优先不抽色度的格式（24/32bpp）：YUY2/I420 这类 16/12bpp
                        // 色度被抽样过，预览会发灰、发软。只在完全打平时起作用，不会拿帧率换色度。
                        int chromaPenalty = cap.BitCount >= 24 ? 0 : 1;
                        int score = resDiff * 10 + fpsDiff + chromaPenalty;
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
                    RuntimeLog.Info(
                        "Camera",
                        $"Selected camera mode={best.FrameSize.Width}x{best.FrameSize.Height}@{best.AverageFrameRate}, bits={best.BitCount}");
                }
                else
                {
                    _actualCameraWidth = Config.FrameWidth;
                    _actualCameraHeight = Config.FrameHeight;
                    _actualCameraFps = Config.Fps > 0 ? Config.Fps : 15;
                }
                _videoSource.NewFrame += VideoSource_NewFrame;
                MarkCameraStarting();
                _videoSource.Start();
                MarkCameraReady();
                RuntimeLog.Info("Camera", $"StartCamera success {_actualCameraWidth}x{_actualCameraHeight}@{_actualCameraFps}, configured={Config.FrameWidth}x{Config.FrameHeight}@{Config.Fps}, running={_videoSource.IsRunning}, previewSession={previewSessionId}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Camera", "StartCamera failed", ex);
                ShowToast("摄像头启动失败", ToastSeverity.Error);
            }
            finally
            {
                _isCameraStarting = false;
            }
        }

        /// <summary>
        /// 尝试用 Media Foundation 后端启动摄像头。返回 false 表示这条路径不可用，
        /// 调用方继续走 AForge 路径。
        ///
        /// 为什么要先探测再启动：格式协商成功并不代表能出帧，虚拟摄像头在后端没有画面时
        /// 会协商成功、却一帧都不给（实测 Iriun 在手机端未连接时就是这样）。
        /// 直接启动的话用户看到的是永久黑屏，所以必须先确认真的收到过一帧。
        /// 代价是设备要开两次，换来的是"要么能用，要么干净回退"。
        /// </summary>
        private bool TryStartMediaFoundationCamera(string monikerString, int previewSessionId)
        {
            if (CameraBackendPolicy.IsMediaFoundationDisabled(Config.CameraBackend))
                return false;

            try
            {
                using MfPlatform platform = MfPlatform.TryStart();
                if (platform == null)
                    return false;

                MfCaptureDevice device = MfDeviceMatcher.FindByMoniker(
                    monikerString,
                    MfCaptureDevice.Enumerate());
                if (device == null)
                    return false;

                MfCaptureProbe.Result probe = MfCaptureProbe.Probe(
                    device.SymbolicLink,
                    Config.FrameWidth,
                    Config.FrameHeight,
                    Config.Fps,
                    Config.CameraColorMatrix);
                if (CameraBackendPolicy.Decide(Config.CameraBackend, probe.Usable)
                    != CameraBackendKind.MediaFoundation)
                {
                    RuntimeLog.Info(
                        "Camera",
                        $"Media Foundation 后端不可用（{probe.Failure}），使用 DirectShow 后端");
                    return false;
                }

                var source = new MfCameraSource(
                    device.SymbolicLink,
                    Config.FrameWidth,
                    Config.FrameHeight,
                    Config.Fps,
                    Config.CameraColorMatrix);
                source.FrameReady += MfCameraSource_FrameReady;
                source.SourceError += MfCameraSource_SourceError;
                MarkCameraStarting();
                if (!source.Start())
                {
                    source.FrameReady -= MfCameraSource_FrameReady;
                    source.SourceError -= MfCameraSource_SourceError;
                    source.Dispose();
                    RuntimeLog.Warn(
                        "Camera",
                        $"Media Foundation 探测通过但启动失败（{source.LastStartFailure}），改用 DirectShow 后端");
                    return false;
                }

                _mfCameraSource = source;
                _actualCameraWidth = source.ActualWidth;
                _actualCameraHeight = source.ActualHeight;
                _actualCameraFps = source.ActualFps > 0
                    ? (int)Math.Round(source.ActualFps)
                    : (Config.Fps > 0 ? Config.Fps : 15);
                MarkCameraReady();
                RuntimeLog.Info(
                    "Camera",
                    $"StartCamera success（Media Foundation）{_actualCameraWidth}x{_actualCameraHeight}"
                        + $"@{_actualCameraFps}，格式={source.ActualFormat}，bt709={source.UsesBt709}"
                        + $"，configured={Config.FrameWidth}x{Config.FrameHeight}@{Config.Fps}"
                        + $"，previewSession={previewSessionId}");
                return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"Media Foundation 后端启动异常，改用 DirectShow：{ex.Message}");
                return false;
            }
        }

        /// <summary>新后端的帧到达。与 AForge 路径共用同一套限流、预录与录像逻辑。</summary>
        private void MfCameraSource_FrameReady(object sender, MfFrameEventArgs e)
        {
            _lastFrameTime = DateTime.Now;
            MarkCameraStreamHealthy();
            Interlocked.Exchange(ref _archiveFrameUtcTicks, DateTime.UtcNow.Ticks);
            UpdateCameraSourceFpsEstimate();

            try
            {
                // 帧已经是 BGR24，不需要 BitmapToMat 那次格式转换与克隆，
                // 也不需要事后的色度校正（新后端在解码时就用了正确的矩阵）。
                Mat frame = e.Frame;
                if (ShouldCaptureEventRecordingBufferFrame())
                    UpdatePreRecordBuffer(frame);
                HandleCameraFrame(frame);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Camera", "Media Foundation frame processing failed", ex);
            }
        }

        private void MfCameraSource_SourceError(object sender, MfSourceErrorEventArgs e)
        {
            RuntimeLog.Warn("Camera", $"Media Foundation 采集错误：{e.Description}");
            if (!e.DeviceLost)
                return;

            // 掉线要走与旧路径一致的重连流程，否则录像会静默停止。
            _ = Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (_isDisposed || _isCameraSleeping) return;
                ShowToast("摄像头连接中断，正在重连...", ToastSeverity.Warning);
                _ = RestartCameraWithRecordingStopAsync("mf-device-lost");
            });
        }

        /// <summary>摄像头就绪后的公共状态设置，两条采集路径共用。</summary>
        private void MarkCameraReady()
        {
            _lastFrameTime = DateTime.Now; // 防止 VideoProcessLoop 启动时误判无帧
            _lastPreviewPublishedAt = DateTime.Now;
            long cameraReadyTicks = DateTime.UtcNow.Ticks;
            Interlocked.Exchange(ref _archiveFrameUtcTicks, cameraReadyTicks);
            Interlocked.Exchange(ref _archivePreviewUtcTicks, cameraReadyTicks);
            Volatile.Write(ref _archiveCameraActive, 1);
            _cameraEverConnected = true;
        }

        /// <summary>
        /// 设备真正开始启动前调用：记录启动时刻，并重新允许统计一次启动失败。
        /// 必须在 Start() 之前调用 —— 设备可能刚启动就报错，那时时间戳还没写就认不出这条路径。
        /// </summary>
        private void MarkCameraStarting()
        {
            _lastCameraStartAt = DateTime.Now;
            Volatile.Write(ref _cameraStartupFailureRecorded, 0);
        }

        /// <summary>
        /// 真的收到画面帧才说明摄像头可用：把启动失败计数清零，解除"已停止自动重连"。
        /// 只看 IsRunning 会把这台"能启动、不给帧"的设备当成连接成功，正是死循环的起点。
        /// </summary>
        private void MarkCameraStreamHealthy()
        {
            _cameraAutoReconnectSuspended = false;
            if (Interlocked.Exchange(ref _consecutiveStartupFailures, 0) != 0)
                RuntimeLog.Info("Camera", "摄像头恢复出帧，启动失败计数清零");
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

            MarkCameraStarting();
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
            LogResourceHealthIfDue("camera-stop", force: true);
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

            MfCameraSource mfSource = _mfCameraSource;
            if (mfSource != null)
            {
                RuntimeLog.Info("Camera", $"StopCamera（Media Foundation）running={mfSource.IsRunning}");
                try { mfSource.FrameReady -= MfCameraSource_FrameReady; } catch { }
                try { mfSource.SourceError -= MfCameraSource_SourceError; } catch { }
                try
                {
                    mfSource.Dispose();
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("Camera", $"Media Foundation camera stop failed: {ex.Message}");
                }
                if (ReferenceEquals(_mfCameraSource, mfSource))
                    _mfCameraSource = null;
                lock (_frameLock) { _latestFrame?.Dispose(); _latestFrame = null; }
                ClearPreRecordBuffer();
                ClearPendingEventRecordingFrames();
                BeginPreviewSession(clearFrame: true);
                Volatile.Write(ref _archiveCameraActive, 0);
                RuntimeLog.Info("Camera", "StopCamera（Media Foundation）completed");
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
            MarkCameraStreamHealthy();
            Interlocked.Exchange(ref _archiveFrameUtcTicks, DateTime.UtcNow.Ticks);
            UpdateCameraSourceFpsEstimate();

            try
            {
                Mat frame = BitmapToMat(eventArgs.Frame);
                if (ShouldCaptureEventRecordingBufferFrame())
                    UpdatePreRecordBuffer(frame);
                HandleCameraFrame(frame);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Camera", "NewFrame conversion failed", ex);
            }
        }

        private void NetworkCameraSource_FrameReady(object sender, NetworkCameraFrameEventArgs e)
        {
            _lastFrameTime = DateTime.Now;
            MarkCameraStreamHealthy();
            Interlocked.Exchange(ref _archiveFrameUtcTicks, DateTime.UtcNow.Ticks);
            UpdateCameraSourceFpsEstimate();

            if (ShouldCaptureEventRecordingBufferFrame())
                UpdatePreRecordBuffer(e.Frame);
            HandleCameraFrame(e.Frame);
        }

        /// <summary>
        /// 打印一次摄像头可选模式。现场反馈"预览发糊/发灰"时，先看这里：
        /// 1080p60 只有 16bpp（YUY2）这类抽色度格式时，DirectShow 采到的画面本身就发软，
        /// 得换成用 FFmpeg 读原始帧才能解决。
        /// </summary>
        private static void LogCameraCapabilities(VideoCapabilities[] capabilities)
        {
            try
            {
                string summary = string.Join(
                    ", ",
                    capabilities
                        .OrderByDescending(cap => cap.FrameSize.Width * cap.FrameSize.Height)
                        .ThenByDescending(cap => cap.AverageFrameRate)
                        .Take(16)
                        .Select(cap => $"{cap.FrameSize.Width}x{cap.FrameSize.Height}@{cap.AverageFrameRate}/{cap.BitCount}bpp"));
                RuntimeLog.Info("Camera", $"Camera modes({capabilities.Length}): {summary}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"读取摄像头模式失败：{ex.Message}");
            }
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
                    _latestFrameCapturedTicks = Stopwatch.GetTimestamp();
                    Interlocked.Increment(ref _latestFrameSequence);
                }
                _cameraFrameReady.Signal();
                _cameraFrameArrival.Signal();
            }
            catch (Exception ex)
            {
                frame.Dispose();
                RuntimeLog.Error("Camera", "NewFrame processing failed", ex);
            }
        }

        private Mat BitmapToMat(Bitmap bitmap)
        {
            Mat frame = CameraFrameConverter.ConvertToBgrMat(bitmap);
            // DirectShow 走的是系统 CSC 的 BT.601 解码，高清源按 BT.709 校正回来。
            // 只作用于直连摄像头这一条路径：NetworkCameraSource 的帧已经由 ffmpeg 解成 BGR24。
            CameraColorMatrixPolicy.ApplyIfNeeded(frame, Config.CameraColorMatrix);
            return frame;
        }

        private void MarkRecordingFramePipelineStage(RecordingFramePipelineStage stage, long frameSequence)
        {
            if (Volatile.Read(ref _isRecording))
                _recordingFramePipelineDiagnostics.Enter(stage, frameSequence);
        }

        private async Task VideoProcessLoop(CancellationToken token)
        {
            using var previewResizer = new GpuPreviewResizer();
            int frameTickCounter = 0;
            long lastProcessedFrameSequence = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    // 处理节奏跟随硬件实际帧率；有新帧时立即处理。
                    int processingFps = CameraFrameProcessingPolicy.GetProcessingFps(
                        IsRecording,
                        _actualCameraFps,
                        CurrentPreviewTargetFps());
                    double frameDurationMs = 1000.0 / processingFps;
                    DateTime startTime = DateTime.Now; Mat currentFrame = null;
                    long currentFrameSequence;
                    long currentFrameCapturedTicks;
                    bool waitingForNewFrame = false;
                    MarkRecordingFramePipelineStage(
                        RecordingFramePipelineStage.AcquireLatestFrame,
                        Volatile.Read(ref _latestFrameSequence));
                    lock (_frameLock)
                    {
                        currentFrameSequence = _latestFrameSequence;
                        currentFrameCapturedTicks = _latestFrameCapturedTicks;
                        if (_latestFrame != null && !_latestFrame.IsDisposed)
                        {
                            // 重复帧只等通知，避免先复制整帧再丢弃；过期帧仍进入断流检测。
                            waitingForNewFrame = currentFrameSequence == lastProcessedFrameSequence
                                && (DateTime.Now - _lastFrameTime).TotalSeconds <= 1.5;
                            if (!waitingForNewFrame)
                                currentFrame = _latestFrame.Clone();
                        }
                    }

                    // _latestFrame 可能在摄像头下一帧到来前被循环多次读取。
                    // 录像只处理真正新到达的帧，避免把同一画面重复写入造成卡顿/闪烁。
                    if (waitingForNewFrame)
                    {
                        MarkRecordingFramePipelineStage(
                            RecordingFramePipelineStage.WaitingForNextFrame,
                            currentFrameSequence);
                        // 等"下一帧到达"通知，而不是睡满一个帧间隔：轮询节拍与摄像头一旦错开，
                        // 睡满一格就会整整丢掉一拍，60fps 的源只能喂到 47fps，编码器按固定帧率
                        // 生成时间戳，文件因此比真实时间快 20% 以上（音画不同步）。
                        // 超时仍保留，摄像头无新帧时循环照常转动以走掉线检测。
                        await _cameraFrameArrival.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1, frameDurationMs)));
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
                        MarkRecordingFramePipelineStage(RecordingFramePipelineStage.PairingQr, currentFrameSequence);
                        TrySubmitCameraPairingQrFrame(currentFrame);
                        MarkRecordingFramePipelineStage(RecordingFramePipelineStage.BarcodeRecognition, currentFrameSequence);
                        TrySubmitCameraBarcodeFrame(currentFrame);
                        Mat processedFrame = currentFrame;
                        MarkRecordingFramePipelineStage(RecordingFramePipelineStage.FrameMetadata, currentFrameSequence);
                        CameraFrameSize = new System.Windows.Size(currentFrame.Width, currentFrame.Height);

                        if (CanApplySmartZoom || PreviewZoomScale.HasValue)
                        {
                            MarkRecordingFramePipelineStage(RecordingFramePipelineStage.SmartZoom, currentFrameSequence);
                            double effectiveScale = PreviewZoomScale ?? Config.MaxZoomScale;
                            CameraBarcodeGeometry barcodeGeometry = _lastBarcodeGeometry;
                            double boundedScale = SmartZoomPolicy.GetBoundedScale(
                                currentFrame.Width,
                                currentFrame.Height,
                                effectiveScale,
                                barcodeGeometry);
                            if (_zoomPhase == ZoomPhase.ZoomingIn && barcodeGeometry != null)
                            {
                                RuntimeLog.Info(
                                    "SmartZoom",
                                    $"Applying barcode-centered zoom scale={boundedScale:F2}, requested={effectiveScale:F2}, center=({barcodeGeometry.CenterX:F1},{barcodeGeometry.CenterY:F1})");
                            }
                            var currentZoomRect = SmartZoomPolicy.CreateCropRect(
                                    currentFrame.Width,
                                    currentFrame.Height,
                                    effectiveScale,
                                    barcodeGeometry)
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
                                    Debug.WriteLine($"[Zoom] 缩放触发: Delay={Config.ZoomDelaySeconds}s, MaxScale={Config.MaxZoomScale}");
                                }

                                // 根据缩放阶段计算动画倍率
                                double animDuration = Config.EnableZoomAnimation ? Config.ZoomAnimationDurationMs : 0;
                                double animatedScale = 1.0;
                                bool applyZoom = false;

                                if (_zoomPhase == ZoomPhase.ZoomingIn)
                                {
                                    double elapsed = (DateTime.Now - _zoomPhaseStartTime).TotalMilliseconds;
                                    double t = animDuration > 0 ? Math.Min(elapsed / animDuration, 1.0) : 1.0;
                                    animatedScale = 1.0 + (boundedScale - 1.0) * SmoothStep(t);
                                    applyZoom = true;
                                    if (t >= 1.0)
                                    {
                                        _zoomPhase = ZoomPhase.Holding;
                                        _zoomPhaseStartTime = DateTime.Now;
                                    }
                                }
                                else if (_zoomPhase == ZoomPhase.Holding)
                                {
                                    animatedScale = boundedScale;
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
                                    animatedScale = boundedScale - (boundedScale - 1.0) * SmoothStep(t);
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
                                        var animRect = SmartZoomPolicy.CreateCropRect(
                                                currentFrame.Width,
                                                currentFrame.Height,
                                                animatedScale,
                                                barcodeGeometry)
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
                            if (_zoomPhase != ZoomPhase.None)
                            {
                                // 解锁识别框调整取景范围时中途停掉放大，预览回到整帧后拖动才准
                                _zoomPhase = ZoomPhase.None;
                                IsZoomingActive = false;
                            }
                            if (_isScanning)
                            {
                                _isScanning = false;
                                Debug.WriteLine($"[Zoom] 扫码已触发但未执行缩放: EnableSmartZoom={Config.EnableSmartZoom}, GuideLocked={IsCameraBarcodeGuideLocked}");
                            }
                        }

                        bool previewPublishDue = ShouldPublishPreviewFrameNow();

                        // 非录制状态只为真正要发布的预览帧绘制水印，避免按摄像头满帧率克隆整帧。
                        if (Config.EnableWatermark && (IsRecording || previewPublishDue))
                        {
                            MarkRecordingFramePipelineStage(RecordingFramePipelineStage.Watermark, currentFrameSequence);
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

                        if (IsRecording && frameTickCounter % 30 == 0)
                        {
                            MarkRecordingFramePipelineStage(RecordingFramePipelineStage.MotionDetection, currentFrameSequence);
                            TryPerformMotionDetection(currentFrame);
                        }
                        if (previewPublishDue)
                        {
                            MarkRecordingFramePipelineStage(RecordingFramePipelineStage.PreviewPublish, currentFrameSequence);
                            PublishPreviewFrameIfDue(processedFrame, previewResizer, currentFrameCapturedTicks);
                        }

                        bool handedToRecorder;
                        MarkRecordingFramePipelineStage(RecordingFramePipelineStage.RecorderEnqueue, currentFrameSequence);
                        lock (_recordingFrameOrderLock)
                        {
                            handedToRecorder = IsRecording && TryEnqueueFrameForRecording(processedFrame, currentFrameCapturedTicks);
                        }
                        MarkRecordingFramePipelineStage(RecordingFramePipelineStage.FrameCleanup, currentFrameSequence);
                        if (processedFrame != currentFrame)
                        {
                            if (!handedToRecorder) processedFrame.Dispose();
                            currentFrame.Dispose();
                        }
                        else if (!handedToRecorder)
                        {
                            currentFrame.Dispose();
                        }

                        if (IsRecording)
                            Volatile.Write(ref _lastRecordingFrameProcessedTimestamp, Stopwatch.GetTimestamp());
                        MarkRecordingFramePipelineStage(RecordingFramePipelineStage.HealthCheck, currentFrameSequence);
                        CheckPreviewWatchdog();
                        LogResourceHealthIfDue("video-loop");
                        MarkRecordingFramePipelineStage(
                            RecordingFramePipelineStage.WaitingForNextFrame,
                            currentFrameSequence);
                    }
                    else
                    {
                        MarkRecordingFramePipelineStage(
                            RecordingFramePipelineStage.NoFrame,
                            Volatile.Read(ref _latestFrameSequence));
                        CameraWatchdogState watchdogState = new(
                            _isCameraSleeping,
                            _isSetupWizardActive,
                            _isCameraStarting,
                            _isRestartingCamera,
                            _cameraAutoReconnectSuspended,
                            Volatile.Read(ref _cameraStartupRetryPending) != 0,
                            _consecutiveRestartFailures,
                            MaxConsecutiveRestartFailures,
                            DateTime.Now - _lastRestartAttempt,
                            MinRestartIntervalSeconds);
                        if (CameraWatchdogPolicy.CanJudgeCameraLost(watchdogState))
                        {
                            // 摄像头掉线检测：使用时间差（避免 200ms 循环间隔导致帧计数不准）
                            if (IsVideoSourceRunning())
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
                    // 满帧时下一轮按帧序号等待采集通知，避免额外定时睡眠引入唤醒延迟。
                    // 无帧状态仍延迟，防止断流或休眠时空转。
                    if (sleepMs > 0 && currentFrame == null)
                        await Task.Delay(sleepMs, token);
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
            if (!PreviewPublishPolicy.ShouldPublish(
                    SuppressVideoPreviewUpdates,
                    Config.DisableLivePreview,
                    _isDisposed,
                    _isCameraSleeping,
                    HasVisiblePreviewConsumer))
            {
                return;
            }
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

        /// <summary>
        /// 预览帧率：有人在看且有操作时跟采集帧率；空闲 1 分钟降到 15fps、5 分钟降到 4fps；
        /// 没有可见预览消费方时降到 2fps 保活。见 PreviewFrameRatePolicy。
        /// </summary>
        private int CurrentPreviewTargetFps() =>
            PreviewFrameRatePolicy.ResolveTargetFps(
                _actualCameraFps,
                DateTime.Now - _lastActivityTime);

        private bool IsPreviewFrameDue() => PreviewPublishPolicy.ShouldPublish(
            SuppressVideoPreviewUpdates,
            Config.DisableLivePreview,
            _isDisposed,
            _isCameraSleeping,
            HasVisiblePreviewConsumer);

        /// <summary>
        /// 这一轮要不要发布预览：先过停止条件，再按空闲分档限流。
        ///
        /// 分档必须在这里真正限流：处理循环是"摄像头来一帧就处理一帧"，循环节奏只当等待超时用，
        /// 不限制发布频率；没有这道门限时空闲降档对预览完全不起作用（现场反馈"降帧没生效"）。
        /// 满帧档放行每一帧（避免 60fps 抖动被误丢），降档后按 15/4fps 放行。
        /// 只影响预览发布，录像、条码识别、运动检测照常。
        /// </summary>
        private bool ShouldPublishPreviewFrameNow()
        {
            if (!IsPreviewFrameDue())
            {
                LogPreviewPausedIfDue();
                return false;
            }

            int previewTargetFps = CurrentPreviewTargetFps();
            bool acceptEveryFrame = previewTargetFps >= PreviewFrameRatePolicy.ResolveTargetFps(_actualCameraFps);
            return _previewPublishRateGate.ShouldAccept(acceptEveryFrame, previewTargetFps);
        }

        private void PublishPreviewFrameIfDue(Mat frame, GpuPreviewResizer previewResizer, long capturedTicks)
        {
            if (!PreviewPublishPolicy.ShouldPublish(
                    SuppressVideoPreviewUpdates,
                    Config.DisableLivePreview,
                    _isDisposed,
                    _isCameraSleeping,
                    HasVisiblePreviewConsumer))
            {
                LogPreviewPausedIfDue();
                return;
            }

            int previewSessionId = _previewSessionGate.CurrentSessionId;

            Mat previewFrame = null;
            try
            {
                // 按控件实际显示尺寸发布：整帧克隆 + 写位图 + 传 GPU 缩放都跟像素数成正比，
                // 1080p 一帧 6MB，缩到显示尺寸后满帧跑也不心疼。
                (int Width, int Height)? target = PreviewDownscalePolicy.ResolveTarget(
                    frame.Width,
                    frame.Height,
                    Volatile.Read(ref _previewDisplayWidth));
                if (target.HasValue)
                {
                    previewFrame = new Mat();
                    // 自动模式下虚拟/网络摄像头也可加速 BGR 预览；强制旧后端保留纯 CPU 排障出口。
                    if (CameraBackendPolicy.IsMediaFoundationDisabled(Config.CameraBackend)
                        || !previewResizer.TryResize(frame, previewFrame, target.Value.Width, target.Value.Height))
                    {
                        Cv2.Resize(
                            frame,
                            previewFrame,
                            new OpenCvSharp.Size(target.Value.Width, target.Value.Height),
                            interpolation: GpuPreviewResizer.ResolveInterpolation(
                                frame.Width, frame.Height, target.Value.Width, target.Value.Height));
                    }
                }
                else
                {
                    previewFrame = frame.Clone();
                }

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    previewFrame.Dispose();
                    return;
                }

                _latestPreviewFrame.Publish(previewSessionId, previewFrame, capturedTicks);
                previewFrame = null;
                ScheduleLatestPreviewFrame(previewSessionId, dispatcher);
            }
            catch
            {
                previewFrame?.Dispose();
                if (DateTime.Now - _lastPreviewConvertErrorLogAt > TimeSpan.FromSeconds(30))
                {
                    _lastPreviewConvertErrorLogAt = DateTime.Now;
                    RuntimeLog.Warn("Preview", $"Preview bitmap conversion failed, {BuildResourceHealthSnapshot()}");
                }
            }
        }

        /// <summary>
        /// 预览因为"没有可见消费方"或用户关闭而暂停时，按 30 秒节流记一条状态，
        /// 现场再看"画面为什么不动/为什么灰"时一眼能看出是哪个条件生效。
        /// </summary>
        private void LogPreviewPausedIfDue()
        {
            DateTime now = DateTime.Now;
            if (now - _lastPreviewPauseLogAt < TimeSpan.FromSeconds(30))
                return;

            _lastPreviewPauseLogAt = now;
            RuntimeLog.Info(
                "Preview",
                $"预览已暂停：mainVisible={_isMainPreviewVisible}, floatingActive={IsFloatingPreviewActive}, "
                    + $"displayWidth={Volatile.Read(ref _previewDisplayWidth)}, userDisabled={Config.DisableLivePreview}, "
                    + $"suppressed={SuppressVideoPreviewUpdates}, sleeping={_isCameraSleeping}, recording={IsRecording}");
        }

        private void ScheduleLatestPreviewFrame(int previewSessionId, System.Windows.Threading.Dispatcher dispatcher)
        {
            if (!_previewSessionGate.IsCurrent(previewSessionId)
                || !_previewSessionGate.TryAcquire(out previewSessionId)) return;
            try
            {
                _ = dispatcher.BeginInvoke(new Action(() =>
                {
                    var frameToPublish = _latestPreviewFrame.Take(previewSessionId, out long frameCapturedTicks);
                    try
                    {
                        if (frameToPublish != null && !_isDisposed
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
                            long writeStarted = Stopwatch.GetTimestamp();
                            // WritePixels 会等待渲染线程解锁，繁忙时能卡住 UI 超过一帧。
                            // 预览允许跳过来不及显示的帧，不等待旧帧占用的缓冲。
                            if (!_previewWriteableBitmap.TryLock(new Duration(TimeSpan.Zero)))
                                return;
                            try
                            {
                                unsafe
                                {
                                    int rowBytes = checked(frameToPublish.Width * 3);
                                    int destinationStride = _previewWriteableBitmap.BackBufferStride;
                                    byte* source = (byte*)frameToPublish.Data;
                                    byte* destination = (byte*)_previewWriteableBitmap.BackBuffer;
                                    for (int row = 0; row < frameToPublish.Height; row++)
                                        Buffer.MemoryCopy(source + row * stride,
                                            destination + row * destinationStride, destinationStride, rowBytes);
                                }
                                _previewWriteableBitmap.AddDirtyRect(
                                    new Int32Rect(0, 0, frameToPublish.Width, frameToPublish.Height));
                            }
                            finally
                            {
                                _previewWriteableBitmap.Unlock();
                            }
                            Interlocked.Add(ref _previewWriteTicksTotal, Stopwatch.GetTimestamp() - writeStarted);
                            Interlocked.Increment(ref _previewWriteCount);
                            Interlocked.Increment(ref _previewPublishedTotal);
                            if (frameCapturedTicks > 0)
                            {
                                long ageTicks = Math.Max(0, Stopwatch.GetTimestamp() - frameCapturedTicks);
                                Interlocked.Add(ref _previewFrameAgeTicks, ageTicks);
                                Interlocked.Increment(ref _previewFrameAgeCount);
                                // 只有 UI 线程写入最大值，后台诊断使用原子读取。
                                long maximum = Volatile.Read(ref _previewFrameAgeMaxTicks);
                                if (ageTicks > maximum) Interlocked.Exchange(ref _previewFrameAgeMaxTicks, ageTicks);
                            }
                            Interlocked.Exchange(ref _publishedPreviewWidth, frameToPublish.Width);
                            Interlocked.Exchange(ref _publishedPreviewHeight, frameToPublish.Height);
                            _lastPreviewPublishedAt = DateTime.Now;
                            Interlocked.Exchange(ref _archivePreviewUtcTicks, DateTime.UtcNow.Ticks);
                        }
                    }
                    finally
                    {
                        frameToPublish?.Dispose();
                        ReleasePreviewUpdate(previewSessionId);
                        // 处理本帧期间若已有新帧到达，主动续约，避免最后一帧留在槽中。
                        if (_latestPreviewFrame.HasFrame(previewSessionId))
                            ScheduleLatestPreviewFrame(previewSessionId, dispatcher);
                    }
                }), System.Windows.Threading.DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                ReleasePreviewUpdate(previewSessionId);
                _latestPreviewFrame.Take(previewSessionId, out _)?.Dispose();
                RuntimeLog.Warn("Preview", $"Preview dispatch failed: {ex.Message}");
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
            DateTime now = DateTime.Now;
            int videoQueueCount = -1;
            int audioQueueCount = -1;
            try { videoQueueCount = _videoWriteQueue?.Count ?? -1; } catch { }
            try { audioQueueCount = _audioWriteQueue?.Count ?? -1; } catch { }

            double frameAge = _lastFrameTime == DateTime.MinValue ? -1 : (DateTime.Now - _lastFrameTime).TotalSeconds;
            double previewAge = _lastPreviewPublishedAt == DateTime.MinValue ? -1 : (DateTime.Now - _lastPreviewPublishedAt).TotalSeconds;
            double uiAge = _lastUiHeartbeatAt == DateTime.MinValue ? -1 : (DateTime.Now - _lastUiHeartbeatAt).TotalSeconds;
            string previewStats = BuildPreviewStatsSnapshot(now);

            try
            {
                using var process = Process.GetCurrentProcess();
                long managedMb = GC.GetTotalMemory(false) / 1024 / 1024;
                long workingSetMb = process.WorkingSet64 / 1024 / 1024;
                long privateMb = process.PrivateMemorySize64 / 1024 / 1024;
                return $"ws={workingSetMb}MB, private={privateMb}MB, managed={managedMb}MB, handles={process.HandleCount}, threads={process.Threads.Count}, gc0={GC.CollectionCount(0)}, gc1={GC.CollectionCount(1)}, gc2={GC.CollectionCount(2)}, frameAge={frameAge:F1}s, previewAge={previewAge:F1}s, uiAge={uiAge:F1}s, {previewStats}, pending={(_previewSessionGate.IsPending ? 1 : 0)}, recording={IsRecording}, videoQueue={videoQueueCount}, audioQueue={audioQueueCount}";
            }
            catch (Exception ex)
            {
                return $"health unavailable: {ex.Message}, frameAge={frameAge:F1}s, previewAge={previewAge:F1}s, uiAge={uiAge:F1}s, {previewStats}, pending={(_previewSessionGate.IsPending ? 1 : 0)}, recording={IsRecording}, videoQueue={videoQueueCount}, audioQueue={audioQueueCount}";
            }
        }

        /// <summary>
        /// 预览发布统计：采样窗口内的实际发布帧率、单帧写位图平均耗时、当前生效的发布间隔。
        /// 用于检查采集和预览发布是否存在吞吐差距。
        /// </summary>
        private string BuildPreviewStatsSnapshot(DateTime now)
        {
            long published = Interlocked.Read(ref _previewPublishedTotal);
            long writes = Interlocked.Read(ref _previewWriteCount);
            double writeMilliseconds = writes > 0
                ? Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _previewWriteTicksTotal)).TotalMilliseconds / writes
                : -1;

            double publishedFps = -1;
            if (_previewStatsWindowStart != DateTime.MinValue)
            {
                double seconds = (now - _previewStatsWindowStart).TotalSeconds;
                long delta = published - _previewStatsWindowPublished;
                if (seconds >= 1 && delta >= 0)
                    publishedFps = delta / seconds;
            }

            _previewStatsWindowStart = now;
            _previewStatsWindowPublished = published;
            double idleSeconds = _lastActivityTime == DateTime.MinValue ? -1 : (now - _lastActivityTime).TotalSeconds;
            int cameraFps = (int)Math.Round(Volatile.Read(ref _cameraSourceFpsEstimate));
            int publishedWidth = Volatile.Read(ref _publishedPreviewWidth);
            int publishedHeight = Volatile.Read(ref _publishedPreviewHeight);
            long ageCount = Interlocked.Read(ref _previewFrameAgeCount);
            double ageMs = ageCount > 0
                ? Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _previewFrameAgeTicks)).TotalMilliseconds / ageCount : -1;
            double maxAgeMs = Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _previewFrameAgeMaxTicks)).TotalMilliseconds;
            return $"previewFps={publishedFps:F1}, previewWriteMs={writeMilliseconds:F1}, previewFrameAgeMs={ageMs:F1}, previewMaxFrameAgeMs={maxAgeMs:F1}, previewIntervalMs=full, previewTargetFps={CurrentPreviewTargetFps()}, cameraFps={cameraFps}, frame={_actualCameraWidth}x{_actualCameraHeight}, publishSize={publishedWidth}x{publishedHeight}, displayWidth={Volatile.Read(ref _previewDisplayWidth)}, focused={(_isAppWindowFocused ? 1 : 0)}, idle={idleSeconds:F0}s";
        }

        /// <summary>
        /// 跟着程序自己的窗口激活状态调整预览节奏：前台满帧，后台才逐级降帧。
        /// Application.Activated/Deactivated 覆盖主界面、设置、回放等所有窗口，
        /// 而且都在 UI 线程触发，这里只写一个 volatile 标记，供采集线程安全读取。
        /// </summary>
        private void HookApplicationFocusTracking()
        {
            Application application = Application.Current;
            if (application == null)
                return;

            _isAppWindowFocused = true;
            application.Activated += OnApplicationActivated;
            application.Deactivated += OnApplicationDeactivated;
        }

        private void UnhookApplicationFocusTracking()
        {
            Application application = Application.Current;
            if (application == null)
                return;

            application.Activated -= OnApplicationActivated;
            application.Deactivated -= OnApplicationDeactivated;
        }

        private void OnApplicationActivated(object sender, EventArgs e) => _isAppWindowFocused = true;

        private void OnApplicationDeactivated(object sender, EventArgs e) => _isAppWindowFocused = false;

        private bool IsVideoSourceRunning()
        {
            // 三条采集路径按存在性依次判断，同一时刻只有一个非空。
            // 漏掉任何一条都会让看门狗误判成"摄像头没在跑"并不断重连。
            var mfSource = _mfCameraSource;
            if (mfSource != null)
            {
                try
                {
                    return mfSource.IsRunning;
                }
                catch
                {
                    return false;
                }
            }

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
            var mfSource = _mfCameraSource;
            if (mfSource != null)
                return mfSource.ActualWidth > 0 && mfSource.ActualHeight > 0 && mfSource.IsRunning;

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

        private bool TryEnqueueFrameForRecording(Mat frame, long capturedTicks = 0)
        {
            try
            {
                if (frame == null || frame.IsDisposed) return false;
                if (_writeTask != null && _writeTask.IsCompleted) return false;

                var queue = _videoWriteQueue;
                bool added = queue != null && !queue.IsAddingCompleted
                    && queue.TryAdd(new RecordingVideoFrame(frame, capturedTicks), 5);
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
