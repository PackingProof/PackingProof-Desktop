using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class CameraBarcodeFrameIntentTests
{
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 2)]
    [InlineData(true, true, 3)]
    public void Resolve_UsesExplicitFrameIntent(
        bool forceBarcodeDecode,
        bool updateVision,
        int expected)
    {
        Assert.Equal(
            (CameraBarcodeFrameIntent)expected,
            CameraBarcodeFrameIntentPolicy.Resolve(
                forceBarcodeDecode,
                updateVision));
    }

    [Fact]
    public void VisionUpdateAloneNeverForcesBarcodeDecode()
    {
        CameraBarcodeFrameIntent intent =
            CameraBarcodeFrameIntent.VisionWithMotionGatedBarcode;

        Assert.True(CameraBarcodeFrameIntentPolicy.ShouldUpdateVision(intent));
        Assert.False(CameraBarcodeFrameIntentPolicy.ShouldForceDecode(intent));
    }
}
