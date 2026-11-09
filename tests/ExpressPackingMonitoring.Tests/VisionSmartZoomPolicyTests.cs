using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Vision;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VisionSmartZoomPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void YoloRunsWhenEitherConsumerNeedsIt(
        bool enhancedRecognition,
        bool smartZoom,
        bool expected)
    {
        Assert.Equal(
            expected,
            VisionObbRuntimePolicy.ShouldRun(enhancedRecognition, smartZoom));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void SmartZoomNeedsMasterAndSmartFollowSwitches(
        bool masterEnabled,
        bool smartZoomEnabled,
        bool expected)
    {
        Assert.Equal(
            expected,
            VisionObbRuntimePolicy.ShouldRunSmartZoom(
                masterEnabled,
                smartZoomEnabled));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, true, false)]
    public void SecondaryBarcodeSourceUsesASeparateMainCameraSmartZoomPath(
        bool smartZoomEnabled,
        bool recognitionEnabled,
        bool overlaySource,
        bool expected)
    {
        Assert.Equal(
            expected,
            VisionObbRuntimePolicy.ShouldRunSeparateSmartZoom(
                smartZoomEnabled,
                recognitionEnabled,
                overlaySource));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, false, false)]
    public void MainBarcodeVisionIgnoresEnhancementAndSmartZoomOnSecondarySource(
        bool enhancedRecognition,
        bool smartZoom,
        bool mainSource,
        bool expected)
    {
        Assert.Equal(
            expected,
            VisionObbRuntimePolicy.ShouldRunMainBarcodeVision(
                enhancedRecognition,
                smartZoom,
                mainSource));
    }

    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(false, true, true, true, false)]
    public void GuideCornerModeRequiresAvailableModelAndBothFeatureSwitches(
        bool componentAvailable,
        bool recognitionEnabled,
        bool enhancedRecognitionEnabled,
        bool modelAvailable,
        bool expected)
    {
        Assert.Equal(
            expected,
            VisionObbRuntimePolicy.ShouldUseGuideCornerMode(
                componentAvailable,
                recognitionEnabled,
                enhancedRecognitionEnabled,
                modelAvailable));
    }

    [Fact]
    public void SelectTarget_PrefersDecodedLabelOverLargerCandidate()
    {
        var large = new VisionObbDetection(320, 240, 320, 180, 0, 0.99f);
        var decoded = new VisionObbDetection(120, 100, 80, 40, 15, 0.70f);

        VisionObbDetection? selected = VisionSmartZoomPolicy.SelectTarget(
            [large, decoded],
            decoded,
            frameWidth: 640,
            frameHeight: 480,
            previousTarget: large);

        Assert.Equal(decoded, selected);
    }

    [Fact]
    public void SelectTarget_RetainsNearbyPreviousCandidate()
    {
        var previous = new VisionObbDetection(320, 240, 200, 100, 0, 0.95f);
        var nearby = new VisionObbDetection(323, 242, 200, 100, 0, 0.94f);
        var larger = new VisionObbDetection(500, 240, 220, 110, 0, 0.99f);

        VisionObbDetection? selected = VisionSmartZoomPolicy.SelectTarget(
            [nearby, larger],
            decodedSelection: null,
            frameWidth: 640,
            frameHeight: 480,
            previousTarget: previous);

        Assert.Equal(nearby, selected);
    }

    [Fact]
    public void SelectTarget_UsesSameWeightedScoreWithoutDecodedCode()
    {
        var largeButUncertain = new VisionObbDetection(
            100, 100, 200, 100, 0, 0.45f);
        var smallButConfidentAndCentered = new VisionObbDetection(
            300, 220, 100, 50, 0, 0.95f);

        VisionObbDetection? selected = VisionSmartZoomPolicy.SelectTarget(
            [largeButUncertain, smallButConfidentAndCentered],
            decodedSelection: null,
            frameWidth: 640,
            frameHeight: 480);

        Assert.Equal(smallButConfidentAndCentered, selected);
    }

    [Fact]
    public void SelectTarget_NoDetectionsHidesSmartTarget()
    {
        Assert.Null(VisionSmartZoomPolicy.SelectTarget(
            [],
            decodedSelection: null,
            frameWidth: 640,
            frameHeight: 480));
    }

    [Fact]
    public void TargetRectKeepsFrameAspectAndEnclosesRotatedLabelWithPadding()
    {
        var selected = new VisionObbDetection(
            CenterX: 160,
            CenterY: 120,
            Width: 100,
            Height: 60,
            AngleDegrees: 24,
            Score: 0.92f);
        var other = new VisionObbDetection(
            CenterX: 420,
            CenterY: 260,
            Width: 180,
            Height: 100,
            AngleDegrees: 0,
            Score: 0.98f);

        var target = VisionSmartZoomPolicy.ResolveTargetRect(
            selected,
            frameWidth: 640,
            frameHeight: 480);
        var otherTarget = VisionSmartZoomPolicy.ResolveTargetRect(
            other,
            frameWidth: 640,
            frameHeight: 480);

        Assert.False(target.IsEmpty);
        Assert.True(target.Contains(selected.CenterX, selected.CenterY));
        Assert.Equal(640.0 / 480.0, target.Width / target.Height, 6);

        OpenCvSharp.Point2f[] corners = VisionObbGeometry.ToCorners(selected);
        double minX = corners.Min(point => point.X);
        double minY = corners.Min(point => point.Y);
        double maxX = corners.Max(point => point.X);
        double maxY = corners.Max(point => point.Y);
        double padding = Math.Max(
            VisionSmartZoomPolicy.MinPadding,
            Math.Min(maxX - minX, maxY - minY)
                * VisionSmartZoomPolicy.PaddingRatio);
        Assert.True(target.Left <= minX - padding);
        Assert.True(target.Top <= minY - padding);
        Assert.True(target.Right >= maxX + padding);
        Assert.True(target.Bottom >= maxY + padding);
        Assert.All(corners, corner => Assert.True(target.Contains(corner.X, corner.Y)));
        Assert.True(target.Right < otherTarget.Left);
    }

    [Fact]
    public void TargetRectClampsToFrameWithoutBecomingEmpty()
    {
        var detection = new VisionObbDetection(
            CenterX: 5,
            CenterY: 5,
            Width: 40,
            Height: 24,
            AngleDegrees: 0,
            Score: 0.90f);

        var target = VisionSmartZoomPolicy.ResolveTargetRect(
            detection,
            frameWidth: 320,
            frameHeight: 240);

        Assert.Equal(0, target.Left, 3);
        Assert.Equal(0, target.Top, 3);
        Assert.Equal(320.0 / 240.0, target.Width / target.Height, 6);
        Assert.True(target.Right <= 320);
        Assert.True(target.Bottom <= 240);
        Assert.True(target.Width > 0);
        Assert.True(target.Height > 0);
    }

    [Fact]
    public void TargetRect_IsExactlyTheCropThatGetsDisplayedAtFullSize()
    {
        var detection = new VisionObbDetection(320, 240, 180, 100, 32, 0.93f);

        System.Windows.Rect target = VisionSmartZoomPolicy.ResolveTargetRect(
            detection,
            frameWidth: 1280,
            frameHeight: 720);
        double scale = ZoomCropPolicy.ResolveScale(
            1280,
            720,
            target);
        System.Windows.Rect actualCrop = ZoomCropPolicy.CreateCropRect(
            1280,
            720,
            scale,
            target);

        Assert.Equal(target.X, actualCrop.X, 6);
        Assert.Equal(target.Y, actualCrop.Y, 6);
        Assert.Equal(target.Width, actualCrop.Width, 6);
        Assert.Equal(target.Height, actualCrop.Height, 6);
    }
}
