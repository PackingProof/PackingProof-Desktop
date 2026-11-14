using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Vision.Obb;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VisionObbDetectorContractTests
{
    [Theory]
    [InlineData(new[] { 1, 3, 640, 640 }, new[] { 1, 6, 8400 }, true)]
    [InlineData(new[] { -1, 3, 640, 640 }, new[] { 1, 8400, 6 }, true)]
    [InlineData(new[] { 1, 3, 320, 640 }, new[] { 1, 6, 8400 }, false)]
    [InlineData(new[] { 1, 3, 640, 640 }, new[] { 1, 5, 8400 }, false)]
    [InlineData(new[] { 1, 3, 640, 640 }, new[] { 1, 6 }, false)]
    public void ModelContractValidationRejectsIncompatibleShapes(
        int[] inputDimensions,
        int[] outputDimensions,
        bool expected)
    {
        Assert.Equal(
            expected,
            VisionObbDetector.TryResolveContract(
                inputDimensions,
                outputDimensions,
                out _,
                out _));
    }

    [Fact]
    public void YoloxOutputLayoutAcceptsChannelsLastTensor()
    {
        Assert.True(
            VisionObbDetector.TryResolveContract(
                [1, 3, 640, 640],
                [1, 8400, 7],
                "yolox_obb",
                out bool channelsFirst,
                out VisionObbOutputLayout outputLayout,
                out string error),
            error);

        Assert.False(channelsFirst);
        Assert.Equal(VisionObbOutputLayout.Yolox, outputLayout);
    }

    [Fact]
    public void UnknownOutputLayoutIsRejected()
    {
        Assert.False(VisionObbDetector.TryResolveContract(
            [1, 3, 640, 640],
            [1, 8400, 7],
            "unknown_obb",
            out _,
            out _,
            out string error));
        Assert.Contains("unknown_obb", error);
    }

    [Fact]
    public void RtmdetOutputLayoutAcceptsCombinedChannelsLastTensor()
    {
        Assert.True(
            VisionObbDetector.TryResolveContract(
                [1, 3, 640, 640],
                [1, 8400, 6],
                "rtmdet_obb",
                out bool channelsFirst,
                out VisionObbOutputLayout outputLayout,
                out string error),
            error);

        Assert.False(channelsFirst);
        Assert.Equal(VisionObbOutputLayout.Rtmdet, outputLayout);
    }

    [Theory]
    [InlineData(new[] { 1, 6, 8400 }, true, 0, 1)]
    [InlineData(new[] { 1, 7, 8400 }, true, 0, 2)]
    [InlineData(new[] { 1, 8, 8400 }, true, 1, 2)]
    public void DetectorClassCountFollowsTheOutputContract(
        int[] dimensions,
        bool channelsFirst,
        int outputLayoutValue,
        int expected)
    {
        Assert.Equal(
            expected,
            VisionObbDetector.ResolveClassCount(
                dimensions,
                channelsFirst,
                (VisionObbOutputLayout)outputLayoutValue));
    }
}
