using ExpressPackingMonitoring.Services;
using OpenCvSharp;
using Xunit;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Tests;

public sealed class CameraBarcodeQuadrilateralDecoderTests
{
    [Fact]
    public void DecodesRotatedWaybillWithoutObb()
    {
        const string expected = "YT123456789012";
        using Mat frame = CreateRotatedWaybillFrame(expected, angleDegrees: 18);
        using var decoder = new CameraBarcodeFrameDecoder();
        using var fallback = new CameraBarcodeQuadrilateralDecoder(decoder);

        string? decoded = fallback.Decode(
            frame,
            value => value == expected,
            CameraBarcodeGuideGeometry.Default);

        Assert.Equal(expected, decoded);
        Assert.NotNull(fallback.LastGeometry);
    }

    [Fact]
    public void RejectsDecodedValueWhenBusinessValidatorFails()
    {
        const string barcode = "YT123456789012";
        using Mat frame = CreateRotatedWaybillFrame(barcode, angleDegrees: 18);
        using var decoder = new CameraBarcodeFrameDecoder();
        using var fallback = new CameraBarcodeQuadrilateralDecoder(decoder);

        string? decoded = fallback.Decode(
            frame,
            _ => false,
            CameraBarcodeGuideGeometry.Default);

        Assert.Null(decoded);
    }

    [Fact]
    public void BlankFrameDoesNotProduceACandidate()
    {
        using var frame = new Mat(
            new OpenCvSharp.Size(1280, 720),
            MatType.CV_8UC3,
            new Scalar(72, 76, 80));
        using var decoder = new CameraBarcodeFrameDecoder();
        using var fallback = new CameraBarcodeQuadrilateralDecoder(decoder);

        string? decoded = fallback.Decode(
            frame,
            _ => true,
            CameraBarcodeGuideGeometry.Default);

        Assert.Null(decoded);
        Assert.Null(fallback.LastGeometry);
    }

    private static Mat CreateRotatedWaybillFrame(string value, double angleDegrees)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions
            {
                Width = 520,
                Height = 150,
                Margin = 20,
                PureBarcode = true
            }
        };
        var pixels = writer.Write(value);
        using Mat bgra = Mat.FromPixelData(
            pixels.Height,
            pixels.Width,
            MatType.CV_8UC4,
            pixels.Pixels).Clone();
        using Mat barcode = new();
        Cv2.CvtColor(bgra, barcode, ColorConversionCodes.BGRA2BGR);

        using var label = new Mat(new OpenCvSharp.Size(620, 320), MatType.CV_8UC3, Scalar.White);
        using (Mat target = label.SubMat(new Rect(50, 85, barcode.Width, barcode.Height)))
            barcode.CopyTo(target);

        var tableColor = new Scalar(72, 76, 80);
        using Mat rotated = new();
        using Mat matrix = Cv2.GetRotationMatrix2D(
            new Point2f(label.Width / 2.0f, label.Height / 2.0f),
            angleDegrees,
            1.0);
        Cv2.WarpAffine(
            label,
            rotated,
            matrix,
            new OpenCvSharp.Size(800, 520),
            InterpolationFlags.Cubic,
            BorderTypes.Constant,
            tableColor);

        var frame = new Mat(new OpenCvSharp.Size(1280, 720), MatType.CV_8UC3, tableColor);
        int left = (frame.Width - rotated.Width) / 2;
        int top = (frame.Height - rotated.Height) / 2;
        using Mat frameTarget = frame.SubMat(new Rect(left, top, rotated.Width, rotated.Height));
        rotated.CopyTo(frameTarget);
        return frame;
    }
}
