using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

internal sealed partial class CameraBarcodeFrameDecoder
{
    internal bool QrCodeEnabledForDecoding => IsQrCodeEnabled();

    /// <summary>
    /// 小角度快速通道：只做一次轻量解码，失败后结束当前候选，
    /// 不再继续尝试慢速扫描或旋转校正。
    /// </summary>
    internal string? DecodeDetachedFast(Mat image)
    {
        LastGeometry = null;
        _lastBestCandidate = null;
        return _fastDecoder.Decode(image, QrCodeEnabledForDecoding);
    }
}
