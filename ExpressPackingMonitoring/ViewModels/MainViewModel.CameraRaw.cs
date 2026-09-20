using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 采集帧的"要不要 BGR / 要不要原始采样"这一层门控。
    ///
    /// 预录环只存不处理，不需要 BGR；只有它一个消费者时，采集端直接给原始采样（NV12/YUY2），
    /// 省掉 NV12→BGR 转换与发布克隆。预览要发布、正在录像、取景框识别或配对二维码在跑时，
    /// 一律照旧要 BGR —— 宁可多转一次，也不能让这些路径少拿到帧。
    /// </summary>
    public partial class MainViewModel
    {
        /// <summary>采集端每帧问一次：这一帧需不需要 BGR。</summary>
        private bool CameraFrameNeedsBgr()
        {
            if (!Config.EnableEventRecordingBuffer)
                return true;
            if (IsRecording)
                return true;
            if (Config.EnableCameraBarcodeRecognition)
                return true;
            if (_cameraPairingQrScan != null)
                return true;

            return IsPreviewFrameDue();
        }

        /// <summary>最近是否真的来过摄像头帧（按断流判定同一个时间窗，避免拿残帧开录）。</summary>
        private bool HasRecentCameraFrame()
        {
            if (_isDisposed || !_cameraEverConnected)
                return false;
            // 用"累计交付帧数"而不是处理循环的帧序号：只要原始采样、不进循环的那些帧也要算数。
            if (Volatile.Read(ref _cameraFramesDelivered) <= 0)
                return false;

            DateTime lastFrameTime = _lastFrameTime;
            return lastFrameTime != DateTime.MinValue
                && DateTime.Now - lastFrameTime <= CameraFrameStaleThreshold;
        }

        /// <summary>
        /// 等摄像头出画面。摄像头已经在持续采集时立即返回，避免扫码启动被一次性的就绪信号误判为超时。
        /// 这里只按"最近还在来帧"判断，不去动任何一帧的所有权。
        /// </summary>
        private async Task<bool> WaitForCameraFrameAsync(TimeSpan timeout)
        {
            if (_isDisposed)
                return false;

            DateTime deadline = DateTime.Now + timeout;
            while (true)
            {
                if (HasRecentCameraFrame())
                    return true;

                TimeSpan remaining = deadline - DateTime.Now;
                if (remaining <= TimeSpan.Zero)
                    return false;

                // 就绪信号是一次性的，可能是更早那帧留下的：按小步长唤醒，等到真的重新来帧。
                await _cameraFrameReady.WaitAsync(
                    remaining < CameraFrameStaleThreshold ? remaining : CameraFrameStaleThreshold);
            }
        }

        /// <summary>
        /// 预录帧入队：载荷可能是 BGR，也可能是原始采样（NV12/YUY2）。
        /// 原始采样那一路的转换、旋转与水印由写入端完成（见 BackgroundFFmpegRecordingLoop）。
        /// </summary>
        private bool TryEnqueueFrameForRecording(PreRecordPayload payload)
        {
            try
            {
                if (payload == null) return false;
                if (_writeTask != null && _writeTask.IsCompleted) return false;

                var queue = _videoWriteQueue;
                bool added = queue != null && !queue.IsAddingCompleted
                    && queue.TryAdd(new RecordingVideoFrame(payload), 5);
                if (!added && DateTime.Now - _lastRecordingQueueWarnAt > TimeSpan.FromSeconds(5))
                {
                    _lastRecordingQueueWarnAt = DateTime.Now;
                    RuntimeLog.Warn("Recording", $"Pre-record frame enqueue failed, queueNull={queue == null}, addingCompleted={queue?.IsAddingCompleted}, queueCount={queue?.Count}, writeTask={_writeTask?.Status}");
                }
                return added;
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Recording", "Pre-record frame enqueue exception", ex);
                return false;
            }
        }
    }
}
