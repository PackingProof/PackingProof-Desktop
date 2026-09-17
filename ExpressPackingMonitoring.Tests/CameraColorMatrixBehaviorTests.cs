using ExpressPackingMonitoring.ViewModels;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 摄像头颜色校正的行为守护。
///
/// 这条路径跑在 60fps 采集线程上，预览与录像都要过一遍。实测 1080p 约 2.0 ms/帧，
/// 60fps 下约占一个核心的 12%；曾尝试改成逐像素查找表，实测 4.1 ms/帧反而更慢
/// （打不过 OpenCV 的 SIMD 实现），所以保留 Cv2.Transform。
/// 真正的出路是不在 CPU 上做这次转换，见 Services/MediaFoundation。
/// </summary>
public sealed class CameraColorMatrixBehaviorTests
{
    /// <summary>
    /// 校正结果必须与按系数矩阵做线性变换一致。允许 ±1 的差：取整位置不同，
    /// 而 1/255 的差异在画面上不可见。
    /// </summary>
    [Fact]
    public void MatchesMatrixTransform()
    {
        using Mat source = CreateGradientFrame();
        using Mat expected = TransformWithMatrix(source);
        using Mat actual = source.Clone();

        CameraColorMatrixPolicy.ApplyIfNeeded(actual, CameraColorMatrixPolicy.ModeBt709);

        using Mat difference = new();
        Cv2.Absdiff(expected, actual, difference);
        Cv2.MinMaxLoc(difference.Reshape(1), out _, out double maxDifference);
        Assert.True(maxDifference <= 1, $"与矩阵变换的最大偏差 {maxDifference}，超过 ±1");
    }

    /// <summary>中性色（R=G=B）必须完全不动：三行系数之和为 1，灰阶不该被染色。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(200)]
    [InlineData(255)]
    public void NeutralColorsStayUnchanged(byte level)
    {
        using var frame = new Mat(4, 4, MatType.CV_8UC3, Scalar.All(level));

        CameraColorMatrixPolicy.ApplyIfNeeded(frame, CameraColorMatrixPolicy.ModeBt709);

        Vec3b pixel = frame.Get<Vec3b>(2, 2);
        Assert.InRange(pixel.Item0, level - 1, level + 1);
        Assert.InRange(pixel.Item1, level - 1, level + 1);
        Assert.InRange(pixel.Item2, level - 1, level + 1);
    }

    /// <summary>边界值不能溢出回绕：0 和 255 必须夹在 0..255 内，不能变成另一端。</summary>
    [Fact]
    public void ExtremeValuesSaturateInsteadOfWrapping()
    {
        using var frame = new Mat(2, 3, MatType.CV_8UC3, Scalar.All(0));
        frame.Set(0, 0, new Vec3b(255, 0, 0));
        frame.Set(0, 1, new Vec3b(0, 255, 0));
        frame.Set(0, 2, new Vec3b(0, 0, 255));
        frame.Set(1, 0, new Vec3b(255, 255, 255));

        CameraColorMatrixPolicy.ApplyIfNeeded(frame, CameraColorMatrixPolicy.ModeBt709);

        // 纯蓝的 R 行系数为负，未夹取时会回绕成很大的值。
        Vec3b pureBlue = frame.Get<Vec3b>(0, 0);
        Assert.InRange(pureBlue.Item2, 0, 255);
        Vec3b white = frame.Get<Vec3b>(1, 0);
        Assert.InRange(white.Item0, 254, 255);
        Assert.InRange(white.Item1, 254, 255);
        Assert.InRange(white.Item2, 254, 255);
    }

    /// <summary>非 8UC3、空帧、null 都不能抛：这条路径在采集线程上，抛一次就丢一帧。</summary>
    [Fact]
    public void IgnoresUnsupportedFrames()
    {
        CameraColorMatrixPolicy.ApplyIfNeeded(null, CameraColorMatrixPolicy.ModeBt709);
        using var gray = new Mat(4, 4, MatType.CV_8UC1, Scalar.All(128));
        CameraColorMatrixPolicy.ApplyIfNeeded(gray, CameraColorMatrixPolicy.ModeBt709);
        using var empty = new Mat();
        CameraColorMatrixPolicy.ApplyIfNeeded(empty, CameraColorMatrixPolicy.ModeBt709);
    }

    /// <summary>
    /// 带边距（stride > width*3）的帧也要正确处理：ROI 取出来的子图就是这种，
    /// 按 width*3 而不是 stride 步进会把画面撕开。
    /// </summary>
    [Fact]
    public void HandlesFramesWithRowPadding()
    {
        using var full = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(0));
        full.Set(0, 0, new Vec3b(10, 20, 30));
        using Mat roi = new(full, new Rect(0, 0, 4, 4));
        Assert.True(roi.Step() > roi.Width * 3, "这个用例需要一个带行边距的帧");

        CameraColorMatrixPolicy.ApplyIfNeeded(roi, CameraColorMatrixPolicy.ModeBt709);

        // ROI 之外的像素不能被动到。
        Assert.Equal(new Vec3b(0, 0, 0), full.Get<Vec3b>(0, 5));
        Assert.Equal(new Vec3b(0, 0, 0), full.Get<Vec3b>(5, 0));
    }

    /// <summary>关闭校正时一个像素都不该变。</summary>
    [Fact]
    public void DoesNothingWhenDisabled()
    {
        using Mat source = CreateGradientFrame();
        using Mat actual = source.Clone();

        CameraColorMatrixPolicy.ApplyIfNeeded(actual, CameraColorMatrixPolicy.ModeOff);

        using Mat difference = new();
        Cv2.Absdiff(source, actual, difference);
        Assert.Equal(0, Cv2.CountNonZero(difference.Reshape(1)));
    }

    private static Mat CreateGradientFrame()
    {
        var frame = new Mat(64, 64, MatType.CV_8UC3);
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                frame.Set(y, x, new Vec3b(
                    (byte)(x * 4),
                    (byte)(y * 4),
                    (byte)((x + y) * 2)));
            }
        }
        return frame;
    }

    private static Mat TransformWithMatrix(Mat source)
    {
        Mat result = source.Clone();
        using Mat matrix = CreateReferenceMatrix();
        Cv2.Transform(result, result, matrix);
        return result;
    }

    /// <summary>与产品代码同一组系数（BGR 顺序），用于对照。</summary>
    private static Mat CreateReferenceMatrix()
    {
        var matrix = new Mat(3, 3, MatType.CV_32FC1);
        float[] coefficients =
        {
            1.04182f, -0.02771f, -0.01411f,
            0.05832f, 0.84563f, 0.09605f,
            -0.01403f, -0.07236f, 1.08639f,
        };
        matrix.SetArray(coefficients);
        return matrix;
    }
}
