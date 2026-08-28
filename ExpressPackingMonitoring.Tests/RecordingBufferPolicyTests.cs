using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class RecordingBufferPolicyTests
{
    [Theory]
    [InlineData(1920, 1080, 60, 32)]
    [InlineData(1280, 720, 15, 15)]
    [InlineData(3840, 2160, 60, 8)]
    [InlineData(640, 480, 120, 60)]
    public void CalculateVideoQueueCapacity_RespectsMemoryTimeAndFrameLimits(
        int width,
        int height,
        int fps,
        int expectedCapacity)
    {
        Assert.Equal(expectedCapacity, RecordingBufferPolicy.CalculateVideoQueueCapacity(width, height, fps));
    }

    [Fact]
    public void CalculateVideoQueueCapacity_InvalidFpsUsesFallback()
    {
        Assert.Equal(15, RecordingBufferPolicy.CalculateVideoQueueCapacity(1280, 720, 0));
    }

    [Theory]
    [InlineData(21, 30, 0.7)]
    [InlineData(21, 8, 2.625)]
    [InlineData(0, 30, 0)]
    [InlineData(21, 0, 0)]
    public void CalculatePreRecordBufferedSeconds_UsesConfiguredFps(
        int frameCount,
        int configuredFps,
        double expectedSeconds)
    {
        Assert.Equal(
            expectedSeconds,
            MainViewModel.CalculatePreRecordBufferedSeconds(frameCount, configuredFps),
            precision: 3);
    }

    [Theory]
    [InlineData(512, 8, 512)]
    [InlineData(1024, 8, 512)]
    [InlineData(1728, 32, 1728)]
    [InlineData(2048, 32, 2048)]
    [InlineData(-1, 16, 0)]
    public void CalculatePreRecordBufferMaxBytes_UsesConfiguredMemoryWithoutFpsLimit(
        int configuredMb,
        int physicalMemoryGb,
        int expectedMb)
    {
        long bytes = MainViewModel.CalculatePreRecordBufferMaxBytes(
            configuredMb,
            (ulong)physicalMemoryGb * 1024 * 1024 * 1024);

        Assert.Equal((long)expectedMb * 1024 * 1024, bytes);
    }
}
