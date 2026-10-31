using ExpressPackingMonitoring.Services;
using OpenCvSharp;
using Xunit;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 面单定向（YOLO-OBB）的几何计算与降级行为。
///
/// 模型文件和 ONNX Runtime 原生库都不随仓库分发，所以这里**不测真实推理**
/// （CI 上没有模型），只测两件必须有保障的事：
///   ① 旋转框的角度归一化、IoU、重叠抑制算对；
///   ② 模型缺失时整条链路安静降级 —— 不会崩，也不会把原本能识别的画面弄坏。
///
/// 真实推理的口径（letterbox 114 灰、NCHW 三平面、[cx,cy,w,h,cls,angle]）
/// 在 PR 说明里写了验证方法，需要自备模型和运行时才能复现。
/// </summary>
public sealed class CameraBarcodeObbPolicyTests
{
    private const string Code = "79140907900402";

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(30.0, 30.0)]
    [InlineData(-30.0, -30.0)]
    [InlineData(50.0, -40.0)]
    [InlineData(-50.0, 40.0)]
    [InlineData(90.0, 0.0)]
    [InlineData(-90.0, 0.0)]
    [InlineData(100.0, 10.0)]
    [InlineData(-100.0, -10.0)]
    public void NormalizesAxisAngleIntoHalfTurn(double input, double expected)
    {
        // 摆正只关心"条纹方向模 90"：长轴和短轴差 90 度是同一个框，
        // 超过 45 度时走另一头更近，插值损失更小。
        Assert.Equal(expected, CameraBarcodeObbPolicy.NormalizeAxisAngle(input), 3);
    }

    [Fact]
    public void IdenticalBoxesHaveFullOverlap()
    {
        var box = new LabelObbDetection(100, 100, 80, 40, 0, 0.9f);

        Assert.Equal(1.0, CameraBarcodeObbPolicy.IntersectionOverUnion(box, box), 2);
    }

    [Fact]
    public void DisjointBoxesHaveNoOverlap()
    {
        var a = new LabelObbDetection(100, 100, 80, 40, 0, 0.9f);
        var b = new LabelObbDetection(500, 500, 80, 40, 0, 0.8f);

        Assert.Equal(0.0, CameraBarcodeObbPolicy.IntersectionOverUnion(a, b), 3);
    }

    [Fact]
    public void HalfOverlapGivesOneThirdIou()
    {
        // 两个 100×100 的框水平错开 50：交集 50×100，并集 150×100。
        var a = new LabelObbDetection(100, 100, 100, 100, 0, 0.9f);
        var b = new LabelObbDetection(150, 100, 100, 100, 0, 0.9f);

        Assert.Equal(1.0 / 3.0, CameraBarcodeObbPolicy.IntersectionOverUnion(a, b), 2);
    }

    [Fact]
    public void RotatedBoxCornersSwapExtents()
    {
        // 100×20 的框转 90 度之后，横向跨度变成原高度、纵向变成原宽度。
        var rotated = new LabelObbDetection(0, 0, 100, 20, 90, 0.9f);
        Point2f[] corners = CameraBarcodeObbPolicy.ToCorners(rotated);

        double width = corners.Max(p => p.X) - corners.Min(p => p.X);
        double height = corners.Max(p => p.Y) - corners.Min(p => p.Y);
        Assert.Equal(20, width, 1);
        Assert.Equal(100, height, 1);
    }

    [Fact]
    public void SuppressOverlapsKeepsHighestScore()
    {
        var high = new LabelObbDetection(100, 100, 100, 100, 0, 0.90f);
        var overlapping = new LabelObbDetection(105, 100, 100, 100, 0, 0.40f);
        var separate = new LabelObbDetection(400, 400, 100, 100, 0, 0.50f);

        List<LabelObbDetection> kept = CameraBarcodeObbPolicy.SuppressOverlaps(
            [overlapping, high, separate],
            0.45f);

        Assert.Equal(2, kept.Count);
        Assert.Equal(0.90f, kept[0].Score);
        Assert.Equal(400, kept[1].CenterX, 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryLoadIgnoresEmptyPath(string? path)
    {
        Assert.Null(YoloObbDetector.TryLoad(path));
    }

    [Fact]
    public void TryLoadReturnsNullWhenModelMissing()
    {
        // 可选能力：模型缺失只能降级，绝不能抛异常把识别链路带崩。
        Assert.Null(YoloObbDetector.TryLoad(@"Z:\definitely-not-here\model.onnx"));
    }

    [Fact]
    public void ExternalAngleProviderDrivesDeskew()
    {
        // 用「假模型」直接告诉链路倾角，验证 YOLO 接入点确实接对了：
        // 这条路径不依赖真实模型，所以 CI 上也能跑。
        using Mat frame = CreateFrameWithSkewedBarcode(25.0);
        int calls = 0;
        using var decoder = new CameraBarcodeFrameDecoder(
            () => false,
            enableEnhancedDecode: true,
            obbAngleProvider: _ =>
            {
                calls++;
                return -25.0;
            });

        Assert.Equal(Code, decoder.DecodeGuideRegion(frame));
        Assert.True(calls > 0, "外部角度提供者应当被调用");
    }

    [Fact]
    public void FallsBackToGradientWhenModelUnavailable()
    {
        // 模型不可用（一直返回 null）时，必须退回全画面梯度方向估角，
        // 结果要和没有 YOLO 时一致。
        using Mat frame = CreateFrameWithSkewedBarcode(25.0);
        using var decoder = new CameraBarcodeFrameDecoder(
            () => false,
            enableEnhancedDecode: true,
            obbAngleProvider: _ => null);

        Assert.Equal(Code, decoder.DecodeGuideRegion(frame));
    }

    [Fact]
    public void StraightBarcodeFromModelIsNotDeskewed()
    {
        // 模型说面单是正的（0 度）时，不要再退回梯度法把它转歪 ——
        // "模型判为正"和"模型没给出结果"是两件事。
        using Mat frame = CreateFrameWithStraightBarcode();
        using var decoder = new CameraBarcodeFrameDecoder(
            () => false,
            enableEnhancedDecode: true,
            obbAngleProvider: _ => 0.0);

        Assert.Equal(Code, decoder.DecodeGuideRegion(frame));
    }

    private static Mat CreateFrameWithSkewedBarcode(double angleDegrees) =>
        CreateFrameWithBarcode(angleDegrees);

    private static Mat CreateFrameWithStraightBarcode() => CreateFrameWithBarcode(0);

    private static Mat CreateFrameWithBarcode(double angleDegrees)
    {
        using Mat barcode = CreateBarcodeMat(Code, angleDegrees);
        var frame = new Mat(new OpenCvSharp.Size(1280, 720), MatType.CV_8UC3, Scalar.White);
        using Mat target = frame.SubMat(new Rect(
            (frame.Width - barcode.Width) / 2,
            (frame.Height - barcode.Height) / 2,
            barcode.Width,
            barcode.Height));
        barcode.CopyTo(target);
        return frame;
    }

    /// <summary>
    /// 合成一张把一维码摆成指定倾角的图（白底黑条）。
    ///
    /// 与 <c>CameraBarcodeEnhancedDecodeTests</c> 里的同名方法一致，故意各留一份：
    /// 两个 PR 之间不互相牵制，改动不会因为另一个还没合并而卡住。
    /// </summary>
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
        // 旋转后的外接矩形：长条形面单高度方向也会变长，只按 1/cos 放大会切掉上下。
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
