using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class CameraVisionAutoEnablePolicyTests
{
    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, false)]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, false)]
    public void BenchmarkRunsOnlyWhenMainCameraRecognitionIsEnabled(
        bool recognitionEnabled,
        int recognitionChannel,
        bool expected)
    {
        Assert.Equal(
            expected,
            CameraVisionAutoEnablePolicy.ShouldRunBenchmark(
                recognitionEnabled,
                recognitionChannel));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void AutoSourceUsesBenchmarkResult(
        bool recommended,
        bool expected)
    {
        Assert.Equal(
            expected,
            CameraVisionAutoEnablePolicy.ResolveEnableDecision(
                ExpressPackingMonitoring.Config.AppConfig.CameraVisionEnableSourceAuto,
                recommended).Enabled);
    }
}
