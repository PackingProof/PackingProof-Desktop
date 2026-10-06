using ExpressPackingMonitoring.ViewModels;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class WatermarkTimestampTests
{
    [Theory]
    [InlineData(8, 0, "UTC+08: 2026/07/12 09:10:11")]
    [InlineData(-4, 0, "UTC-04: 2026/07/12 09:10:11")]
    [InlineData(5, 30, "UTC+05:30: 2026/07/12 09:10:11")]
    [InlineData(0, 0, "UTC+00: 2026/07/12 09:10:11")]
    public void FormatWatermarkTimestamp_UsesTimestampOffset(int hours, int minutes, string expected)
    {
        var offset = new TimeSpan(hours, minutes, 0);
        var timestamp = new DateTimeOffset(2026, 7, 12, 9, 10, 11, offset);

        Assert.Equal(expected, MainViewModel.FormatWatermarkTimestamp(timestamp));
    }

    /// <summary>水印时区跟着这台电脑走：时间点不变，偏移换成机器时区，不是写死的 UTC+8。</summary>
    [Fact]
    public void ToWatermarkLocalTime_UsesTheMachineTimeZoneWithoutChangingTheInstant()
    {
        var utc = new DateTimeOffset(2026, 7, 12, 1, 10, 11, TimeSpan.Zero);

        DateTimeOffset local = MainViewModel.ToWatermarkLocalTime(utc);

        Assert.Equal(utc.UtcDateTime, local.UtcDateTime);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(local), local.Offset);
    }

    /// <summary>
    /// 水印跟着画面等比走：4K 上不能显得更小，720P 上也不能被压扁。
    /// 这里量白色墨迹包围盒的高度和右边距占画面的比例，任何分辨率都必须落在同一档。
    /// </summary>
    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(2560, 1440)]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    public void ApplyWatermarkToFrame_ScalesWithFrameResolution(int width, int height)
    {
        using var frame = new Mat(height, width, MatType.CV_8UC3, Scalar.Black);
        MainViewModel.ApplyWatermarkToFrame(
            frame,
            new DateTimeOffset(2026, 10, 20, 9, 10, 11, TimeSpan.FromHours(8)),
            "435384812936683");

        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        using var mask = new Mat();
        Cv2.Threshold(gray, mask, 240, 255, ThresholdTypes.Binary);
        using var points = new Mat();
        Cv2.FindNonZero(mask, points);
        Assert.False(points.Empty());

        Rect box = Cv2.BoundingRect(points);
        Assert.InRange(box.Height / (double)height, 0.064, 0.078);
        Assert.InRange((width - (box.X + box.Width)) / (double)width, 0.010, 0.020);
    }

    [Fact]
    public void ApplyWatermarkToFrame_DrawsOnAnOtherwiseBlankFirstFrame()
    {
        using var frame = new Mat(720, 1280, MatType.CV_8UC3, Scalar.Black);
        using var before = frame.Clone();
        var timestamp = new DateTimeOffset(2026, 7, 20, 9, 10, 11, TimeSpan.FromHours(8));

        MainViewModel.ApplyWatermarkToFrame(frame, timestamp, "TEST123");

        using var difference = new Mat();
        Cv2.Absdiff(before, frame, difference);
        Assert.True(Cv2.CountNonZero(difference.Reshape(1)) > 0);
    }

    [Fact]
    public void ApplyWatermarkToFrame_DrawsExtensionLines()
    {
        using var frame = new Mat(720, 1280, MatType.CV_8UC3, Scalar.Black);
        using var before = frame.Clone();

        MainViewModel.ApplyWatermarkToFrame(
            frame,
            new DateTimeOffset(2026, 7, 20, 9, 10, 11, TimeSpan.FromHours(8)),
            "TEST123",
            new[] { "scale.example.weight: 1.25 kg" });

        using var difference = new Mat();
        Cv2.Absdiff(before, frame, difference);
        Assert.True(Cv2.CountNonZero(difference.Reshape(1)) > 0);
    }
}
