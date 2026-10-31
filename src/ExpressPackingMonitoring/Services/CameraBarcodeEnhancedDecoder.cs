using ExpressPackingMonitoring.Logging;
using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 常规解码失败后的兜底：先把斜放的面单摆正，再按更高的分辨率档位重试。
///
/// 这条链路只在正常路径已经读不出码时才会走，正常情况下不增加任何开销。
/// 摆正和提分辨率是两件事：
///   摆正解决"条码斜着放，水平扫描线对不上"；
///   提分辨率解决"4K 画面被缩到解码上限，条码条纹被抹平"。
///
/// 摆正后的图与原图不是同一个坐标系，所以这里只把解码出的字符串交回去，
/// 不返回候选框 —— 让上层别拿变换后的坐标去画框。
/// </summary>
internal sealed class CameraBarcodeEnhancedDecoder : IDisposable
{
    private const string LogCategory = "CameraBarcode";

    private readonly Mat _gray = new();
    private readonly Mat _mid = new();
    private readonly Mat _scaled = new();
    private bool _disposed;

    /// <summary>
    /// 试一次增强解码。命中返回 true，<paramref name="code"/> 为解出的单号。
    /// </summary>
    /// <param name="roi">引导框裁出来的区域（彩色或灰度都行）。</param>
    /// <param name="decode">对一张图做解码的委托，返回 null 表示没读出来。</param>
    /// <param name="code">解出的单号。</param>
    /// <param name="obbAngleProvider">
    /// 可选的面单定向：给定原始 ROI，返回面单长轴与水平线的夹角（度）。
    /// 返回 null 表示"这一帧拿不到"（模型不可用或没检出面单），
    /// 此时退回全画面梯度方向去估 —— 注意这和"模型说面单是正的"是两回事。
    /// </param>
    public bool TryDecode(
        Mat roi,
        Func<Mat, string?> decode,
        out string? code,
        Func<Mat, double?>? obbAngleProvider = null)
    {
        code = null;
        if (_disposed || roi == null || roi.IsDisposed || roi.Empty())
            return false;

        switch (roi.Channels())
        {
            case 1:
                roi.CopyTo(_gray);
                break;
            case 3:
                Cv2.CvtColor(roi, _gray, ColorConversionCodes.BGR2GRAY);
                break;
            case 4:
                Cv2.CvtColor(roi, _gray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                return false;
        }

        double? angle = null;
        bool angleDecidedByModel = false;
        if (obbAngleProvider != null)
        {
            // 模型直接给角度，优先用它：画面里有多个方向不同的面单、
            // 或者背景有规则斜纹时，梯度法会挑错方向，模型不会。
            angle = obbAngleProvider(roi);
            if (angle.HasValue)
            {
                angleDecidedByModel = true;
                angle = CameraBarcodeObbPolicy.NormalizeAxisAngle(angle.Value);
                if (Math.Abs(angle.Value) < CameraBarcodeDeskewPolicy.MinAngle ||
                    Math.Abs(angle.Value) > CameraBarcodeDeskewPolicy.MaxAngle)
                {
                    // 模型判为「本来就正」或「接近竖直」→ 不扶正，
                    // 也不要再退回梯度法把它转歪。
                    angle = null;
                }
            }
        }

        if (!angleDecidedByModel)
        {
            // 估角在常规分辨率上做就够：角度与分辨率无关，
            // 而 4K 图上跑一遍 Sobel 明显更慢。
            ResizeToLimit(
                _gray,
                CameraBarcodeFrameDecoder.MaxDecodeDimension,
                CameraBarcodeUpscalePolicy.PixelsForDimension(
                    CameraBarcodeFrameDecoder.MaxDecodeDimension),
                _mid);
            angle = CameraBarcodeDeskewPolicy.EstimateSkewAngle(_mid);
        }

        Mat? working = null;
        try
        {
            // 摆正在原分辨率上做：远景面单本来像素就少，
            // 先缩再摆会再糊一次，等于把唯一的信息也丢了。
            if (angle.HasValue)
            {
                working = CameraBarcodeDeskewPolicy.Deskew(_gray, angle.Value);
                if (working != null)
                {
                    code = TryDecodeOnce(decode, working);
                    if (!string.IsNullOrEmpty(code))
                    {
                        RuntimeLog.Info(
                            LogCategory,
                            $"常规解码未命中，扶正 {angle.Value:F1}° 后读出 {code}");
                        return true;
                    }
                }
            }

            Mat source = working ?? _gray;
            IReadOnlyList<int> tiers = CameraBarcodeUpscalePolicy.BuildTierDimensions(
                source.Width,
                source.Height,
                CameraBarcodeFrameDecoder.MaxDecodeDimension);

            foreach (int dimension in tiers)
            {
                ResizeToLimit(
                    source,
                    dimension,
                    CameraBarcodeUpscalePolicy.PixelsForDimension(dimension),
                    _scaled);
                code = TryDecodeOnce(decode, _scaled);
                if (!string.IsNullOrEmpty(code))
                {
                    RuntimeLog.Info(
                        LogCategory,
                        $"常规解码未命中，按 {dimension} 像素档位重试后读出 {code}");
                    return true;
                }
            }
        }
        finally
        {
            working?.Dispose();
        }

        code = null;
        return false;
    }

    private static string? TryDecodeOnce(Func<Mat, string?> decode, Mat image)
    {
        try
        {
            return decode(image);
        }
        catch
        {
            // 兜底链路失败不能影响主流程：读不出就还是读不出。
            return null;
        }
    }

    /// <summary>把图缩到给定边长与像素上限内（只缩不放，和解码器的口径一致）。</summary>
    private static void ResizeToLimit(Mat source, int maxDimension, long maxPixels, Mat destination)
    {
        double dimensionScale = (double)maxDimension / Math.Max(source.Width, source.Height);
        double pixelScale = Math.Sqrt(
            (double)maxPixels / ((double)source.Width * source.Height));
        double scale = Math.Min(1, Math.Min(dimensionScale, pixelScale));
        if (scale >= 0.999)
        {
            source.CopyTo(destination);
            return;
        }

        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        Cv2.Resize(
            source,
            destination,
            new Size(width, height),
            interpolation: InterpolationFlags.Area);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gray.Dispose();
        _mid.Dispose();
        _scaled.Dispose();
    }
}
