using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Vision;
using OpenCvSharp;
using System.Diagnostics;
using Xunit;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Tests;

public sealed class VisionObbProductionPathTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 8)]
    [InlineData(45, 45)]
    [InlineData(80, 10)]
    [InlineData(90, 0)]
    [InlineData(-80, 10)]
    public void RightAngleResidualUsesNearestQuarterTurn(
        double angle,
        double expected)
    {
        Assert.Equal(
            expected,
            CameraBarcodeObbDecoder.GetRightAngleResidualDegrees(angle),
            6);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(80, true)]
    [InlineData(10.01, false)]
    [InlineData(45, false)]
    [InlineData(79, false)]
    public void DirectCropIsLimitedToTenDegreeResidual(
        double angle,
        bool expected)
    {
        var detection = new VisionObbDetection(
            100,
            100,
            80,
            40,
            angle,
            0.95f);

        Assert.Equal(
            expected,
            CameraBarcodeObbDecoder.ShouldUseDirectCrop(detection));
    }

    [Fact]
    public void NonFiniteAngleNeverUsesDirectCrop()
    {
        var detection = new VisionObbDetection(
            100,
            100,
            80,
            40,
            double.NaN,
            0.95f);

        Assert.False(CameraBarcodeObbDecoder.ShouldUseDirectCrop(detection));
    }

    [Fact]
    public void ProductionPostprocessingDecodesDetectedRegion()
    {
        using var barcode = CreateBarcodeMat("YT123456789012");
        using var frame = new Mat(
            new Size(960, 540),
            MatType.CV_8UC3,
            Scalar.White);
        int x = (frame.Width - barcode.Width) / 2;
        int y = (frame.Height - barcode.Height) / 2;
        using (Mat target = frame.SubMat(new Rect(x, y, barcode.Width, barcode.Height)))
            barcode.CopyTo(target);

        var detection = new VisionObbDetection(
            x + barcode.Width / 2.0,
            y + barcode.Height / 2.0,
            barcode.Width,
            barcode.Height,
            AngleDegrees: 0,
            Score: 0.95f);
        using var decoder = new CameraBarcodeFrameDecoder();
        using var obbDecoder = new CameraBarcodeObbDecoder(decoder);

        string? decoded = obbDecoder.DecodeDetectedRegions(
            frame,
            [detection],
            _ => true,
            useGuideRegionFallback: false);

        Assert.Equal("YT123456789012", decoded);
        Assert.Single(obbDecoder.LastObbDetections);
        Assert.Equal(detection, obbDecoder.LastSelectedObbDetection);
    }

    [Fact]
    public void CachedObbDetectionsCanDecodeTheNextFrameWithoutAnotherModelRun()
    {
        using var barcode = CreateBarcodeMat("YT123456789012");
        using var firstFrame = new Mat(
            new Size(960, 540),
            MatType.CV_8UC3,
            Scalar.White);
        using var nextFrame = firstFrame.Clone();
        int x = (firstFrame.Width - barcode.Width) / 2;
        int y = (firstFrame.Height - barcode.Height) / 2;
        using (Mat target = firstFrame.SubMat(
                   new Rect(x, y, barcode.Width, barcode.Height)))
        {
            barcode.CopyTo(target);
        }
        using (Mat target = nextFrame.SubMat(
                   new Rect(x, y, barcode.Width, barcode.Height)))
        {
            barcode.CopyTo(target);
        }

        var detection = new VisionObbDetection(
            x + barcode.Width / 2.0,
            y + barcode.Height / 2.0,
            barcode.Width,
            barcode.Height,
            AngleDegrees: 0,
            Score: 0.95f);
        using var decoder = new CameraBarcodeFrameDecoder();
        using var obbDecoder = new CameraBarcodeObbDecoder(decoder);
        Assert.Equal(
            "YT123456789012",
            obbDecoder.DecodeDetectedRegions(
                firstFrame,
                [detection],
                _ => true,
                useGuideRegionFallback: false));

        Assert.Equal(
            "YT123456789012",
            obbDecoder.DecodeCachedDetections(
                nextFrame,
                _ => true,
                geometry: default));
    }

    [Fact]
    public void SmallAngleDirectCropDecodesRotatedBarcode()
    {
        const double angle = 8;
        using var frame = CreateRotatedBarcodeFrame(angle);
        var detection = CreateDetection(angle);
        using Mat patch = CameraBarcodeObbDecoder.CreateAxisAlignedPatch(
            frame,
            detection);
        using var decoder = new CameraBarcodeFrameDecoder();

        Assert.Equal("YT123456789012", decoder.DecodeDetachedFast(patch));
    }

    [Fact]
    public void SmallAngleDirectCropDecodesQrCodeWhenEnabled()
    {
        using Mat qrCode = CreateQrCodeMat("YT123456789012");
        using var frame = new Mat(
            new Size(960, 540),
            MatType.CV_8UC3,
            Scalar.White);
        int x = (frame.Width - qrCode.Width) / 2;
        int y = (frame.Height - qrCode.Height) / 2;
        using (Mat target = frame.SubMat(
                   new Rect(x, y, qrCode.Width, qrCode.Height)))
        {
            qrCode.CopyTo(target);
        }

        var detection = new VisionObbDetection(
            x + qrCode.Width / 2.0,
            y + qrCode.Height / 2.0,
            qrCode.Width,
            qrCode.Height,
            AngleDegrees: 0,
            Score: 0.95f);
        using Mat patch = CameraBarcodeObbDecoder.CreateAxisAlignedPatch(
            frame,
            detection);
        using var decoder = new CameraBarcodeFrameDecoder(() => true);

        Assert.Equal("YT123456789012", decoder.DecodeDetachedFast(patch));
    }

    [Fact]
    public void FastDecoderReusesBuffersForStablePatchSize()
    {
        const double angle = 8;
        using var frame = CreateRotatedBarcodeFrame(angle);
        var detection = CreateDetection(angle);
        using Mat patch = CameraBarcodeObbDecoder.CreateAxisAlignedPatch(
            frame,
            detection);
        using var decoder = new CameraBarcodeFrameDecoder();

        Assert.Equal("YT123456789012", decoder.DecodeDetachedFast(patch));
        int allocationsAfterWarmup = decoder.PixelBufferAllocationCount;
        Assert.Equal("YT123456789012", decoder.DecodeDetachedFast(patch));

        Assert.Equal(allocationsAfterWarmup, decoder.PixelBufferAllocationCount);
    }

    [Fact]
    public void LargeAngleUprightCropDecodesRotatedBarcode()
    {
        const double angle = 20;
        using var frame = CreateRotatedBarcodeFrame(angle);
        var detection = CreateDetection(angle);
        using Mat patch = CameraBarcodeObbDecoder.CreateUprightPatch(
            frame,
            detection);
        using var decoder = new CameraBarcodeFrameDecoder();

        Assert.Equal("YT123456789012", decoder.DecodeDetached(patch, _ => true));
    }

    [Fact]
    public void FailedObbCandidatesDoNotTriggerWholeFrameScan()
    {
        using var frame = CreateRotatedBarcodeFrame(angleDegrees: 0);
        using var decoder = new CameraBarcodeFrameDecoder();
        using var obbDecoder = new CameraBarcodeObbDecoder(decoder);

        string? decoded = obbDecoder.DecodeDetectedRegions(
            frame,
            [],
            _ => true,
            useGuideRegionFallback: false);

        Assert.Null(decoded);
    }

    [Fact]
    public void UnavailableModelStillUsesGuideRegion()
    {
        using var frame = CreateRotatedBarcodeFrame(angleDegrees: 0);
        using var decoder = new CameraBarcodeFrameDecoder();
        using var obbDecoder = new CameraBarcodeObbDecoder(decoder);

        string? decoded = obbDecoder.DecodeGuideRegion(
            frame,
            _ => true,
            CameraBarcodeGuideGeometry.Default);

        Assert.Equal("YT123456789012", decoded);
    }

    [Fact]
    public void CachedDecodeFallsBackToGuideRegionWhenYoloFindsNoBoxes()
    {
        using var frame = CreateRotatedBarcodeFrame(angleDegrees: 0);
        using var decoder = new CameraBarcodeFrameDecoder();
        using var obbDecoder = new CameraBarcodeObbDecoder(decoder);

        string? decoded = obbDecoder.DecodeCachedDetections(
            frame,
            _ => true,
            CameraBarcodeGuideGeometry.Default);

        Assert.Equal("YT123456789012", decoded);
    }

    [Fact]
    public void DirectCropFallsBackToFullDecoderWhenFastPassRejectsBareWaybill()
    {
        using var barcode = CreateBarcodeMat("79023021221301");
        using var frame = new Mat(
            new Size(960, 540),
            MatType.CV_8UC3,
            Scalar.White);
        int x = (frame.Width - barcode.Width) / 2;
        int y = (frame.Height - barcode.Height) / 2;
        using (Mat target = frame.SubMat(
                   new Rect(x, y, barcode.Width, barcode.Height)))
        {
            barcode.CopyTo(target);
        }

        var detection = new VisionObbDetection(
            x + barcode.Width / 2.0,
            y + barcode.Height / 2.0,
            barcode.Width,
            barcode.Height,
            AngleDegrees: 0,
            Score: 0.95f);
        using var decoder = new CameraBarcodeFrameDecoder();
        using var obbDecoder = new CameraBarcodeObbDecoder(decoder);

        string? decoded = obbDecoder.DecodeDetectedRegions(
            frame,
            [detection],
            _ => true,
            useGuideRegionFallback: false);

        Assert.Equal("79023021221301", decoded);
    }

    [Fact]
    public void DecodeChannels_ReportMeasuredLatency()
    {
        using var barcode = CreateBarcodeMat("79023021221301");
        using var frame = new Mat(
            new Size(960, 540),
            MatType.CV_8UC3,
            Scalar.White);
        int x = (frame.Width - barcode.Width) / 2;
        int y = (frame.Height - barcode.Height) / 2;
        using (Mat target = frame.SubMat(
                   new Rect(x, y, barcode.Width, barcode.Height)))
        {
            barcode.CopyTo(target);
        }

        var detection = new VisionObbDetection(
            x + barcode.Width / 2.0,
            y + barcode.Height / 2.0,
            barcode.Width,
            barcode.Height,
            AngleDegrees: 0,
            Score: 0.95f);
        using Mat patch = CameraBarcodeObbDecoder.CreateAxisAlignedPatch(
            frame,
            detection);
        using var blankPatch = new Mat(
            new Size(
                Math.Max(1, patch.Width),
                Math.Max(1, patch.Height)),
            MatType.CV_8UC3,
            Scalar.White);
        using var decoder = new CameraBarcodeFrameDecoder();

        for (int index = 0; index < 3; index++)
        {
            decoder.DecodeDetachedFast(patch);
            decoder.DecodeDetached(patch, _ => true);
            decoder.DecodeDetachedFast(blankPatch);
            decoder.DecodeDetached(blankPatch, _ => true);
            decoder.DecodeGuideRegion(
                frame,
                _ => true,
                CameraBarcodeGuideGeometry.Default);
        }

        (double fastMedian, double fastP95) = MeasureDecode(
            () => decoder.DecodeDetachedFast(patch));
        (double fullMedian, double fullP95) = MeasureDecode(
            () => decoder.DecodeDetached(patch, _ => true));
        (double guideMedian, double guideP95) = MeasureDecode(
            () => decoder.DecodeGuideRegion(
                frame,
                _ => true,
                CameraBarcodeGuideGeometry.Default));
        (double fastMissMedian, double fastMissP95) = MeasureDecode(
            () => decoder.DecodeDetachedFast(blankPatch));
        (double fullMissMedian, double fullMissP95) = MeasureDecode(
            () => decoder.DecodeDetached(blankPatch, _ => true));

        output.WriteLine(
            $"fast median={fastMedian:F2}ms p95={fastP95:F2}ms; "
            + $"full median={fullMedian:F2}ms p95={fullP95:F2}ms; "
            + $"guide median={guideMedian:F2}ms p95={guideP95:F2}ms; "
            + $"fast-miss median={fastMissMedian:F2}ms p95={fastMissP95:F2}ms; "
            + $"full-miss median={fullMissMedian:F2}ms p95={fullMissP95:F2}ms");
        Assert.True(fastMedian > 0);
        Assert.True(fullMedian > 0);
        Assert.True(guideMedian > 0);
        Assert.True(fastMissMedian > 0);
        Assert.True(fullMissMedian > 0);
    }

    private static VisionObbDetection CreateDetection(double angleDegrees) =>
        new(
            480,
            270,
            520,
            120,
            -angleDegrees,
            0.95f);

    private static (double MedianMs, double P95Ms) MeasureDecode(Action action)
    {
        const int iterations = 20;
        var samples = new double[iterations];
        for (int index = 0; index < iterations; index++)
        {
            long started = Stopwatch.GetTimestamp();
            action();
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(samples);
        double median = samples[samples.Length / 2];
        int p95Index = Math.Clamp(
            (int)Math.Ceiling(samples.Length * 0.95) - 1,
            0,
            samples.Length - 1);
        return (median, samples[p95Index]);
    }

    private static Mat CreateRotatedBarcodeFrame(double angleDegrees)
    {
        using Mat barcode = CreateBarcodeMat("YT123456789012");
        var baseFrame = new Mat(
            new Size(960, 540),
            MatType.CV_8UC3,
            Scalar.White);
        try
        {
            int x = (baseFrame.Width - barcode.Width) / 2;
            int y = (baseFrame.Height - barcode.Height) / 2;
            using (Mat target = baseFrame.SubMat(
                       new Rect(x, y, barcode.Width, barcode.Height)))
            {
                barcode.CopyTo(target);
            }

            if (Math.Abs(angleDegrees) < 0.001)
                return baseFrame.Clone();

            using Mat matrix = Cv2.GetRotationMatrix2D(
                new Point2f(baseFrame.Width / 2f, baseFrame.Height / 2f),
                angleDegrees,
                1);
            var rotated = new Mat();
            Cv2.WarpAffine(
                baseFrame,
                rotated,
                matrix,
                baseFrame.Size(),
                InterpolationFlags.Linear,
                BorderTypes.Constant,
                Scalar.White);
            return rotated;
        }
        finally
        {
            baseFrame.Dispose();
        }
    }

    private static Mat CreateBarcodeMat(string value)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions
            {
                Width = 520,
                Height = 120,
                Margin = 16,
                PureBarcode = true
            }
        };
        var pixels = writer.Write(value);
        using Mat bgra = Mat.FromPixelData(
            pixels.Height,
            pixels.Width,
            MatType.CV_8UC4,
            pixels.Pixels).Clone();
        var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr;
    }

    private static Mat CreateQrCodeMat(string value)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions
            {
                Width = 220,
                Height = 220,
                Margin = 3
            }
        };
        var pixels = writer.Write(value);
        using Mat bgra = Mat.FromPixelData(
            pixels.Height,
            pixels.Width,
            MatType.CV_8UC4,
            pixels.Pixels).Clone();
        var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr;
    }
}
