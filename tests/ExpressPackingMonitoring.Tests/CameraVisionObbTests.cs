using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Vision;
using ExpressPackingMonitoring.Vision.Obb;
using ExpressPackingMonitoring.UI;
using OpenCvSharp;
using System.Diagnostics;
using Xunit;
using WpfPoint = System.Windows.Point;

namespace ExpressPackingMonitoring.Tests;

public sealed class VisionObbDetectorTests(ITestOutputHelper output)
{
    [Fact]
    public void FillInputConvertsBgrToPlanarRgb()
    {
        using var canvas = new Mat(
            VisionObbDetector.InputSize,
            VisionObbDetector.InputSize,
            MatType.CV_8UC3,
            new Scalar(114, 114, 114));
        Cv2.Rectangle(canvas, new Rect(0, 0, 1, 1), new Scalar(10, 20, 30), -1);
        var values = new float[3 * VisionObbDetector.InputSize * VisionObbDetector.InputSize];

        Assert.True(VisionObbDetector.FillInput(canvas, values));

        int plane = VisionObbDetector.InputSize * VisionObbDetector.InputSize;
        Assert.Equal(30f / 255f, values[0], 6);
        Assert.Equal(20f / 255f, values[plane], 6);
        Assert.Equal(10f / 255f, values[2 * plane], 6);
    }

    [Fact]
    public void FillInputRejectsWrongImageOrBufferShape()
    {
        using var wrongImage = new Mat(
            VisionObbDetector.InputSize - 1,
            VisionObbDetector.InputSize,
            MatType.CV_8UC3);
        using var correctImage = new Mat(
            VisionObbDetector.InputSize,
            VisionObbDetector.InputSize,
            MatType.CV_8UC3);

        Assert.False(VisionObbDetector.FillInput(
            wrongImage,
            new float[3 * VisionObbDetector.InputSize * VisionObbDetector.InputSize]));
        Assert.False(VisionObbDetector.FillInput(correctImage, new float[3]));
    }

    [Fact]
    public void FillInputCanPreserveYoloxRawScale()
    {
        using var canvas = new Mat(
            VisionObbDetector.InputSize,
            VisionObbDetector.InputSize,
            MatType.CV_8UC3,
            new Scalar(114, 114, 114));
        Cv2.Rectangle(canvas, new Rect(0, 0, 1, 1), new Scalar(10, 20, 30), -1);
        var values = new float[3 * VisionObbDetector.InputSize * VisionObbDetector.InputSize];

        Assert.True(VisionObbDetector.FillInput(
            canvas,
            values,
            normalize: false));

        int plane = VisionObbDetector.InputSize * VisionObbDetector.InputSize;
        Assert.Equal(30f, values[0], 6);
        Assert.Equal(20f, values[plane], 6);
        Assert.Equal(10f, values[2 * plane], 6);
    }

    [Fact]
    public void FillRtmdetInputUsesBgrMeanAndStandardDeviation()
    {
        using var canvas = new Mat(
            VisionObbDetector.InputSize,
            VisionObbDetector.InputSize,
            MatType.CV_8UC3,
            new Scalar(114, 114, 114));
        Cv2.Rectangle(canvas, new Rect(0, 0, 1, 1), new Scalar(10, 20, 30), -1);
        var values = new float[3 * VisionObbDetector.InputSize * VisionObbDetector.InputSize];

        Assert.True(VisionObbDetector.FillRtmdetInput(canvas, values));

        int plane = VisionObbDetector.InputSize * VisionObbDetector.InputSize;
        Assert.Equal((10f - 103.53f) / 57.375f, values[0], 6);
        Assert.Equal((20f - 116.28f) / 57.12f, values[plane], 6);
        Assert.Equal((30f - 123.675f) / 58.395f, values[2 * plane], 6);
    }

    [Fact]
    public void ParseOutputReadsUltralyticsChannelsFirstTensor()
    {
        float[] values =
        [
            320, 100,
            240, 100,
            120, 40,
            60, 20,
            0.80f, 0.10f,
            0.20f, -0.10f
        ];

        List<VisionObbDetection> detections = VisionObbDetector.ParseOutput(
            values,
            [1, 6, 2],
            channelsFirst: true);

        VisionObbDetection detection = Assert.Single(detections);
        Assert.Equal(320, detection.CenterX);
        Assert.Equal(240, detection.CenterY);
        Assert.Equal(120, detection.Width);
        Assert.Equal(60, detection.Height);
        Assert.InRange(detection.AngleDegrees, 11.45, 11.46);
        Assert.Equal(0.80f, detection.Score);
    }

