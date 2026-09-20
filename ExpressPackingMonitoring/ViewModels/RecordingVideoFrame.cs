using OpenCvSharp;

namespace ExpressPackingMonitoring.ViewModels;

/// <summary>
/// 录像队列里的一帧，队列独占其像素与采集时刻。
///
/// 实时帧走 <see cref="Frame"/>（BGR，处理循环里已经按当前配置画好水印）。
/// 预录帧走 <see cref="Payload"/>：BGR（没有原始采样的路径）或原始采样（NV12/YUY2）——
/// 后者的转换、旋转与水印都在写入端做，所以把采集时刻一起带过去给水印用。
/// </summary>
internal sealed class RecordingVideoFrame : IDisposable
{
    internal RecordingVideoFrame(Mat frame, long capturedTicks)
    {
        Frame = frame;
        CapturedTicks = capturedTicks;
    }

    internal RecordingVideoFrame(PreRecordPayload payload)
    {
        Payload = payload;
        WatermarkTime = payload.Timestamp;
    }

    /// <summary>BGR 像素；预录里存原始采样的那一帧为 null（由写入端解码）。</summary>
    internal Mat? Frame { get; }

    internal PreRecordPayload? Payload { get; }

    /// <summary>写入端画预录水印用的采集时刻；实时帧为 MinValue（循环里已经画过）。</summary>
    internal DateTime WatermarkTime { get; } = DateTime.MinValue;

    internal long CapturedTicks { get; }

    internal bool IsPreRecord => CapturedTicks == 0;

    public void Dispose()
    {
        Frame?.Dispose();
        Payload?.Dispose();
    }
}
