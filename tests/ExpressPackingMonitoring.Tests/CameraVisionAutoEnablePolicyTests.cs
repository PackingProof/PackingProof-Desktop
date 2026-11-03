using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class CameraVisionAutoEnablePolicyTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public void BenchmarkRunsOnlyForMainCameraRecognition(
        int recognitionChannel,
        bool expected)
    {
        Assert.Equal(
            expected,
            CameraVisionAutoEnablePolicy.ShouldRunBenchmark(recognitionChannel));
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
