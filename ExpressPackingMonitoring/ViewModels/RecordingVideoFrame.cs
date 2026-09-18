using OpenCvSharp;

namespace ExpressPackingMonitoring.ViewModels;

/// <summary>录像队列独占像素和采集时刻；预录帧使用已有独立时间轴。</summary>
internal sealed class RecordingVideoFrame(Mat frame, long capturedTicks) : IDisposable
{
    internal Mat Frame { get; } = frame;
    internal long CapturedTicks { get; } = capturedTicks;
    internal bool IsPreRecord => CapturedTicks == 0;
    public void Dispose() => Frame.Dispose();
}
