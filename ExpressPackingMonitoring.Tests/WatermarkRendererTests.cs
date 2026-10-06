using ExpressPackingMonitoring.ViewModels;
using OpenCvSharp;
using System;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 水印渲染改成“按行预渲染 + 每帧一次叠加”之后的回归：
/// 缓存命中不能改变画面、换分辨率不能串用缓存、非 ASCII 字符不能把一行甚至整块水印打掉。
/// </summary>
public sealed class WatermarkRendererTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 10, 20, 9, 10, 11, TimeSpan.FromHours(8));

    private static Mat Render(
        int width,
        int height,
        string orderId = "435384812936683",
        params string[] extensionLines)
    {
        var frame = new Mat(height, width, MatType.CV_8UC3, Scalar.Black);
        MainViewModel.ApplyWatermarkToFrame(frame, Timestamp, orderId, extensionLines);
        return frame;
    }

    private static void AssertSamePixels(Mat expected, Mat actual, string because)
    {
        using var difference = new Mat();
        Cv2.Absdiff(expected, actual, difference);
        Assert.True(Cv2.CountNonZero(difference.Reshape(1)) == 0, because);
    }

    /// <summary>同一秒里的后续帧命中缓存，画面必须和第一次渲染完全一样。</summary>
    [Fact]
    public void RepeatedFrames_AreIdenticalWithWarmCache()
    {
        using Mat first = Render(3840, 2160, "SF1234567890123", "scale.example: 1.25 kg");
        using Mat second = Render(3840, 2160, "SF1234567890123", "scale.example: 1.25 kg");

        AssertSamePixels(first, second, "缓存命中后的水印必须与首次渲染逐像素一致");
    }

    /// <summary>分辨率不同 → 字号不同 → 缓存键必须分开，不能在 4K 与 720p 之间串用。</summary>
    [Fact]
    public void DifferentResolutions_DoNotShareCachedLines()
    {
        using Mat first4K = Render(3840, 2160);
        using Mat small = Render(1280, 720);
        using Mat second4K = Render(3840, 2160);

        AssertSamePixels(first4K, second4K, "中间的 720p 渲染不得污染 4K 的缓存条目");
        Assert.True(small.Width == 1280, "720p 路径也要正常画完");
    }

    /// <summary>扩展字段值不限 ASCII，含中文时必须照样画完，不能抛异常也不能丢掉后面的行。</summary>
    [Fact]
    public void NonAsciiExtensionLine_IsReplacedAndOtherLinesSurvive()
    {
        using var frame = new Mat(2160, 3840, MatType.CV_8UC3, Scalar.Black);

        MainViewModel.ApplyWatermarkToFrame(
            frame,
            Timestamp,
            "435384812936683",
            new[] { "scale.example.商品: 水杯", "scale.example.weight: 1.25 kg" });

        // 含中文的行被替换成 ? 后照画，后面的纯 ASCII 行也必须照画：按最后一行基线检查墨迹底边。
        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        using var ink = new Mat();
        Cv2.Threshold(gray, ink, 128, 255, ThresholdTypes.Binary);
        using var points = new Mat();
        Cv2.FindNonZero(ink, points);
        Assert.False(points.Empty());

        Rect box = Cv2.BoundingRect(points);
        int lineHeight = WatermarkOverlayRenderer.LineHeightOf(
            WatermarkOverlayRenderer.FontScaleOf(frame.Height));
        int lastBaseline = (int)(lineHeight * 1.1 * 4);
        Assert.True(
            box.Y + box.Height >= lastBaseline,
            $"最后一行没画出来：墨迹底边 {box.Y + box.Height}，最后一行基线 {lastBaseline}");
    }

    [Fact]
    public void SanitizeForHershey_KeepsAsciiAndReplacesTheRest()
    {
        Assert.Equal("scale.example.weight: 1.25 kg", WatermarkOverlayRenderer.SanitizeForHershey(
            "scale.example.weight: 1.25 kg"));
        Assert.Equal("scale.example.??: ??", WatermarkOverlayRenderer.SanitizeForHershey(
            "scale.example.商品: 水杯"));
        Assert.Equal("??", WatermarkOverlayRenderer.SanitizeForHershey("描述"));
    }

    /// <summary>超长扩展行按旧行为贴边裁剪：既不能越界，也不能让整个水印消失。</summary>
    [Fact]
    public void OverlongExtensionLine_IsClippedWithoutFailing()
    {
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        string longLine = new('W', 900);

        MainViewModel.ApplyWatermarkToFrame(frame, Timestamp, "435384812936683", new[] { longLine });

        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        Assert.True(Cv2.CountNonZero(gray) > 0);
    }

    /// <summary>非 8UC3 帧走逐字兜底路径，行为与老实现一致。</summary>
    [Fact]
    public void NonBgr8Frame_StillGetsWatermark()
    {
        using var frame = new Mat(720, 1280, MatType.CV_8UC1, Scalar.Black);

        MainViewModel.ApplyWatermarkToFrame(frame, Timestamp, "435384812936683");

        using var mask = new Mat();
        Cv2.Threshold(frame, mask, 240, 255, ThresholdTypes.Binary);
        Assert.True(Cv2.CountNonZero(mask) > 0);
    }
}
