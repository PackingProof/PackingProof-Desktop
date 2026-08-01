using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

internal static class CameraFrameOrientation
{
    internal static void Apply(Mat frame, bool rotate180)
    {
        if (!rotate180 || frame.Empty())
            return;

        Cv2.Flip(frame, frame, FlipMode.XY);
    }
}
