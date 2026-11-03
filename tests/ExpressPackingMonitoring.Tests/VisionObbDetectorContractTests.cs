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
}
