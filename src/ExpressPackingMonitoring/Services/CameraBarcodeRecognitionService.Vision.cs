using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

internal sealed partial class CameraBarcodeRecognitionService
{
    /// <summary>
    /// 只提交 OBB 视觉帧，不进入条码解码队列。录像/确认等忙碌阶段仍可让
    /// 面单框继续跟随，但不会重复触发扫码。
    /// </summary>
    public bool TrySubmitVisionFrame(Mat frame)
    {
        if (_disposed || frame == null || frame.IsDisposed || frame.Empty())
            return false;

        lock (_pendingLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan visionInterval = _visionFollowIntervalProvider();
            if (visionInterval <= TimeSpan.Zero)
                visionInterval = GuideInterval;
            if (_lastVisionUpdateAt != default
                && now - _lastVisionUpdateAt < visionInterval)
            {
                return false;
            }
            if (!_visionWorker.TryEnqueue(
                    frame,
                    _guideGeometryProvider(),
                    _generation))
            {
                return false;
            }
            _lastVisionUpdateAt = now;
            return true;
        }
    }
}
