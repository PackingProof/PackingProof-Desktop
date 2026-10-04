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

    /// <summary>
    /// 现场反馈"降到 4 帧看着像卡住了、心里不踏实"，所以最低档抬到 10 帧、阈值拉到 10 分钟；
    /// 用户在设置里关掉空闲降帧后，隔多久都保持满帧。
    /// </summary>
    [Fact]
    public void IdleTiersKeepTheLowestStepAtTenFpsAndCanBeTurnedOff()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), PreviewFrameRatePolicy.ReducedAfter);
        Assert.Equal(TimeSpan.FromMinutes(10), PreviewFrameRatePolicy.LowAfter);
        Assert.Equal(15, PreviewFrameRatePolicy.ReducedFps);
        Assert.Equal(10, PreviewFrameRatePolicy.LowFps);

        Assert.Equal(
            PreviewFrameRatePolicy.ReducedFps,
            PreviewFrameRatePolicy.ResolveTargetFps(30, TimeSpan.FromMinutes(2)));
        Assert.Equal(
            PreviewFrameRatePolicy.LowFps,
            PreviewFrameRatePolicy.ResolveTargetFps(30, TimeSpan.FromMinutes(30)));
        Assert.Equal(
            30,
            PreviewFrameRatePolicy.ResolveTargetFps(
                30,
                TimeSpan.FromHours(5),
                idleThrottleEnabled: false));
    }
}
