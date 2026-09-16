using ExpressPackingMonitoring.ViewModels;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 高清色度矩阵校正：DirectShow 固定按 BT.601 解码，高清源是 BT.709，
/// 这里验证判定规则与矩阵本身（数值取自现场同一帧的实测值）。
/// </summary>
public sealed class CameraColorMatrixPolicyTests
{
    /// <summary>现场实测：屏幕上真实按钮为 R245 G158 B11，经 601 解码后成 R235 G157 B18。</summary>
    private static readonly Vec3b MeasuredDecodedButton = new(18, 157, 235);

    /// <summary>同一帧按标准 BT.709 解码应得到的值（作为校正目标）。</summary>
    private static readonly Vec3b ExpectedCorrectedButton = new(11, 156, 243);

    [Theory]
    [InlineData(1920, true)]
    [InlineData(1280, true)]
    [InlineData(1024, false)]
    [InlineData(640, false)]
    public void AutoModeTreatsWideSourcesAsBt709(int frameWidth, bool expected)
    {
        Assert.Equal(expected, CameraColorMatrixPolicy.ShouldApply(CameraColorMatrixPolicy.ModeAuto, frameWidth));
    }

    [Fact]
    public void ExplicitModesOverrideResolution()
    {
        Assert.True(CameraColorMatrixPolicy.ShouldApply(CameraColorMatrixPolicy.ModeBt709, 640));
        Assert.False(CameraColorMatrixPolicy.ShouldApply(CameraColorMatrixPolicy.ModeBt601, 1920));
        Assert.False(CameraColorMatrixPolicy.ShouldApply(CameraColorMatrixPolicy.ModeOff, 1920));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("something-else")]
    public void UnknownModeFallsBackToAuto(string? mode)
    {
        Assert.Equal(CameraColorMatrixPolicy.ModeAuto, CameraColorMatrixPolicy.Normalize(mode));
        Assert.True(CameraColorMatrixPolicy.ShouldApply(mode, 1920));
        Assert.False(CameraColorMatrixPolicy.ShouldApply(mode, 640));
    }

    [Fact]
    public void ModeIsCaseAndWhitespaceInsensitive()
    {
        Assert.Equal(CameraColorMatrixPolicy.ModeBt709, CameraColorMatrixPolicy.Normalize("  BT709 "));
        Assert.Equal(CameraColorMatrixPolicy.ModeOff, CameraColorMatrixPolicy.Normalize("OFF"));
    }

    /// <summary>校正后应落在标准 BT.709 解码附近（实测误差 1/255 量级）。</summary>
    [Fact]
    public void CorrectionRestoresSaturatedColour()
    {
        using Mat frame = CreateFrame(1920, 1080, MeasuredDecodedButton);

        CameraColorMatrixPolicy.ApplyIfNeeded(frame, CameraColorMatrixPolicy.ModeAuto);

        Vec3b actual = frame.At<Vec3b>(10, 10);
        Assert.InRange(actual.Item0, ExpectedCorrectedButton.Item0 - 2, ExpectedCorrectedButton.Item0 + 2);
        Assert.InRange(actual.Item1, ExpectedCorrectedButton.Item1 - 2, ExpectedCorrectedButton.Item1 + 2);
        Assert.InRange(actual.Item2, ExpectedCorrectedButton.Item2 - 2, ExpectedCorrectedButton.Item2 + 2);
    }

    /// <summary>校正只放大色度：饱和的黄应该更黄（B 更低、R 更高）。</summary>
    [Fact]
    public void CorrectionIncreasesSaturation()
    {
        using Mat frame = CreateFrame(1920, 1080, MeasuredDecodedButton);

        CameraColorMatrixPolicy.ApplyIfNeeded(frame, CameraColorMatrixPolicy.ModeAuto);

        Vec3b actual = frame.At<Vec3b>(10, 10);
        Assert.True(actual.Item2 > MeasuredDecodedButton.Item2);
        Assert.True(actual.Item0 < MeasuredDecodedButton.Item0);
    }

    /// <summary>三行系数之和为 1，中性色（R=G=B）必须原样保留。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(192)]
    [InlineData(255)]
    public void NeutralGreyIsPreserved(byte level)
    {
        using Mat frame = CreateFrame(1920, 1080, new Vec3b(level, level, level));

        CameraColorMatrixPolicy.ApplyIfNeeded(frame, CameraColorMatrixPolicy.ModeAuto);

        Vec3b actual = frame.At<Vec3b>(10, 10);
        Assert.InRange(actual.Item0, level - 1, level + 1);
        Assert.InRange(actual.Item1, level - 1, level + 1);
        Assert.InRange(actual.Item2, level - 1, level + 1);
    }

    [Fact]
    public void SmallFrameIsLeftUntouchedInAutoMode()
    {
        using Mat frame = CreateFrame(640, 480, MeasuredDecodedButton);

        CameraColorMatrixPolicy.ApplyIfNeeded(frame, CameraColorMatrixPolicy.ModeAuto);

        Assert.Equal(MeasuredDecodedButton, frame.At<Vec3b>(10, 10));
    }

    [Fact]
    public void DisabledModeLeavesFullHdFrameUntouched()
    {
        using Mat frame = CreateFrame(1920, 1080, MeasuredDecodedButton);

        CameraColorMatrixPolicy.ApplyIfNeeded(frame, CameraColorMatrixPolicy.ModeOff);

        Assert.Equal(MeasuredDecodedButton, frame.At<Vec3b>(10, 10));
    }

    /// <summary>就地变换不能改变帧的尺寸与像素格式。</summary>
    [Fact]
    public void InPlaceTransformKeepsGeometryAndType()
    {
        using Mat frame = CreateFrame(1920, 1080, MeasuredDecodedButton);

        CameraColorMatrixPolicy.ApplyIfNeeded(frame, CameraColorMatrixPolicy.ModeBt709);

        Assert.Equal(1920, frame.Width);
        Assert.Equal(1080, frame.Height);
        Assert.Equal(MatType.CV_8UC3, frame.Type());
    }

    /// <summary>空帧与非法输入不应该抛异常（摄像头停流时会走到这里）。</summary>
    [Fact]
    public void EmptyOrNullFrameIsIgnored()
    {
        CameraColorMatrixPolicy.ApplyIfNeeded(null, CameraColorMatrixPolicy.ModeAuto);
        using var empty = new Mat();
        CameraColorMatrixPolicy.ApplyIfNeeded(empty, CameraColorMatrixPolicy.ModeAuto);
        using var singleChannel = new Mat(1080, 1920, MatType.CV_8UC1, new Scalar(128));
        CameraColorMatrixPolicy.ApplyIfNeeded(singleChannel, CameraColorMatrixPolicy.ModeAuto);
    }

    private static Mat CreateFrame(int width, int height, Vec3b colour)
        => new(height, width, MatType.CV_8UC3, new Scalar(colour.Item0, colour.Item1, colour.Item2));
}
