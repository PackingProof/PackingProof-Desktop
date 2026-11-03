using ExpressPackingMonitoring.Services;
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

        Assert.Contains(
            "CreateFalsePositiveBenchmarkFrame(frameWidth, frameHeight, candidateCount: 1)",
            source);
        Assert.Contains(
            "CreateFalsePositiveBenchmarkFrame(frameWidth, frameHeight, candidateCount: 3)",
            source);
        Assert.Contains(
            "CreateFalsePositiveBenchmarkFrame(frameWidth, frameHeight, candidateCount: 6)",
            source);
        Assert.Contains("bareWaybill: true", source);
        Assert.Contains("\"JD0123456789\"", source);
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
        Assert.Contains("frameWidth: Config.FrameWidth", viewModel);
        Assert.Contains("frameHeight: Config.FrameHeight", viewModel);
        Assert.Contains("new Size(frameWidth, frameHeight)", benchmark);

        string settings = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Settings.cs"));
        Assert.Contains("mainCameraResolutionChanged", settings);
        Assert.Contains("_ = RunCameraVisionBenchmarkIfNeededAsync()", settings);
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
        Assert.Equal(6, generated.Detections.Length);
        Assert.All(generated.Detections, detection =>
        {
            Assert.InRange(detection.CenterX, 0, 2560);
            Assert.InRange(detection.CenterY, 0, 1440);
            Assert.InRange(detection.Width, 1, 2560);
            Assert.InRange(detection.Height, 1, 1440);
        });
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
