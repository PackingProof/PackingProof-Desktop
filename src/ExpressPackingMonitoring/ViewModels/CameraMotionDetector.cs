using OpenCvSharp;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 录制静止检测只保留缩小后的上一帧，避免为一次低频判断常驻整张摄像头画面。
    /// 检测、复位和释放共用一把锁，录音启动刷新状态时不会与视频处理循环并发访问 Mat。
    /// </summary>
    internal sealed class CameraMotionDetector : IDisposable
    {
        private const int MotionWidth = 320;
        private const int MotionHeight = 240;
        private const double ChangeRatioThreshold = 0.01;

        private readonly object _gate = new();
        private Mat _previousSmall = new();
        private Mat _currentSmall = new();
        private Mat _currentGray = new();
        private Mat _previousGray = new();
        private Mat _difference = new();
        private Mat _thresholdMask = new();
        private bool _disposed;

        internal (int Width, int Height)? PreviousFrameSize
        {
            get
            {
                lock (_gate)
                {
                    return _disposed || _previousSmall.Empty()
                        ? null
                        : (_previousSmall.Width, _previousSmall.Height);
                }
            }
        }

        internal long AllocatedBytes
        {
            get
            {
                lock (_gate)
                {
                    if (_disposed)
                        return 0;
                    return GetMatBytes(_previousSmall)
                        + GetMatBytes(_currentSmall)
                        + GetMatBytes(_currentGray)
                        + GetMatBytes(_previousGray)
                        + GetMatBytes(_difference)
                        + GetMatBytes(_thresholdMask);
                }
            }
        }

        /// <summary>返回当前帧相对上一采样帧是否发生了足以重置静止超时的变化。</summary>
        internal bool Detect(Mat? currentFrame, double pixelThreshold)
        {
            lock (_gate)
            {
                if (_disposed || currentFrame == null || currentFrame.IsDisposed || currentFrame.Empty())
                    return false;

                var motionSize = new Size(MotionWidth, MotionHeight);
                if (_previousSmall.Empty())
                {
                    Cv2.Resize(currentFrame, _previousSmall, motionSize);
                    return false;
                }

                Cv2.Resize(currentFrame, _currentSmall, motionSize);
                Cv2.CvtColor(_currentSmall, _currentGray, ColorConversionCodes.BGR2GRAY);
                Cv2.CvtColor(_previousSmall, _previousGray, ColorConversionCodes.BGR2GRAY);
                Cv2.Absdiff(_currentGray, _previousGray, _difference);
                Cv2.Threshold(_difference, _thresholdMask, pixelThreshold, 255, ThresholdTypes.Binary);

                int changedPixels = Cv2.CountNonZero(_thresholdMask);
                double changeRatio = (double)changedPixels / (MotionWidth * MotionHeight);
                _currentSmall.CopyTo(_previousSmall);
                return changeRatio > ChangeRatioThreshold;
            }
        }

        /// <summary>开始新一段录制时丢弃上一段录制的采样帧，首帧只负责重建基准。</summary>
        internal void Reset()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                ReleaseRuntimeBuffersLocked();
            }
        }

        /// <summary>录制结束后释放工作 Mat；下一段录制首帧再按需分配。</summary>
        internal void ReleaseRuntimeBuffers()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                ReleaseRuntimeBuffersLocked();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                DisposeRuntimeBuffersLocked();
            }
        }

        private void ReleaseRuntimeBuffersLocked()
        {
            DisposeRuntimeBuffersLocked();
            _previousSmall = new Mat();
            _currentSmall = new Mat();
            _currentGray = new Mat();
            _previousGray = new Mat();
            _difference = new Mat();
            _thresholdMask = new Mat();
        }

        private void DisposeRuntimeBuffersLocked()
        {
            _previousSmall.Dispose();
            _currentSmall.Dispose();
            _currentGray.Dispose();
            _previousGray.Dispose();
            _difference.Dispose();
            _thresholdMask.Dispose();
        }

        private static long GetMatBytes(Mat mat) =>
            mat.Empty() ? 0 : (long)mat.Total() * mat.ElemSize();
    }
}