    [Fact]
    public void ParseOutputReadsChannelsLastTensor()
    {
        float[] values =
        [
            320, 240, 120, 60, 0.90f, 0,
            100, 100, 40, 20, 0.10f, 0
        ];

        List<VisionObbDetection> detections = VisionObbDetector.ParseOutput(
            values,
            [1, 2, 6],
            channelsFirst: false);

        VisionObbDetection detection = Assert.Single(detections);
        Assert.Equal(320, detection.CenterX);
        Assert.Equal(0.90f, detection.Score);
    }

    [Fact]
    public void ParseOutputPreservesMultiClassId()
    {
        float[] values =
        [
            320,
            240,
            120,
            60,
            0.10f,
            0.80f,
            0.20f
        ];

        VisionObbDetection detection = Assert.Single(
            VisionObbDetector.ParseOutput(values, [1, 7, 1], channelsFirst: true));

        Assert.Equal(1, detection.ClassId);
        Assert.Equal(0.80f, detection.Score);
    }

    [Fact]
    public void ParseOutputReadsYoloxObjectnessAndClassConfidence()
    {
        float[] values =
        [
            320,
            240,
            120,
            60,
            MathF.PI / 6,
            0.80f,
            0.75f
        ];

        VisionObbDetection detection = Assert.Single(
            VisionObbDetector.ParseOutput(
                values,
                [1, 1, 7],
                channelsFirst: false,
                VisionObbOutputLayout.Yolox));

        Assert.Equal(320, detection.CenterX);
        Assert.Equal(240, detection.CenterY);
        Assert.Equal(120, detection.Width);
        Assert.Equal(60, detection.Height);
        Assert.Equal(-30, detection.AngleDegrees, 3);
        Assert.Equal(0.60f, detection.Score, 6);
    }

    [Fact]
    public void ParseOutputUsesModelDeclaredScoreThreshold()
    {
        float[] values =
        [
            320,
            240,
            120,
            60,
            MathF.PI / 6,
            0.70f,
            0.60f
        ];

        Assert.Empty(
            VisionObbDetector.ParseOutput(
                values,
                [1, 1, 7],
                channelsFirst: false,
                VisionObbOutputLayout.Yolox,
                scoreThreshold: 0.50f));
    }

    [Fact]
    public void ParseOutputReadsCombinedRtmdetTensor()
    {
        float[] values =
        [
            320,
            240,
            120,
            60,
            MathF.PI / 6,
            0.75f
        ];

        VisionObbDetection detection = Assert.Single(
            VisionObbDetector.ParseOutput(
                values,
                [1, 1, 6],
                channelsFirst: false,
                VisionObbOutputLayout.Rtmdet));

        Assert.Equal(30, detection.AngleDegrees, 3);
        Assert.Equal(0.75f, detection.Score, 6);
    }

    [Fact]
    public void SuppressOverlapsKeepsHighestScoringDuplicate()
    {
        var high = new VisionObbDetection(100, 100, 80, 40, 0, 0.90f);
        var overlap = new VisionObbDetection(104, 100, 80, 40, 0, 0.50f);
        var separate = new VisionObbDetection(400, 400, 80, 40, 30, 0.70f);

        List<VisionObbDetection> kept = VisionObbDetector.SuppressOverlaps(
            [overlap, high, separate],
            VisionObbDetector.NmsThreshold);

        Assert.Equal(2, kept.Count);
        Assert.Equal(0.90f, kept[0].Score);
        Assert.Equal(400, kept[1].CenterX, 1);
    }

    [Fact]
    public void SuppressOverlapsRemovesContainedPartialDetection()
    {
        var wholeLabel = new VisionObbDetection(100, 100, 200, 100, 0, 0.90f);
        var containedPart = new VisionObbDetection(100, 100, 100, 40, 0, 0.95f);

        List<VisionObbDetection> kept = VisionObbDetector.SuppressOverlaps(
            [containedPart, wholeLabel],
            VisionObbDetector.NmsThreshold);

        VisionObbDetection detection = Assert.Single(kept);
        Assert.Equal(200, detection.Width);
    }

