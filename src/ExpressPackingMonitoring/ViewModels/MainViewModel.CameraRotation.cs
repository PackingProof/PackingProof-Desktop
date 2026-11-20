using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using OpenCvSharp;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 摄像头画面旋转带来的尺寸口径。
    ///
    /// 90/270 度会交换画面的宽高，而录制参数、副画面落位与识别框都读
    /// <c>_actualCameraWidth</c>/<c>_actualCameraHeight</c>。这两个尺寸必须与真正喂给
    /// 编码器的帧完全一致，否则 FFmpeg 会因为"帧尺寸与约定不符"把整帧丢弃。
    /// 这里集中这一套换算，避免在采集启动、网络流就绪、帧到达三处各写一遍而互相漂移。
    /// </summary>
    public partial class MainViewModel
    {
        private int _actualCameraRotationDegrees;

        /// <summary>采集端上报的是未旋转的画面尺寸，这里换算成旋转之后的实际尺寸。</summary>
        private void SetActualCameraSize(int width, int height)
        {
            int nextRotation = CameraFrameOrientation.NormalizeDegrees(
                Config.CameraRotationDegrees);
            (int nextWidth, int nextHeight) =
                CameraFrameOrientation.RotateDimensions(
                    width,
                    height,
                    nextRotation);
            if (_actualCameraWidth == nextWidth
                && _actualCameraHeight == nextHeight
                && _actualCameraRotationDegrees == nextRotation)
            {
                return;
            }

            (_actualCameraWidth, _actualCameraHeight) =
                (nextWidth, nextHeight);
            _actualCameraRotationDegrees = nextRotation;
            NotifyCameraVisionFrameGeometryChanged();
        }

        /// <summary>
        /// 帧到达后的兜底校正：运行中改角度或换设备后，启动时记录的尺寸可能与真实帧不符，
        /// 这里保证录制参数始终等于真正喂给编码器的帧尺寸。
        /// </summary>
        private void SyncActualCameraSizeToFrame(Mat frame)
        {
            if (frame == null || frame.Empty())
                return;
            if (_actualCameraWidth == frame.Width && _actualCameraHeight == frame.Height)
                return;

            RuntimeLog.Info(
                "Camera",
                $"画面尺寸随旋转调整为 {frame.Width}x{frame.Height}（原 {_actualCameraWidth}x{_actualCameraHeight}）");
            _actualCameraWidth = frame.Width;
            _actualCameraHeight = frame.Height;
            _actualCameraRotationDegrees =
                CameraFrameOrientation.NormalizeDegrees(
                    Config.CameraRotationDegrees);
            NotifyCameraVisionFrameGeometryChanged();
        }
    }
}
