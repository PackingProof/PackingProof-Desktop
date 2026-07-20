using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VideoLibraryTrackingTests
{
    [Fact]
    public void ResolveTrackingNumber_PrefersTrackingNumber()
    {
        string result = VideoLibraryPage.ResolveTrackingNumber("YT123", "ORDER456", "OTHER_20260720_120000_发货.mkv");

        Assert.Equal("YT123", result);
    }

    [Fact]
    public void ResolveTrackingNumber_FallsBackToOrderId()
    {
        string result = VideoLibraryPage.ResolveTrackingNumber("", "ORDER456", "OTHER_20260720_120000_发货.mkv");

        Assert.Equal("ORDER456", result);
    }

    [Theory]
    [InlineData("SF123456_20260720_120000_发货.mkv", "SF123456")]
    [InlineData("TRACK_WITH_UNDERSCORE_20260720_120000_退货.mp4", "TRACK_WITH_UNDERSCORE")]
    public void ResolveTrackingNumber_ParsesRecordingFileName(string fileName, string expected)
    {
        string result = VideoLibraryPage.ResolveTrackingNumber(null, null, fileName);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolveTrackingNumber_UsesUnknownLabelForUnrecognizedFileName()
    {
        string result = VideoLibraryPage.ResolveTrackingNumber(null, null, "video.mp4");

        Assert.Equal("未识别面单", result);
    }
}
