using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Vision;
using OpenCvSharp;
using System.IO;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VisionObbPerformanceBenchmarkTests
{
    [Fact]
    public void ProductionBenchmarkCoversFalsePositiveAndBareWaybillPaths()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "Services",
            "VisionObbPerformanceBenchmark.cs"));

        Assert.Contains("candidateCount: 1", source);
        Assert.Contains("candidateCount: 3", source);
        Assert.Contains("candidateCount: 6", source);
        Assert.Contains("bareWaybill: true", source);
        Assert.Contains("\"JD0123456789\"", source);
    }

    [Fact]
    public void ProductionBenchmarkSkipsBaselineAfterProviderAlreadyFails()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "Services",
            "VisionObbPerformanceBenchmark.cs"));

        Assert.Contains("MeasureNoObbProduction", source, StringComparison.Ordinal);
        Assert.Contains("enhancedScenarios", source, StringComparison.Ordinal);
        Assert.Contains(
            "ShouldAbortProviderBenchmark",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "bool recommended = allScenariosCompleted",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AllScenariosCompleted: allScenariosCompleted",
            source,
            StringComparison.Ordinal);
        Assert.Contains("if (!recommended)", source, StringComparison.Ordinal);
        Assert.Contains("noObbScenarios", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf(
                "if (!recommended)",
                StringComparison.Ordinal)
            < source.IndexOf(
                "noObbScenarios",
                StringComparison.Ordinal));
    }

    [Fact]
    public void CandidateDiagnosticsRecordPixelAndFrameDiagonalPercentage()
    {
        string decoder = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "Services",
            "CameraBarcodeObbDecoder.cs"));
        string diagnosticLog = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "Services",
            "VisionObbDiagnosticLog.cs"));
        string appPaths = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "Config",
            "AppPaths.cs"));

        Assert.Contains(
            "VisionObbDiagnosticLog.Append",
            decoder,
            StringComparison.Ordinal);
        Assert.Contains(
            "vision_obb_diagnostics.log",
            appPaths,
            StringComparison.Ordinal);
        Assert.Contains("diagonal_percent", diagnosticLog, StringComparison.Ordinal);
        Assert.Contains("decode_allowed", diagnosticLog, StringComparison.Ordinal);
    }

    [Fact]
    public void TimingDiagnosticsCoverDetectionDecodeAndZxingOrientationStages()
    {
        string decoder = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "Services",
            "CameraBarcodeObbDecoder.cs"));
        string timingLog = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "Services",
            "VisionObbTimingDiagnosticLog.cs"));
        string detector = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring.Vision.Obb",
            "Services",
            "VisionObbDetector.cs"));
        string recognition = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "Services",
            "CameraBarcodeRecognitionService.cs"));
        string fastDecoder = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "Services",
            "CameraBarcodeFastDecoder.cs"));
        string appPaths = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "Config",
            "AppPaths.cs"));

        Assert.Contains("vision_obb_timing.log", appPaths, StringComparison.Ordinal);
        Assert.Contains("preprocess_ms", timingLog, StringComparison.Ordinal);
        Assert.Contains("inference_ms", timingLog, StringComparison.Ordinal);
        Assert.Contains("postprocess_ms", timingLog, StringComparison.Ordinal);
        Assert.Contains("patch_ms", timingLog, StringComparison.Ordinal);
        Assert.Contains("decode_ms", timingLog, StringComparison.Ordinal);
        Assert.Contains("zxing_orientation_deg", timingLog, StringComparison.Ordinal);
        Assert.Contains("LastTiming = new VisionObbDetectorTiming", detector);
        Assert.Contains("VisionObbTimingDiagnosticLog.Append", decoder);
        Assert.Contains("CameraBarcodeDecodeAngleTelemetry.Decode", recognition);
        Assert.Contains("CameraBarcodeDecodeAngleTelemetry.Record", recognition);
        Assert.Contains("CameraBarcodeDecodeAngleTelemetry.Decode", fastDecoder);
    }

    [Fact]
    public void CiRunsProductionBenchmarkWithoutRequiringASamplePath()
    {
        string testSource = File.ReadAllText(FindRepositoryFile(
            "tests",
            "ExpressPackingMonitoring.Tests",
            "CameraVisionObbTests.cs"));
        string workflow = File.ReadAllText(FindRepositoryFile(
            ".github",
            "workflows",
            "ci.yml"));

        Assert.Contains("PACKINGPROOF_BENCHMARK_OBB=1", workflow);
        int benchmarkIndex = testSource.IndexOf(
            "VisionObbPerformanceBenchmark.Run(",
            StringComparison.Ordinal);
        int sampleIndex = testSource.IndexOf(
            "PACKINGPROOF_WAYBILL_OBB_SAMPLE",
            StringComparison.Ordinal);
        Assert.True(
            benchmarkIndex >= 0 && sampleIndex > benchmarkIndex,
            "CI benchmark must run before the optional real-sample branch.");
    }

    [Fact]
    public void CameraResolutionParticipatesInBenchmarkAndCacheLookup()
    {
        string viewModel = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.CameraVisionObb.cs"));
        string benchmark = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "Services",
            "VisionObbPerformanceBenchmark.cs"));

        Assert.Contains("Config.FrameWidth", viewModel);
        Assert.Contains("Config.FrameHeight", viewModel);
        Assert.Contains("Config.CameraVisionBenchmarkCache", viewModel);
        Assert.Contains("StoreCameraVisionBenchmarkCache", viewModel);
        Assert.Contains("FrameWidth = frameWidth", viewModel);
        Assert.Contains("FrameHeight = frameHeight", viewModel);
        Assert.DoesNotContain(
            "FrameWidth = Config.FrameWidth",
            viewModel);
        Assert.Contains("frameWidth: benchmarkWidth", viewModel);
        Assert.Contains("frameHeight: benchmarkHeight", viewModel);
        Assert.Contains(
            "guideGeometry: benchmarkGuide",
            viewModel);
        Assert.Contains("ShouldCompareCpuProvider", viewModel);
        Assert.Contains("_actualCameraWidth > 0", viewModel);
        Assert.Contains("_actualCameraHeight > 0", viewModel);
        Assert.Contains("new Size(frameWidth, frameHeight)", benchmark);
        Assert.Contains(
            "CameraBarcodeGuideLayout.ToFrameRect",
            benchmark);
        Assert.Contains("new Mat(frame, guide)", benchmark);

        string settings = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Settings.cs"));
        Assert.Contains("mainCameraResolutionChanged", settings);
        Assert.Contains("_ = RunCameraVisionBenchmarkIfNeededAsync()", settings);
    }

    [Fact]
    public void CameraVisionBenchmarkDefersAndCancelsInsteadOfDroppingWork()
    {
        string viewModel = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.CameraVisionObb.cs"));
        string settings = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Settings.cs"));
        string recording = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Recording.cs"));
        string camera = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Camera.cs"));
        string cameraGuide = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.CameraGuide.cs"));
        string cameraRotation = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.CameraRotation.cs"));

        Assert.Contains(
            "_cameraVisionBenchmarkRerunRequested",
            viewModel);
        Assert.Contains(
            "CancelCameraVisionBenchmarkIfRunning",
            viewModel);
        Assert.Contains(
            "TryRunDeferredCameraVisionBenchmark",
            viewModel);
        Assert.Contains(
            "CancelCameraVisionBenchmarkIfRunning(",
            settings);
        Assert.Contains(
            "TryRunDeferredCameraVisionBenchmark",
            recording);
        Assert.Contains(
            "DeferCameraVisionBenchmarkForRecording",
            recording);
        Assert.Contains(
            "InternalStopRecordingCoreAsync",
            recording);
        Assert.Contains(
            "NotifyCameraVisionFrameGeometryChanged",
            camera);
        Assert.Contains(
            "NotifyCameraVisionFrameGeometryChanged",
            cameraGuide);
        Assert.Contains(
            "_actualCameraRotationDegrees",
            cameraRotation);
        Assert.Contains(
            "Task.Run(() =>",
            viewModel);
        Assert.Contains(
            "AllScenariosCompleted",
            viewModel);
        Assert.Contains(
            "NotifyCameraVisionFrameGeometryChanged",
            viewModel);
    }

    [Fact]
    public void SyntheticBenchmarkFramesFollowConfiguredResolution()
    {
        (Mat Frame, ExpressPackingMonitoring.Vision.VisionObbDetection[] Detections)
            generated = VisionObbPerformanceBenchmark.CreateBenchmarkFrame(
                2560,
                1440,
                barcodeCount: 6);
        using Mat frame = generated.Frame;

        Assert.Equal(new Size(2560, 1440), frame.Size());
        Assert.Equal(12, generated.Detections.Length);
        Assert.Equal(
            6,
            generated.Detections.Count(detection =>
                VisionObbClassPolicy.IsWaybill(detection)));
        Assert.Equal(
            6,
            generated.Detections.Count(detection =>
                VisionObbClassPolicy.IsBarcode(detection)));
        Rect guide = CameraBarcodeGuideLayout.ToFrameRect(
            2560,
            1440,
            CameraBarcodeGuideGeometry.Default);
        Assert.All(generated.Detections, detection =>
        {
            Assert.InRange(detection.CenterX, guide.Left, guide.Right);
            Assert.InRange(detection.CenterY, guide.Top, guide.Bottom);
            Assert.InRange(detection.Width, 1, 2560);
            Assert.InRange(detection.Height, 1, 1440);
        });
    }

    [Fact]
    public void ScreeningBenchmarkConfirmsBorderlineResultsWithRobustP95()
    {
        Assert.Equal(
            1,
            VisionObbPerformanceBenchmark.ScreeningWarmupIterations);
        Assert.Equal(
            7,
            VisionObbPerformanceBenchmark.ScreeningMeasuredIterations);
        Assert.Equal(
            21,
            VisionObbPerformanceBenchmark.ConfirmationMeasuredIterations);
        Assert.Equal(
            0.75,
            VisionObbPerformanceBenchmark.ClearPerformanceRatio);
    }

    [Theory]
    [InlineData(true, false, 0, true)]
    [InlineData(true, true, 100, false)]
    [InlineData(true, true, 140, true)]
    [InlineData(true, true, 160, true)]
    [InlineData(false, true, 100, false)]
    public void CpuComparisonOnlyRunsWhenDirectMlCannotConclude(
        bool modelAvailable,
        bool recommended,
        double enhancedP95Ms,
        bool expected)
    {
        var directMl = new VisionObbBenchmarkResult(
            ModelAvailable: modelAvailable,
            Provider: "DirectML",
            BaselineP95Ms: 20,
            EnhancedP95Ms: enhancedP95Ms,
            BaselineCpuMsPerFrame: 20,
            EnhancedCpuMsPerFrame: enhancedP95Ms,
            Recommended: recommended);

        Assert.Equal(
            expected,
            VisionObbPerformanceBenchmark.ShouldCompareCpuProvider(directMl));
    }

    [Theory]
    [InlineData("DirectML", 1, 500, true)]
    [InlineData("DirectML", 175, 500, true)]
    [InlineData("DirectML", 175.01, 40, false)]
    [InlineData("CPU", 40, 100, true)]
    [InlineData("CPU", 175, 175, true)]
    [InlineData("CPU", 175.01, 100, false)]
    [InlineData("CPU", 40, 175.01, false)]
    [InlineData("CPU", 40, 0, false)]
    [InlineData("DirectML", 0, 10, false)]
    [InlineData("Unknown", 40, 40, false)]
    public void RecommendationUsesFourFpsWithThirtyPercentHeadroom(
        string provider,
        double p95Ms,
        double cpuMsPerFrame,
        bool expected)
    {
        Assert.Equal(
            expected,
            VisionObbPerformanceBenchmark.ShouldRecommend(
                provider,
                p95Ms,
                cpuMsPerFrame));
        Assert.Equal(250, VisionObbPerformanceBenchmark.TargetIntervalMs);
        Assert.Equal(175, VisionObbPerformanceBenchmark.RecommendedP95Ms);
        Assert.Equal(175, VisionObbPerformanceBenchmark.RecommendedCpuMsPerFrame);
    }

    [Theory]
    [InlineData("model-a", "", true)]
    [InlineData("model-a", "model-b", true)]
    [InlineData("model-a", "model-a", false)]
    [InlineData("", "", false)]
    public void BenchmarkRunsOncePerModelIdentity(
        string currentModelKey,
        string completedModelKey,
        bool expected)
    {
        Assert.Equal(
            expected,
            VisionObbPerformanceBenchmark.ShouldRunForModel(
                currentModelKey,
                completedModelKey));
    }

    [Fact]
    public void ProviderSelectionPrefersRecommendedThenMeaningfullyFasterProvider()
    {
        static VisionObbBenchmarkResult Result(
            string provider,
            double p95,
            bool recommended) =>
            new(
                ModelAvailable: true,
                Provider: provider,
                BaselineP95Ms: 20,
                EnhancedP95Ms: p95,
                BaselineCpuMsPerFrame: 20,
                EnhancedCpuMsPerFrame: p95,
                Recommended: recommended);

        Assert.Equal(
            "CPU",
            VisionObbPerformanceBenchmark.SelectFasterProvider(
                Result("DirectML", 100, recommended: false),
                Result("CPU", 120, recommended: true)).Provider);
        Assert.Equal(
            "DirectML",
            VisionObbPerformanceBenchmark.SelectFasterProvider(
                Result("DirectML", 100, recommended: true),
                Result("CPU", 80, recommended: false)).Provider);
        Assert.Equal(
            "CPU",
            VisionObbPerformanceBenchmark.SelectFasterProvider(
                Result("DirectML", 100, recommended: false),
                Result("CPU", 80, recommended: false)).Provider);
        Assert.Equal(
            "DirectML",
            VisionObbPerformanceBenchmark.SelectFasterProvider(
                Result("DirectML", 100, recommended: false),
                Result("CPU", 95, recommended: false)).Provider);
    }

    [Fact]
    public void ModelIdentityReadFailureReturnsEmptyInsteadOfThrowing()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "pp-locked-vision-model-" + Guid.NewGuid().ToString("N") + ".onnx");
        File.WriteAllText(path, "locked-model");
        try
        {
            using var exclusive = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            Assert.Empty(VisionObbPerformanceBenchmark.GetModelIdentity(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine([directory.FullName, .. relativeParts]);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Repository file not found: {Path.Combine(relativeParts)}");
    }
}
