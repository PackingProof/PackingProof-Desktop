#nullable disable
using AForge.Video.DirectShow;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Services.MediaFoundation;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ExpressPackingMonitoring.ViewModels;

public partial class MainViewModel
{
    /// <summary>仅用于关闭阶段已经在后台运行的资源释放，界面路径必须调用 StopCameraAsync。</summary>
    private bool StopCamera() => StopCameraAsync().GetAwaiter().GetResult();

    private async Task<bool> StopCameraAsync()
    {
        LogResourceHealthIfDue("camera-stop", force: true);
        // 镜头控制在媒体源失效前回收入口；重连成功后 AttachCameraLens 会重新恢复。
        DetachCameraLens();
        if (!_isDisposed)
            ResetCameraBarcodeRecognition();

        NetworkCameraSource networkSource = _networkCameraSource;
        if (networkSource != null)
        {
            LogZeroFrameNetworkCameraSession(networkSource);
            networkSource.StreamInfoReady -= NetworkCameraSource_StreamInfoReady;
            networkSource.FrameReady -= NetworkCameraSource_FrameReady;
            networkSource.SourceError -= NetworkCameraSource_SourceError;
            try
            {
                await Task.Run(networkSource.Stop).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"Network camera stop failed: {ex.Message}");
            }
            if (ReferenceEquals(_networkCameraSource, networkSource))
                _networkCameraSource = null;
            lock (_frameLock) { _latestCameraFrame.Clear(); }
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
                await Task.Run(mfSource.Dispose).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"Media Foundation camera stop failed: {ex.Message}");
            }
            if (ReferenceEquals(_mfCameraSource, mfSource))
                _mfCameraSource = null;
            lock (_frameLock) { _latestCameraFrame.Clear(); }
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
                        await Task.Delay(100).ConfigureAwait(false);
                }
            }
            catch (SEHException) { /* AForge COM cleanup on some laptops */ }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"Graceful camera stop failed: {ex.Message}");
            }

            if (source.IsRunning)
            {
                RuntimeLog.Warn("Camera", "Graceful camera stop timed out, forcing stop");
                if (_cameraForceStopTask == null || _cameraForceStopTask.IsCompleted)
                    _cameraForceStopTask = Task.Run(() => source.Stop());
                Task forceStopTask = _cameraForceStopTask;
                try
                {
                    Task finished = await Task.WhenAny(
                        forceStopTask,
                        Task.Delay(2000)).ConfigureAwait(false);
                    if (ReferenceEquals(finished, forceStopTask))
                        await forceStopTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn(
                        "Camera",
                        $"Forced camera stop failed: {ex.GetBaseException().Message}");
                }
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
        lock (_frameLock) { _latestCameraFrame.Clear(); }
        ClearPreRecordBuffer();
        ClearPendingEventRecordingFrames();
        BeginPreviewSession(clearFrame: true);
        Volatile.Write(ref _archiveCameraActive, 0);
        RuntimeLog.Info("Camera", "StopCamera completed");
        return true;
    }

    private void QueueCameraRestart()
    {
        if (_isDisposed || _shutdownRequested || _isRestartingCamera)
            return;
        _ = RestartCameraAsync();
    }
}
