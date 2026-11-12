using ExpressPackingMonitoring.Services;
using OpenCvSharp;
using Xunit;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 斜放面单和远处小面单的兜底解码。
///
/// 常规链路只有 0/90/180/270 四个朝向，一维码的扫描线又是水平的，
/// 斜着放的面单条纹方向和扫描线不重合，ZXing 直接读不出；远处的小面单
/// 则是条纹本身糊在一起，放大也救不回来。这两条兜底分别是"摆正"和"放大"。
/// </summary>
public sealed class CameraBarcodeEnhancedDecodeTests
{
    private const string Code = "79140907900402";

    [Theory]
    [InlineData(12.0)]
    [InlineData(-18.0)]
    [InlineData(25.0)]
    public void EstimatesSkewAngleOfRotatedBarcode(double angleDegrees)
    {
        using Mat barcode = CreateBarcodeMat(Code, angleDegrees);
        using Mat gray = new();
        Cv2.CvtColor(barcode, gray, ColorConversionCodes.BGR2GRAY);

        double? angle = CameraBarcodeDeskewPolicy.EstimateSkewAngle(gray);

        Assert.NotNull(angle);
        Assert.InRange(
            Math.Abs(angle!.Value),
            Math.Abs(angleDegrees) - 5.0,
            Math.Abs(angleDegrees) + 5.0);
    }

    [Fact]
    public void HorizontalBarcodeNeedsNoDeskew()
    {
        using Mat barcode = CreateBarcodeMat(Code, 0);
        using Mat gray = new();
        Cv2.CvtColor(barcode, gray, ColorConversionCodes.BGR2GRAY);

        // 已经摆正的条码不该被再转一次：多转只会引入插值模糊。
        Assert.Null(CameraBarcodeDeskewPolicy.EstimateSkewAngle(gray));
    }

    [Theory]
    [InlineData(25.0)]
    [InlineData(-32.0)]
    [InlineData(40.0)]
    public void SkewedBarcodeIsRecoveredByEnhancedDecode(double angleDegrees)
    {
        using Mat frame = CreateFrameWithSkewedBarcode(angleDegrees);

        // 关掉兜底时读不出来 —— 这正是现场"必须把面单摆正才能识别"的样子。
        using (var plain = new CameraBarcodeFrameDecoder(() => false, enableEnhancedDecode: false))
        {
            Assert.Null(plain.DecodeGuideRegion(frame));
        }

        using var decoder = new CameraBarcodeFrameDecoder(() => false);
        Assert.Equal(Code, decoder.DecodeGuideRegion(frame));
    }

    [Fact]
    public void LargePictureKeepsDecodingAfterEnhancement()
    {
        // 4K 画面里的面单：常规解码要缩到 1440，这条链路不能因此读错或读不出。
        using Mat frame = new(new OpenCvSharp.Size(3840, 2160), MatType.CV_8UC3, Scalar.White);
        using Mat barcode = CreateBarcodeMat(Code, 0, width: 600, height: 130);
        using Mat target = frame.SubMat(new Rect(1600, 1000, barcode.Width, barcode.Height));
        barcode.CopyTo(target);

        using var decoder = new CameraBarcodeFrameDecoder(() => false);
        Assert.Equal(Code, decoder.DecodeGuideRegion(frame));
    }

    [Fact]
    public void UpscaleTiersSkipUnusableResolutions()
    {
        // 1080p 画面：更高的档位只会把同一张图反复缩放，没有额外信息。
        Assert.Empty(CameraBarcodeUpscalePolicy.BuildTierDimensions(1920, 1080, 1440));

        // 4K 画面：2160 和 2880 两档都能提供比常规更细的条码。
        IReadOnlyList<int> tiers =
            CameraBarcodeUpscalePolicy.BuildTierDimensions(3840, 2160, 1440);
        Assert.Equal(new[] { 2160, 2880 }, tiers);

        // 像素上限要跟着边长一起涨，否则边长放宽了像素还是被压住。
        Assert.True(CameraBarcodeUpscalePolicy.PixelsForDimension(2160) > 1_200_000);
    }

    [Fact]
    public void DeskewKeepsBarcodeCentered()
    {
        using Mat barcode = CreateBarcodeMat(Code, 30);
        using Mat gray = new();
        Cv2.CvtColor(barcode, gray, ColorConversionCodes.BGR2GRAY);

        double? angle = CameraBarcodeDeskewPolicy.EstimateSkewAngle(gray);
        Assert.NotNull(angle);

        using Mat deskewed = CameraBarcodeDeskewPolicy.Deskew(gray, angle!.Value)!;
        Assert.False(deskewed.Empty());

        // 摆正之后再估一次：角度应该已经回到"不需要扶正"的范围内。
        double? after = CameraBarcodeDeskewPolicy.EstimateSkewAngle(deskewed);
        Assert.True(
            after is null || Math.Abs(after.Value) < 4.0,
            $"摆正后仍剩 {after?.ToString("F1") ?? "-"} 度倾角");
    }

    private static Mat CreateFrameWithSkewedBarcode(double angleDegrees) =>
        CreateFrameWithBarcode(CreateBarcodeMat(Code, angleDegrees));

    private static Mat CreateFrameWithBarcode(Mat barcode)
    {
        var frame = new Mat(new OpenCvSharp.Size(1280, 720), MatType.CV_8UC3, Scalar.White);
        using Mat target = frame.SubMat(new Rect(
            (frame.Width - barcode.Width) / 2,
            (frame.Height - barcode.Height) / 2,
            barcode.Width,
            barcode.Height));
        barcode.CopyTo(target);
        return frame;
    }

    /// <summary>合成一张把一维码摆成指定倾角的图（白底黑条，和真实面单一致）。</summary>
    private static Mat CreateBarcodeMat(
        string value,
        double angleDegrees,
        int width = 520,
        int height = 120)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions
            {
                Width = width,
                Height = height,
                Margin = 16,
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

        if (Math.Abs(angleDegrees) < 0.01)
            return barcode.Clone();

        double radians = angleDegrees * Math.PI / 180.0;
        double cos = Math.Abs(Math.Cos(radians));
        double sin = Math.Abs(Math.Sin(radians));
        // 旋转后的外接矩形：长条形的条码高度方向也会变长，
        // 只按 1/cos 放大会把条码上下切掉一截。
        int outWidth = (int)Math.Ceiling(barcode.Width * cos + barcode.Height * sin);
        int outHeight = (int)Math.Ceiling(barcode.Width * sin + barcode.Height * cos);
        using Mat matrix = Cv2.GetRotationMatrix2D(
            new Point2f(barcode.Width / 2f, barcode.Height / 2f),
            angleDegrees,
            1.0);
        matrix.At<double>(0, 2) += outWidth / 2.0 - barcode.Width / 2.0;
        matrix.At<double>(1, 2) += outHeight / 2.0 - barcode.Height / 2.0;

        Mat rotated = new();
        Cv2.WarpAffine(
            barcode,
            rotated,
            matrix,
            new Size(outWidth, outHeight),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            Scalar.White);
        return rotated;
    }
}
