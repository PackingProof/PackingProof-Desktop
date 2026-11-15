using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Vision;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VisionObbCandidatePolicyTests
{
    private static VisionObbDetection Waybill(
        double x,
        double y,
        double width,
        double height,
        float score = 0.9f) =>
        new(x, y, width, height, 0, score, VisionObbClassIds.Waybill);

    private static VisionObbDetection Barcode(
        double x,
        double y,
        double width,
        double height,
        float score = 0.9f) =>
        new(x, y, width, height, 0, score, VisionObbClassIds.Barcode);

    [Fact]
    public void SelectBest_RequiresBarcodeInsideWaybill()
    {
        var waybill = Waybill(500, 400, 400, 300);
        var inside = Barcode(500, 400, 200, 100);
        var outside = Barcode(50, 50, 200, 100, 0.99f);

        VisionObbDetection? selected =
            VisionObbCandidatePolicy.SelectBestDecodeCandidate(
                [waybill, outside, inside],
                frameWidth: 1000,
                frameHeight: 800,
                qrCodeEnabled: false,
                hasBarcodeClassSupport: true);

        Assert.Equal(inside.CenterX, selected?.CenterX);
        Assert.Equal(inside.CenterY, selected?.CenterY);
    }

    [Fact]
    public void SelectBest_RejectsBarcodeBelowDiagonalThreshold()
    {
        var waybill = Waybill(500, 400, 400, 300);
        var tooSmall = Barcode(500, 400, 50, 50);

        VisionObbDetection? selected =
            VisionObbCandidatePolicy.SelectBestDecodeCandidate(
                [waybill, tooSmall],
                frameWidth: 1000,
                frameHeight: 800,
                qrCodeEnabled: false,
                hasBarcodeClassSupport: true);

        Assert.Null(selected);
    }

    [Fact]
    public void SelectBest_ReturnsOnlyOneCandidatePerFrame()
    {
        var waybill = Waybill(500, 400, 400, 300);
        var large = Barcode(480, 400, 220, 110, 0.80f);
        var small = Barcode(520, 400, 120, 60, 0.99f);

        VisionObbDetection? selected =
            VisionObbCandidatePolicy.SelectBestDecodeCandidate(
                [waybill, large, small],
                frameWidth: 1000,
                frameHeight: 800,
                qrCodeEnabled: false,
                hasBarcodeClassSupport: true);

        Assert.NotNull(selected);
        Assert.True(
            selected.Value.CenterX == large.CenterX
            || selected.Value.CenterX == small.CenterX);
    }

    [Fact]
    public void SelectBest_ReturnsTwoCandidatesForFallback()
    {
        var waybill = Waybill(500, 400, 400, 300);
        var first = Barcode(480, 400, 220, 110, 0.95f);
        var second = Barcode(560, 400, 180, 90, 0.80f);

        IReadOnlyList<VisionObbDetection> selected =
            VisionObbCandidatePolicy.SelectBestDecodeCandidates(
                [waybill, first, second],
                frameWidth: 1000,
                frameHeight: 800,
                qrCodeEnabled: false,
                hasBarcodeClassSupport: true,
                maxCandidates: 2);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void SelectBest_AllowsOneOccludedBarcodeCorner()
    {
        var waybill = Waybill(500, 400, 400, 300);
        var oneCornerOutside =
            new VisionObbDetection(
                650, 350, 150, 100, 45, 0.95f, VisionObbClassIds.Barcode);

        VisionObbDetection? selected =
            VisionObbCandidatePolicy.SelectBestDecodeCandidate(
                [waybill, oneCornerOutside],
                frameWidth: 1000,
                frameHeight: 800,
                qrCodeEnabled: false,
                hasBarcodeClassSupport: true);

        Assert.NotNull(selected);
        Assert.Equal(oneCornerOutside.CenterX, selected.Value.CenterX);
    }

    [Fact]
    public void SelectBest_RejectsBarcodeWithoutWaybill()
    {
        var barcode = Barcode(500, 400, 200, 100);

        Assert.Null(VisionObbCandidatePolicy.SelectBestDecodeCandidate(
            [barcode],
            frameWidth: 1000,
            frameHeight: 800,
            qrCodeEnabled: false,
            hasBarcodeClassSupport: true));
    }

    [Fact]
    public void SelectBest_LegacyModelStillPicksOneDetection()
    {
        var first = Waybill(500, 400, 400, 300, 0.80f);
        var second = Waybill(300, 300, 300, 220, 0.95f);

        VisionObbDetection? selected =
            VisionObbCandidatePolicy.SelectBestDecodeCandidate(
                [first, second],
                frameWidth: 1000,
                frameHeight: 800,
                qrCodeEnabled: false,
                hasBarcodeClassSupport: false);

        Assert.NotNull(selected);
        Assert.Equal(second.CenterX, selected.Value.CenterX);
    }
}
