using ExpressPackingMonitoring.Logging;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>
/// 按符号链接打开一台采集设备。
///
/// 用符号链接而不是枚举下标：下标会随插拔顺序变化，符号链接是稳定标识，
/// 摄像头重新插拔、系统重启后仍然指向同一台设备。
/// </summary>
internal static class MfCaptureSourceFactory
{
    [DllImport("mf.dll", ExactSpelling = true)]
    private static extern int MFCreateDeviceSource(
        IMFAttributes attributes,
        out IMFMediaSource mediaSource);

    /// <summary>创建媒体源。失败返回 null，由调用方回退到旧采集路径。</summary>
    internal static IMFMediaSource? TryCreateSource(string symbolicLink)
    {
        if (string.IsNullOrWhiteSpace(symbolicLink))
            return null;

        IMFAttributes? attributes = null;
        try
        {
            if (MfInterop.MFCreateAttributes(out attributes, 2) < 0)
                return null;

            Guid sourceTypeKey = MfInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE;
            Guid videoCapture = MfInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID;
            if (attributes.SetGUID(ref sourceTypeKey, ref videoCapture) < 0)
                return null;

            Guid linkKey = MfInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK;
            if (attributes.SetString(ref linkKey, symbolicLink) < 0)
                return null;

            return MFCreateDeviceSource(attributes, out IMFMediaSource source) < 0
                ? null
                : source;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Camera", $"打开 Media Foundation 采集设备失败：{ex.Message}");
            return null;
        }
        finally
        {
            if (attributes != null)
                Marshal.ReleaseComObject(attributes);
        }
    }
}

/// <summary>从媒体类型里读出我们关心的字段。</summary>
internal static class MfMediaTypeReader
{
    /// <summary>读一个视频媒体类型。不是视频、或缺少必要字段时返回 null。</summary>
    internal static MfNativeFormat? Read(IMFMediaType mediaType)
    {
        if (mediaType == null)
            return null;

        try
        {
            Guid majorKey = MfInterop.MF_MT_MAJOR_TYPE;
            if (mediaType.GetGUID(ref majorKey, out Guid majorType) < 0
                || majorType != MfInterop.MFMediaType_Video)
            {
                return null;
            }

            Guid subtypeKey = MfInterop.MF_MT_SUBTYPE;
            if (mediaType.GetGUID(ref subtypeKey, out Guid subtype) < 0)
                return null;

            Guid sizeKey = MfInterop.MF_MT_FRAME_SIZE;
            if (mediaType.GetUINT64(ref sizeKey, out long packedSize) < 0)
                return null;

            // 帧尺寸与帧率都打包在一个 64 位值里：高 32 位在前。
            int width = (int)(packedSize >> 32);
            int height = (int)(packedSize & 0xFFFFFFFF);
            if (width <= 0 || height <= 0)
                return null;

            int numerator = 0;
            int denominator = 1;
            Guid rateKey = MfInterop.MF_MT_FRAME_RATE;
            if (mediaType.GetUINT64(ref rateKey, out long packedRate) >= 0)
            {
                numerator = (int)(packedRate >> 32);
                denominator = (int)(packedRate & 0xFFFFFFFF);
                if (denominator <= 0)
                    denominator = 1;
            }

            return new MfNativeFormat(subtype, width, height, numerator, denominator);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 读色彩空间标记。读到了就不必按分辨率猜 BT.601/709 ——
    /// 现在那个"宽度 ≥ 1280 就当 BT.709"的启发式对 720p 的 601 源、
    /// 640x480 的 709 源都会判错。
    /// </summary>
    internal static MfColorInfo ReadColorInfo(IMFMediaType mediaType)
    {
        int? yuvMatrix = TryReadUInt32(mediaType, MfInterop.MF_MT_YUV_MATRIX);
        int? primaries = TryReadUInt32(mediaType, MfInterop.MF_MT_VIDEO_PRIMARIES);
        int? transfer = TryReadUInt32(mediaType, MfInterop.MF_MT_TRANSFER_FUNCTION);
        int? nominalRange = TryReadUInt32(mediaType, MfInterop.MF_MT_VIDEO_NOMINAL_RANGE);
        return new MfColorInfo(yuvMatrix, primaries, transfer, nominalRange);
    }

    private static int? TryReadUInt32(IMFMediaType mediaType, Guid key)
    {
        try
        {
            return mediaType.GetUINT32(ref key, out int value) < 0 ? null : value;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// 媒体类型携带的色彩空间信息。全为 null 表示摄像头没有声明，
/// 这时才退回按分辨率推断。
/// </summary>
internal sealed record MfColorInfo(
    int? YuvMatrix,
    int? VideoPrimaries,
    int? TransferFunction,
    int? NominalRange)
{
    /// <summary>MFVideoTransferMatrix_BT709 = 1。</summary>
    private const int TransferMatrixBt709 = 1;

    /// <summary>MFVideoTransferMatrix_BT601 = 2。</summary>
    private const int TransferMatrixBt601 = 2;

    internal bool HasAnyInformation =>
        YuvMatrix.HasValue
        || VideoPrimaries.HasValue
        || TransferFunction.HasValue
        || NominalRange.HasValue;

    /// <summary>
    /// 摄像头是否明确声明了 BT.709。返回 null 表示没声明（要按分辨率推断）。
    /// </summary>
    internal bool? DeclaresBt709 => YuvMatrix switch
    {
        TransferMatrixBt709 => true,
        TransferMatrixBt601 => false,
        _ => null,
    };

    public override string ToString() =>
        $"yuvMatrix={Describe(YuvMatrix)}, primaries={Describe(VideoPrimaries)}, "
        + $"transfer={Describe(TransferFunction)}, range={Describe(NominalRange)}";

    private static string Describe(int? value) => value?.ToString() ?? "未声明";
}
