using ExpressPackingMonitoring.ViewModels;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class CameraMotionDetectorTests
{
    [Fact]
    public void FirstFullResolutionFrameInitializesOnlyTheSmallReference()
    {
        using var detector = new CameraMotionDetector();
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);

        Assert.False(detector.Detect(frame, pixelThreshold: 20));
        Assert.Equal((320, 240), detector.PreviousFrameSize);
    }

    [Fact]
    public void UnchangedFramesDoNotReportMotion()
    {
        using var detector = new CameraMotionDetector();
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);

        Assert.False(detector.Detect(frame, pixelThreshold: 20));
        Assert.False(detector.Detect(frame, pixelThreshold: 20));
    }

    [Fact]
    public void VisibleFrameChangeReportsMotion()
    {
        using var detector = new CameraMotionDetector();
        using var first = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var second = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.White);

        Assert.False(detector.Detect(first, pixelThreshold: 20));
        Assert.True(detector.Detect(second, pixelThreshold: 20));
    }

    [Fact]
    public void ResetMakesTheNextFrameTheNewReference()
    {
        using var detector = new CameraMotionDetector();
        using var first = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var second = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.White);

        Assert.False(detector.Detect(first, pixelThreshold: 20));
        Assert.True(detector.Detect(second, pixelThreshold: 20));

        detector.Reset();

        Assert.Null(detector.PreviousFrameSize);
        Assert.False(detector.Detect(second, pixelThreshold: 20));
        Assert.Equal((320, 240), detector.PreviousFrameSize);
    }

    [Fact]
    public void DisposedDetectorRejectsFurtherFrames()
    {
        var detector = new CameraMotionDetector();
        detector.Dispose();
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);

        Assert.False(detector.Detect(frame, pixelThreshold: 20));
        Assert.Null(detector.PreviousFrameSize);
    }
}