    [Fact]
    public void SuppressOverlapsKeepsCrossingRotatedLabelsWithSimilarBounds()
    {
        var diagonal = new VisionObbDetection(100, 100, 200, 20, 45, 0.90f);
        var oppositeDiagonal = new VisionObbDetection(100, 100, 200, 20, 135, 0.90f);

        List<VisionObbDetection> kept = VisionObbDetector.SuppressOverlaps(
            [diagonal, oppositeDiagonal],
            VisionObbDetector.NmsThreshold);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void SuppressOverlapsUsesRotatedOverlapForDuplicates()
    {
        var high = new VisionObbDetection(100, 100, 120, 50, 30, 0.90f);
        var duplicate = new VisionObbDetection(104, 102, 120, 50, 31, 0.60f);

        VisionObbDetection detection = Assert.Single(
            VisionObbDetector.SuppressOverlaps(
                [duplicate, high],
                VisionObbDetector.NmsThreshold));

        Assert.Equal(0.90f, detection.Score);
    }

    [Fact]
    public void UprightPatchUsesRotatedFourPointBounds()
    {
        using var frame = new Mat(
            new Size(240, 240),
            MatType.CV_8UC3,
            Scalar.Black);
        var detection = new VisionObbDetection(120, 120, 100, 30, 25, 0.95f);
        using Mat patch = CameraBarcodeObbDecoder.CreateUprightPatch(frame, detection);

        Assert.InRange(patch.Width, 108, 116);
        Assert.InRange(patch.Height, 38, 46);
        Assert.False(patch.Empty());
    }

    [Fact]
    public void DisplayPointsUseVideoOriginAndUniformScale()
    {
        var detection = new VisionObbDetection(100, 50, 20, 10, 0, 0.95f);

        WpfPoint[] points = MainWindow.BuildObbDisplayPoints(
            detection,
            new WpfPoint(10, 20),
            2);

        Assert.Equal(4, points.Length);
        Assert.Equal(new WpfPoint(190, 110), points[0]);
        Assert.Equal(new WpfPoint(230, 110), points[1]);
        Assert.Equal(new WpfPoint(230, 130), points[2]);
        Assert.Equal(new WpfPoint(190, 130), points[3]);
    }

    [Fact]
    public void RoundedRectangleFrameUsesRotatedObbDimensions()
    {
        MainWindow.VisionObbRectangleFrame frame =
            MainWindow.ResolveRectangleFrame(
            [
                new WpfPoint(10, 10),
                new WpfPoint(110, 10),
                new WpfPoint(110, 50),
                new WpfPoint(10, 50)
            ]);

        Assert.Equal(100, frame.Width, 3);
        Assert.Equal(40, frame.Height, 3);
        Assert.Equal(0, frame.Angle, 3);
        Assert.Equal(new WpfPoint(10, 10), frame.TopLeft);
        Assert.Equal(6, frame.CornerRadius, 3);
    }

    [Fact]
    public void ObbAnimationUsesCubicEaseInOutAndInterpolatesCorners()
    {
        Assert.Equal(0, MainWindow.EaseInOutCubic(0), 3);
        Assert.Equal(0.0625, MainWindow.EaseInOutCubic(0.25), 3);
        Assert.Equal(0.5, MainWindow.EaseInOutCubic(0.5), 3);
        Assert.Equal(0.9375, MainWindow.EaseInOutCubic(0.75), 3);
        Assert.Equal(1, MainWindow.EaseInOutCubic(1), 3);

        WpfPoint[] points = MainWindow.InterpolatePoints(
            [new WpfPoint(0, 0), new WpfPoint(10, 0)],
            [new WpfPoint(10, 20), new WpfPoint(30, 20)],
            0.5);

        Assert.Equal(new WpfPoint(5, 10), points[0]);
        Assert.Equal(new WpfPoint(20, 10), points[1]);
    }

    [Theory]
    [InlineData(16, 80)]
    [InlineData(100, 100)]
    [InlineData(250, 240)]
    [InlineData(800, 240)]
    public void ObbAnimationDurationFollowsDetectionCadence(
        double targetIntervalMs,
        double expectedDurationMs)
    {
        Assert.Equal(
            expectedDurationMs,
            MainWindow.ResolveObbAnimationDurationMs(targetIntervalMs),
            1);
    }

    [Theory]
    [InlineData(false, false, VisionObbVisualState.Idle)]
    [InlineData(true, false, VisionObbVisualState.Candidate)]
    [InlineData(true, true, VisionObbVisualState.Confirmed)]
    [InlineData(false, true, VisionObbVisualState.Confirmed)]
    public void ObbColorsFollowDetectionCandidateAndConfirmedPriority(
        bool candidate,
        bool confirmed,
        VisionObbVisualState expected)
    {
        Assert.Equal(
            expected,
            MainWindow.ResolveVisionObbVisualState(candidate, confirmed));
    }

    [Theory]
    [InlineData(false, false, false, VisionObbVisualState.Idle)]
    [InlineData(true, false, false, VisionObbVisualState.Idle)]
    [InlineData(false, true, false, VisionObbVisualState.Idle)]
    [InlineData(true, false, true, VisionObbVisualState.Candidate)]
    [InlineData(true, true, true, VisionObbVisualState.Confirmed)]
    public void OnlySelectedObbTrackShowsCandidateOrConfirmedColor(
        bool candidate,
        bool confirmed,
        bool selected,
        VisionObbVisualState expected)
    {
        Assert.Equal(
            expected,
            MainWindow.ResolveVisionObbBoxVisualState(
                candidate,
                confirmed,
                selected));
    }

    [Fact]
    public void WeightedScoreBalancesAreaConfidenceAndCenter()
    {
        var guide = new Rect(0, 0, 400, 200);
        var largeButUncertain = new VisionObbDetection(
            100, 100, 200, 100, 0, 0.45f);
        var smallButConfidentAndCentered = new VisionObbDetection(
            200, 100, 100, 50, 0, 0.95f);
        double largeScore = CameraBarcodeObbDecoder.CalculateScore(
            largeButUncertain,
            guide,
            maxArea: 20_000);
        double smallScore = CameraBarcodeObbDecoder.CalculateScore(
            smallButConfidentAndCentered,
            guide,
            maxArea: 20_000);

        Assert.True(smallScore > largeScore);
        ObbBarcodeSelection selection = CameraBarcodeObbDecoder.SelectCandidate(
        [
            new DecodedObbBarcodeCandidate(
                "JD000000000002",
                largeButUncertain),
            new DecodedObbBarcodeCandidate(
                "JD000000000001",
                smallButConfidentAndCentered)
        ],
        guide);

        Assert.Equal("JD000000000001", selection.Code);
        Assert.Equal(2, selection.CandidateCount);
        Assert.NotNull(selection.Detection);
        Assert.Equal(200, selection.Detection.Value.CenterX, 1);
    }

    [Fact]
    public void SameOrderUsesHighestScoringPackageCandidate()
    {
        ObbBarcodeSelection selection = CameraBarcodeObbDecoder.SelectCandidate(
        [
            new DecodedObbBarcodeCandidate(
                "JD000000000001",
                new VisionObbDetection(100, 100, 80, 40, 0, 0.70f)),
            new DecodedObbBarcodeCandidate(
                "JD000000000001-1-2-",
                new VisionObbDetection(190, 100, 120, 60, 0, 0.95f))
        ],
        new Rect(0, 0, 400, 200));

        Assert.Equal("JD000000000001-1-2-", selection.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Z:\definitely-not-here\model.onnx")]
    public void MissingModelDegradesToNull(string? path)
    {
        Assert.Null(VisionObbDetector.TryLoad(path));
    }

    [Fact]
    public void RealModelLoadsAndRunsWhenManualModelPathIsProvided()
    {
        string? modelPath = Environment.GetEnvironmentVariable(
            "PACKINGPROOF_WAYBILL_OBB_MODEL");
        if (string.IsNullOrWhiteSpace(modelPath))
            return;

        using VisionObbDetector? detector = VisionObbDetector.TryLoad(modelPath);
        Assert.NotNull(detector);
        Assert.Contains(detector.ExecutionProvider, new[] { "DirectML", "CPU" });
        string? expectedProvider = Environment.GetEnvironmentVariable(
            "PACKINGPROOF_WAYBILL_OBB_PROVIDER");
        if (!string.IsNullOrWhiteSpace(expectedProvider))
            Assert.Equal(expectedProvider, detector.ExecutionProvider);

        using var frame = new Mat(
            new Size(1280, 720),
            MatType.CV_8UC3,
            Scalar.White);
        IReadOnlyList<VisionObbDetection> detections = detector.Detect(frame);

        Assert.NotNull(detections);

        if (string.Equals(
                Environment.GetEnvironmentVariable("PACKINGPROOF_BENCHMARK_OBB"),
                "1",
                StringComparison.Ordinal))
        {
            int benchmarkWidth = int.TryParse(
                Environment.GetEnvironmentVariable("PACKINGPROOF_BENCHMARK_OBB_WIDTH"),
                out int configuredWidth)
                ? configuredWidth
                : 1280;
            int benchmarkHeight = int.TryParse(
                Environment.GetEnvironmentVariable("PACKINGPROOF_BENCHMARK_OBB_HEIGHT"),
                out int configuredHeight)
                ? configuredHeight
                : 720;
            VisionObbBenchmarkResult benchmark =
                VisionObbPerformanceBenchmark.Run(
                    modelPath,
                    preferDirectMl: true,
                    frameWidth: benchmarkWidth,
                    frameHeight: benchmarkHeight);
            Assert.True(benchmark.ModelAvailable);
            output.WriteLine(
                $"OBB benchmark: resolution={benchmarkWidth}x{benchmarkHeight}, "
                + $"provider={benchmark.Provider}, "
                + $"baselineP95={benchmark.BaselineP95Ms:F2}ms, "
                + $"enhancedP95={benchmark.EnhancedP95Ms:F2}ms, "
                + $"addedP95={benchmark.AddedP95Ms:F2}ms, "
                + $"baselineCpu={benchmark.BaselineCpuMsPerFrame:F2}ms/frame, "
                + $"enhancedCpu={benchmark.EnhancedCpuMsPerFrame:F2}ms/frame, "
                + $"addedCpu={benchmark.AddedCpuMsPerFrame:F2}ms/frame, "
                + $"recommended={benchmark.Recommended}");

            if (string.Equals(
                    Environment.GetEnvironmentVariable("PACKINGPROOF_BENCHMARK_OBB_CPU"),
                    "1",
                    StringComparison.Ordinal))
            {
                VisionObbBenchmarkResult cpuBenchmark =
                    VisionObbPerformanceBenchmark.Run(
                        modelPath,
                        preferDirectMl: false,
                        frameWidth: benchmarkWidth,
                        frameHeight: benchmarkHeight);
                Assert.True(cpuBenchmark.ModelAvailable);
                output.WriteLine(
                    $"OBB CPU benchmark: resolution={benchmarkWidth}x{benchmarkHeight}, "
                    + $"provider={cpuBenchmark.Provider}, "
                    + $"baselineP95={cpuBenchmark.BaselineP95Ms:F2}ms, "
                    + $"enhancedP95={cpuBenchmark.EnhancedP95Ms:F2}ms, "
                    + $"addedP95={cpuBenchmark.AddedP95Ms:F2}ms, "
                    + $"baselineCpu={cpuBenchmark.BaselineCpuMsPerFrame:F2}ms/frame, "
                    + $"enhancedCpu={cpuBenchmark.EnhancedCpuMsPerFrame:F2}ms/frame, "
                    + $"addedCpu={cpuBenchmark.AddedCpuMsPerFrame:F2}ms/frame, "
                    + $"recommended={cpuBenchmark.Recommended}");
            }
        }

        string? samplePath = Environment.GetEnvironmentVariable(
            "PACKINGPROOF_WAYBILL_OBB_SAMPLE");
        if (string.IsNullOrWhiteSpace(samplePath) || !File.Exists(samplePath))
            return;

        using Mat sample = Cv2.ImRead(samplePath, ImreadModes.Color);
        Assert.False(sample.Empty());
        IReadOnlyList<VisionObbDetection> sampleDetections = detector.Detect(sample);
        output.WriteLine(
            $"OBB sample candidates={detector.LastRawCandidateCount}, "
            + $"filtered={detector.LastFilteredDetectionCount}: "
            + string.Join(
                "; ",
                sampleDetections.Select(detection =>
                    $"score={detection.Score:F3},"
                    + $"center=({detection.CenterX:F1},{detection.CenterY:F1}),"
                    + $"size={detection.Width:F1}x{detection.Height:F1},"
                    + $"angle={detection.AngleDegrees:F1}")));
        Assert.NotEmpty(sampleDetections);
    }
}
