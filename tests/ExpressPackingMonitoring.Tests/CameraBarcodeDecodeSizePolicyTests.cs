using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class CameraBarcodeDecodeSizePolicyTests
{
    [Theory]
    [InlineData(240, 64, true)]
    [InlineData(64, 240, true)]
    [InlineData(239, 80, true)]
    [InlineData(300, 63, true)]
    [InlineData(0, 100, false)]
    public void OneDimensionalModeUsesDiagonalPixelSpan(
        int width,
        int height,
        bool expected)
    {
        Assert.Equal(
            expected,
            CameraBarcodeDecodeSizePolicy.ShouldDecode(
                width,
                height,
                qrCodeEnabled: false));
    }

    [Theory]
    [InlineData(120, 80, true)]
    [InlineData(80, 120, true)]
    [InlineData(119, 80, true)]
    [InlineData(120, 79, true)]
    [InlineData(64, 64, false)]
    [InlineData(240, 64, true)]
    public void QrCodeModeUsesDiagonalPixelSpan(
        int width,
        int height,
        bool expected)
    {
        Assert.Equal(
            expected,
            CameraBarcodeDecodeSizePolicy.ShouldDecode(
                width,
                height,
                qrCodeEnabled: true));
    }

    [Theory]
    [InlineData(300, 63, true)]
    [InlineData(64, 240, true)]
    [InlineData(70, 20, false)]
    public void ThinButLongCropIsNotRejectedByShortEdge(
        int width,
        int height,
        bool expected)
    {
        Assert.Equal(
            expected,
            CameraBarcodeDecodeSizePolicy.ShouldDecode(
                width,
                height,
                qrCodeEnabled: false));
    }

    [Theory]
    [InlineData(100, 100, 1920, 1080, false, true)]
    [InlineData(100, 100, 3840, 2160, false, false)]
    [InlineData(200, 100, 3840, 2160, false, true)]
    [InlineData(60, 60, 1280, 720, false, false)]
    [InlineData(120, 120, 1920, 1080, true, true)]
    [InlineData(120, 120, 3840, 2160, true, false)]
    public void ProductionPolicyAlsoChecksFrameDiagonalPercentage(
        int width,
        int height,
        int frameWidth,
        int frameHeight,
        bool qrCodeEnabled,
        bool expected)
    {
        Assert.Equal(
            expected,
            CameraBarcodeDecodeSizePolicy.ShouldDecode(
                width,
                height,
                frameWidth,
                frameHeight,
                qrCodeEnabled));
    }

    [Fact]
    public void FrameDiagonalRatioUsesTheWholeFrame()
    {
        double ratio = CameraBarcodeDecodeSizePolicy.FrameDiagonalRatio(
            192,
            108,
            1920,
            1080);

        Assert.Equal(0.1, ratio, 6);
    }
}
