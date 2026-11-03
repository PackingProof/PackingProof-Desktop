using OpenCvSharp;
using System.Runtime.InteropServices;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 小角度候选的一次性快速解码。只运行轻量 ZXing 配置，失败后不继续慢速扫描。
/// </summary>
internal sealed class CameraBarcodeFastDecoder : IDisposable
{
    private static readonly HashSet<BarcodeFormat> LinearFormats =
    [
        BarcodeFormat.CODE_128,
        BarcodeFormat.CODE_39,
        BarcodeFormat.CODE_93,
        BarcodeFormat.CODABAR
    ];

    private static readonly List<BarcodeFormat> FormatsWithQrCode =
    [
        BarcodeFormat.CODE_128,
        BarcodeFormat.CODE_39,
        BarcodeFormat.CODE_93,
        BarcodeFormat.CODABAR,
        BarcodeFormat.QR_CODE
    ];

    private readonly BarcodeReaderGeneric _linearReader = new()
    {
        AutoRotate = false,
        Options = new DecodingOptions
        {
            TryHarder = false,
            PossibleFormats = LinearFormats.ToList()
        }
    };
    private readonly BarcodeReaderGeneric _readerWithQrCode = new()
    {
        AutoRotate = false,
        Options = new DecodingOptions
        {
            TryHarder = false,
            PossibleFormats = FormatsWithQrCode.ToList()
        }
    };
    private readonly Mat _gray = new();
    private readonly Mat _scaled = new();
    private readonly Mat _continuous = new();
    private byte[] _pixels = [];
    private byte[] _orientationScratch = [];
    private bool _disposed;

    internal int PixelBufferAllocationCount { get; private set; }

    internal string? Decode(Mat image, bool qrCodeEnabled)
    {
        if (_disposed || image == null || image.IsDisposed || image.Empty())
            return null;
        if (!CameraBarcodeDecodeSizePolicy.ShouldDecode(
                image.Width,
                image.Height,
                qrCodeEnabled))
        {
            return null;
        }

        switch (image.Channels())
        {
            case 1:
                image.CopyTo(_gray);
                break;
            case 3:
                Cv2.CvtColor(image, _gray, ColorConversionCodes.BGR2GRAY);
                break;
            case 4:
                Cv2.CvtColor(image, _gray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                return null;
        }

        Mat source = PrepareDecodeSize(_gray, _scaled);
        if (!source.IsContinuous())
        {
            source.CopyTo(_continuous);
            source = _continuous;
        }

        int length = checked(source.Width * source.Height);
        EnsureBuffers(length);
        Marshal.Copy(source.Data, _pixels, 0, length);

        BarcodeReaderGeneric reader = qrCodeEnabled
            ? _readerWithQrCode
            : _linearReader;
        Result? result = null;
        for (int orientation = 0; orientation < 4 && result == null; orientation++)
        {
            var luminance = new ReusableGrayLuminanceSource(
                _pixels,
                _orientationScratch,
                source.Width,
                source.Height,
                orientation);
            result = reader.Decode(luminance);
        }
        if (result == null
            || !IsAcceptedFormat(result.BarcodeFormat, qrCodeEnabled))
        {
            return null;
        }

        string normalized = (result.Text ?? "").Trim().ToUpperInvariant();
        if (normalized.Length == 0 || JdBarcodePolicy.IsBareWaybill(normalized))
            return null;
        return JdBarcodePolicy.Normalize(normalized);
    }

    private static bool IsAcceptedFormat(
        BarcodeFormat format,
        bool qrCodeEnabled) =>
        format == BarcodeFormat.QR_CODE
            ? qrCodeEnabled
            : LinearFormats.Contains(format);

    private static Mat PrepareDecodeSize(Mat gray, Mat scaled)
    {
        double dimensionScale =
            (double)CameraBarcodeFrameDecoder.MaxDecodeDimension
            / Math.Max(gray.Width, gray.Height);
        double pixelScale = Math.Sqrt(
            (double)CameraBarcodeFrameDecoder.MaxDecodePixels
            / (gray.Width * (double)gray.Height));
        double scale = Math.Min(1, Math.Min(dimensionScale, pixelScale));
        if (scale >= 0.999)
            return gray;

        int width = Math.Max(1, (int)Math.Round(gray.Width * scale));
        int height = Math.Max(1, (int)Math.Round(gray.Height * scale));
        Cv2.Resize(
            gray,
            scaled,
            new Size(width, height),
            interpolation: InterpolationFlags.Area);
        return scaled;
    }

    private void EnsureBuffers(int length)
    {
        if (_pixels.Length == length && _orientationScratch.Length == length)
            return;
        _pixels = GC.AllocateUninitializedArray<byte>(length);
        _orientationScratch = GC.AllocateUninitializedArray<byte>(length);
        PixelBufferAllocationCount++;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gray.Dispose();
        _scaled.Dispose();
        _continuous.Dispose();
    }
}
