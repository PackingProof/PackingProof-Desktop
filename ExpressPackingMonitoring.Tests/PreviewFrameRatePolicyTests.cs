using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class PreviewFrameRatePolicyTests
{
    [Theory]
    [InlineData(24, 24)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(120, 120)]
    [InlineData(240, 120)]
    [InlineData(0, PreviewFrameRatePolicy.FallbackCameraFps)]
    [InlineData(-1, PreviewFrameRatePolicy.FallbackCameraFps)]
    public void TargetFollowsCameraWithoutIdleOrFocusTiers(int sourceFps, int expected)
    {
        Assert.Equal(expected, PreviewFrameRatePolicy.ResolveTargetFps(sourceFps));
    }
}
