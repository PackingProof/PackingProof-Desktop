namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>
/// 采集端的"这一帧要不要 BGR"分支：预录这种只存不处理的消费者可以只要原始采样，
/// 省掉 NV12→BGR 转换与发布克隆（2K 一帧合计好几毫秒）。
/// </summary>
public sealed partial class MfCameraSource
{
    /// <summary>订阅方没表态时按需要 BGR 处理（老行为）。</summary>
    private bool BgrFramesRequested()
    {
        Func<bool>? provider = BgrFrameRequestProvider;
        return provider == null || provider();
    }

    /// <summary>
    /// 从锁定的缓冲区描出原始采样视图。只有 NV12/YUY2 才有得省；
    /// RGB24/RGB32 这类系统已经转好的格式返回 false，走原来的转换路径。
    /// </summary>
    private bool TryDescribeRawFrame(IntPtr scanline0, int pitch, out CameraRawFrame raw)
    {
        raw = default;
        Guid subtype;
        int width;
        int height;
        bool useBt709;
        lock (_sync)
        {
            if (_stopping || _conversionBuffer == null)
                return false;

            subtype = _activeSubtype;
            width = ActualWidth;
            height = ActualHeight;
            useBt709 = _useBt709;
        }

        CameraRawPixelFormat format;
        if (subtype == MfInterop.MFVideoFormat_NV12)
            format = CameraRawPixelFormat.Nv12;
        else if (subtype == MfInterop.MFVideoFormat_YUY2)
            format = CameraRawPixelFormat.Yuy2;
        else
            return false;

        if (width <= 0 || height <= 0 || scanline0 == IntPtr.Zero || pitch <= 0)
            return false;

        raw = new CameraRawFrame(scanline0, pitch, width, height, format, useBt709);
        return true;
    }
}
