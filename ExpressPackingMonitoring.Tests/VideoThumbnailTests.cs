using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VideoThumbnailTests
{
    [Theory]
    [InlineData(10, 5)]
    [InlineData(60, 30)]
    [InlineData(1, 0.5)]
    public void WebThumbnailUsesFrameAtFiftyPercent(double duration, double expected)
    {
        Assert.Equal(expected, VideoClipService.CalculateThumbnailSecond(duration), precision: 1);
    }
}
