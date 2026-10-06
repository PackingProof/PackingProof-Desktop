using ExpressPackingMonitoring.ViewModels;
using OpenCvSharp;
using System;
using System.Threading;
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
    public void NonAsciiExtensionLine_IsDrawnAndOtherLinesSurvive()
    {
        using var frame = new Mat(2160, 3840, MatType.CV_8UC3, Scalar.Black);

        MainViewModel.ApplyWatermarkToFrame(
            frame,
            Timestamp,
            "435384812936683",
            new[] { "scale.example.商品: 水杯", "scale.example.weight: 1.25 kg" });

        // 含中文的行照画（真字体），后面的纯 ASCII 行也必须照画：按最后一行基线检查墨迹底边。
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

    /// <summary>
    /// 中文必须按原字渲染，不能像 Hershey 时代那样退化成 ?：两者画出来必须不是同一张图。
    /// </summary>
    [Fact]
    public void ChineseText_IsRenderedAsItselfNotAsQuestionMarks()
    {
        using Mat chinese = Render(1920, 1080, "435384812936683", "商品名称：蓝色水杯 3 件");
        using Mat questionMarks = Render(1920, 1080, "435384812936683", "??????????:?????? 3 ?");

        using var difference = new Mat();
        Cv2.Absdiff(chinese, questionMarks, difference);
        Assert.True(
            Cv2.CountNonZero(difference.Reshape(1)) > 0,
            "中文行被画成了 ? 占位符，说明真字体渲染没生效");
    }

    /// <summary>
    /// 秒数变化时整行不能左右挪：实测 60 秒（0~59）里墨迹左边界波动 0px，
    /// 也就是排版宽度不随数字变化（微软雅黑是等宽数字）。
    /// 右边界允许 ≤14px 的波动——那是最后一位数字的字形本身宽窄不同（"1" 比 "8" 窄），
    /// 三个方案（0.0.76 / 0.0.77 / 现在）都有，属于字形墨迹而不是排版漂移。
    /// </summary>
    [Fact]
    public void TimestampLine_DoesNotShiftWhenSecondsChange()
    {
        var bounds = new System.Collections.Generic.List<(int X, int Width)>();
        foreach ((int hour, int minute, int second) in new[]
                 {
                     (9, 10, 11),
                     (9, 10, 19),
                     (9, 10, 59),
                     (9, 11, 0)
                 })
        {
            using var frame = new Mat(2160, 3840, MatType.CV_8UC3, Scalar.Black);
            MainViewModel.ApplyWatermarkToFrame(
                frame,
                new DateTimeOffset(2026, 10, 20, hour, minute, second, TimeSpan.FromHours(8)),
                "435384812936683");
            Rect box = InkBounds(frame);
            bounds.Add((box.X, box.Width));
        }

        Assert.All(bounds, value => Assert.Equal(bounds[0].X, value.X));
        Assert.All(
            bounds,
            value => Assert.InRange(Math.Abs(value.Width - bounds[0].Width), 0, 14));
    }

    private static Rect InkBounds(Mat frame)
    {
        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        using var mask = new Mat();
        Cv2.Threshold(gray, mask, 128, 255, ThresholdTypes.Binary);
        using var points = new Mat();
        Cv2.FindNonZero(mask, points);
        return Cv2.BoundingRect(points);
    }

    /// <summary>
    /// 字号跟画面高度等比：4K 的字号必须是 720p 的 3 倍，分辨率变大不会让水印相对变小。
    /// （比 720p 还低的分辨率有下限保护，不参与这条线性关系。）
    /// </summary>
    [Theory]
    [InlineData(1440, 720, 2.0)]
    [InlineData(2160, 720, 3.0)]
    [InlineData(2160, 1080, 2.0)]
    public void FontSize_IsProportionalToFrameHeight(int tall, int shortHeight, double expectedRatio)
    {
        double tallSize = WatermarkOverlayRenderer.FontSizeOf(
            WatermarkOverlayRenderer.FontScaleOf(tall));
        double shortSize = WatermarkOverlayRenderer.FontSizeOf(
            WatermarkOverlayRenderer.FontScaleOf(shortHeight));

        Assert.Equal(expectedRatio, tallSize / shortSize, 2);
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

    /// <summary>
    /// 自然排版的前提：字体数字等宽。实测微软雅黑 0~9 的推进宽度完全一致，
    /// 所以时间戳整行宽度在秒数变化时不变，右对齐不会左右挪（数字不等宽时会退化成 0.0.76 那种漂移）。
    /// </summary>
    [Fact]
    public void FontDigits_AreEqualWidthSoTheLineWidthStaysStable()
    {
        const double fontScale = 1.8;
        double zero = WatermarkOverlayRenderer.MeasureCharacterAdvance('0', fontScale);
        Assert.True(zero > 0, "字体度量没量出来");
        foreach (char digit in "123456789")
        {
            Assert.Equal(zero, WatermarkOverlayRenderer.MeasureCharacterAdvance(digit, fontScale), 3);
        }

        double early = WatermarkOverlayRenderer.MeasureLineWidth(
            "UTC+08: 2026/10/20 09:10:11", fontScale);
        double late = WatermarkOverlayRenderer.MeasureLineWidth(
            "UTC+08: 2026/10/20 09:10:59", fontScale);
        Assert.Equal(early, late, 3);
    }

    /// <summary>
    /// 实测：同一个字体实例可以跨线程复用、两个线程并发渲染也安全（摄像头处理循环与
    /// 预录帧回填是两条线程池线程，会同时画水印）。渲染器因此只保留一个静态字体实例；
    /// 这条用例守住这个前提，换成有线程亲和的对象时会立刻失败。
    /// </summary>
    [Fact]
    public void WatermarkRendering_IsSafeFromMultipleThreadsConcurrently()
    {
        Exception? failure = null;
        var workers = new Thread[2];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = new Thread(() =>
            {
                try
                {
                    for (int frame = 0; frame < 12; frame++)
                    {
                        using var canvas = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
                        MainViewModel.ApplyWatermarkToFrame(
                            canvas,
                            Timestamp.AddSeconds(frame),
                            "435384812936683",
                            new[] { "scale.example.weight: 1.25 kg" });
                    }
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }
            });
            workers[i].SetApartmentState(ApartmentState.MTA);
            workers[i].Start();
        }

        foreach (Thread worker in workers)
            Assert.True(worker.Join(TimeSpan.FromSeconds(60)), "水印并发渲染线程超时");
        Assert.Null(failure);
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
