using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class RecordingSpecificationTests
{
    [Fact]
    public void RecommendationUsesNative15FpsAndHighestResolutionWithHeadroom()
    {
        NativeCameraMode[] modes =
        [
            new(3840, 2160, 15),
            new(1920, 1080, 15),
            new(3840, 2160, 30)
        ];

        RecordingProfileRecommendation result = RecordingProfileDetector.Recommend(
            modes,
            mode => Benchmark(mode, mode.Width == 1920 ? 40 : 10));

        Assert.True(result.Success);
        Assert.Equal(new NativeCameraMode(1920, 1080, 15), result.Mode);
        Assert.All(result.Benchmarks, benchmark => Assert.Equal(15, benchmark.Mode.Fps));
        Assert.Contains(result.Benchmarks, item => item.Mode == new NativeCameraMode(2560, 1440, 15));
        Assert.Contains(result.Benchmarks, item => item.Mode == new NativeCameraMode(1280, 720, 15));
    }

    [Fact]
    public void RecommendationUsesLowestNativeFpsAbove15When15IsUnavailable()
    {
        NativeCameraMode[] modes =
        [
            new(1280, 720, 30),
            new(1920, 1080, 20),
            new(1280, 720, 20),
            new(3840, 2160, 25)
        ];

        RecordingProfileRecommendation result = RecordingProfileDetector.Recommend(
            modes,
            mode => Benchmark(mode, 30));

        Assert.True(result.Success);
        Assert.Equal(new NativeCameraMode(1920, 1080, 20), result.Mode);
    }

    [Fact]
    public void RecommendationBenchmarksFixedAndNonstandardNativeResolutionsOnce()
    {
        NativeCameraMode[] modes =
        [
            new(2304, 1296, 15),
            new(2304, 1296, 30),
            new(1920, 1080, 60)
        ];

        RecordingProfileRecommendation result = RecordingProfileDetector.Recommend(
            modes,
            mode => Benchmark(mode, 80));

        Assert.Equal(5, result.Benchmarks.Count);
        Assert.Single(result.Benchmarks, item => item.Mode.Width == 2304 && item.Mode.Height == 1296);
        Assert.All(result.Benchmarks, item => Assert.Equal(15, item.Mode.Fps));
    }

    [Fact]
    public void RecommendationRejectsCameraWithoutNativeModeAtLeast15ButStillBenchmarks()
    {
        int benchmarkCalls = 0;

        RecordingProfileRecommendation result = RecordingProfileDetector.Recommend(
            [new NativeCameraMode(1920, 1080, 10)],
            mode =>
            {
                benchmarkCalls++;
                return Benchmark(mode, 100);
            });

        Assert.False(result.Success);
        Assert.Null(result.Mode);
        Assert.Equal(4, benchmarkCalls);
        Assert.Contains("至少 15 FPS", result.Message);
    }

    [Theory]
    [InlineData(17.99, false)]
    [InlineData(18.00, true)]
    public void EncodingCapacityRequires20PercentHeadroom(
        double measuredFps,
        bool expected)
    {
        RealtimeEncodingBenchmarkResult benchmark = Benchmark(
            new NativeCameraMode(1920, 1080, 15),
            measuredFps);

        Assert.Equal(expected, benchmark.SupportsFrameRate(15));
    }

    [Fact]
    public void SameResolutionCapacityCanBeComparedAgainstDifferentNativeFps()
    {
        RealtimeEncodingBenchmarkResult benchmark = Benchmark(
            new NativeCameraMode(1920, 1080, 15),
            36);

        Assert.True(benchmark.SupportsFrameRate(15));
        Assert.True(benchmark.SupportsFrameRate(30));
        Assert.False(benchmark.SupportsFrameRate(31));
    }

    [Fact]
    public void IncompleteTimedOutBenchmarkNeverQualifiesDespitePartialThroughput()
    {
        RealtimeEncodingBenchmarkResult benchmark = Benchmark(
            new NativeCameraMode(3840, 2160, 15),
            50,
            completed: false,
            encodedFrames: 90);

        Assert.Equal(90, benchmark.EncodedFrames);
        Assert.False(benchmark.SupportsFrameRate(15));
    }

    [Fact]
    public void ProgressParserKeepsHighestCompletedFrameCount()
    {
        const string progress = """
            frame=12
            fps=0.00
            progress=continue
            frame=87
            progress=continue
            frame=83
            progress=end
            """;

        Assert.Equal(87, RecordingProfileDetector.ParseProgressFrameCount(progress));
    }

    [Theory]
    [InlineData(180, 3.0, 60.0)]
    [InlineData(90, 12.0, 7.5)]
    [InlineData(0, 0.0, 0.0)]
    public void MeasuredEncodingFpsUsesCompletedFramesAndElapsedTime(
        int frames,
        double elapsed,
        double expected)
    {
        Assert.Equal(
            expected,
            RecordingProfileDetector.CalculateMeasuredEncodingFps(frames, elapsed),
            6);
    }

    [Fact]
    public void SafeFallbackUsesLowestFpsAtLeast15AndClosest720p()
    {
        NativeCameraMode? mode = RecordingProfileDetector.SelectSafeFallback(
        [
            new(1920, 1080, 15),
            new(1280, 720, 20),
            new(1024, 768, 15),
            new(1280, 720, 30)
        ]);

        Assert.Equal(new NativeCameraMode(1024, 768, 15), mode);
    }

    [Fact]
    public void SafeFallbackUsesHighestNativeFpsWhenAllModesAreBelow15()
    {
        NativeCameraMode? mode = RecordingProfileDetector.SelectSafeFallback(
        [
            new(1920, 1080, 5),
            new(1024, 768, 10),
            new(1280, 720, 10)
        ]);

        Assert.Equal(new NativeCameraMode(1280, 720, 10), mode);
    }

    [Fact]
    public void SafeFallbackLeavesDefaultsWhenCapabilityListIsEmpty()
    {
        Assert.Null(RecordingProfileDetector.SelectSafeFallback([]));
    }

    [Fact]
    public void CompletedVideoProbeParsesHeaderMetadataWithoutFrameScan()
    {
        const string output = """
            Input #0, matroska,webm, from 'sample.mkv':
              Duration: 00:01:02.40, start: 0.000000, bitrate: 1000 kb/s
              Stream #0:0: Video: hevc, yuv420p, 1920x1080 [SAR 1:1 DAR 16:9], 14.70 fps, 15 tbr, 1k tbn
            At least one output file must be specified
            """;

        bool parsed = CompletedVideoSpecificationProbe.TryParse(output, out CompletedVideoMetadata metadata);

        Assert.True(parsed);
        Assert.Equal(1920, metadata.Width);
        Assert.Equal(1080, metadata.Height);
        Assert.Equal(62.4, metadata.DurationSeconds, 3);
        Assert.Equal(14.7, metadata.AverageFrameRate, 3);
    }

    [Fact]
    public void CompletedVideoEvaluationSkipsShortRecordings()
    {
        var expected = new ExpectedRecordingSpecification(1920, 1080, 15, 9.9);
        var actual = new CompletedVideoMetadata(1280, 720, 5, 5);

        CompletedVideoSpecificationEvaluation result =
            CompletedVideoSpecificationProbe.Evaluate(expected, actual);

        Assert.False(result.ShouldEvaluate);
        Assert.True(result.MeetsSpecification);
    }

    [Theory]
    [InlineData(12.74, false)]
    [InlineData(12.75, true)]
    public void CompletedVideoEvaluationUses85PercentFrameRateThreshold(
        double actualFps,
        bool expectedPass)
    {
        var expected = new ExpectedRecordingSpecification(1920, 1080, 15, 30);
        var actual = new CompletedVideoMetadata(1920, 1080, 30, actualFps);

        CompletedVideoSpecificationEvaluation result =
            CompletedVideoSpecificationProbe.Evaluate(expected, actual);

        Assert.True(result.ShouldEvaluate);
        Assert.Equal(expectedPass, result.MeetsSpecification);
    }

    [Fact]
    public void CompletedVideoEvaluationChecksResolutionAndDuration()
    {
        var expected = new ExpectedRecordingSpecification(1920, 1080, 20, 30);
        var actual = new CompletedVideoMetadata(1280, 720, 26.9, 20);

        CompletedVideoSpecificationEvaluation result =
            CompletedVideoSpecificationProbe.Evaluate(expected, actual);

        Assert.True(result.ShouldEvaluate);
        Assert.False(result.MeetsSpecification);
        Assert.Contains("分辨率", result.Reason);
        Assert.Contains("时长", result.Reason);
    }

    [Fact]
    public void RecordingArgumentsDoNotForceSoftwareFrameRateConversion()
    {
        string args = MainViewModel.BuildFFmpegArgs(
            1920,
            1080,
            20,
            "recording.mkv",
            "libx264",
            false,
            25);

        Assert.Contains("-framerate 20", args);
        Assert.DoesNotContain("-fps_mode", args);
        Assert.DoesNotContain(" -r 20", args);
    }

    [Fact]
    public void BatchConversionResultKeepsOneFinalPathPerSource()
    {
        var result = new MkvBatchConversionResult();

        result.MarkProcessedSource("recording.mkv");
        result.MarkProcessedSource("RECORDING.mkv");
        result.AddFinalFile("recording.mkv", "recording.mp4");
        result.AddFinalFile("RECORDING.mkv", "other.mp4");

        Assert.Single(result.ProcessedSources);
        MkvFinalizedFile item = Assert.Single(result.FinalFiles);
        Assert.Equal("recording.mkv", item.SourcePath);
        Assert.Equal("recording.mp4", item.FinalPath);
    }

    [Fact]
    public void ManualRecommendationChangesConfigOnlyAfterApply()
    {
        var config = new AppConfig
        {
            FrameWidth = 3840,
            FrameHeight = 2160,
            Fps = 30,
            CameraMonikerString = "camera-1"
        };
        var recommendation = new NativeCameraMode(1920, 1080, 15);

        Assert.True(RecordingProfileDetector.IsRecommendationDifferent(config, recommendation));
        RecordingProfileDetector.ApplyRecommendation(
            config,
            recommendation,
            config.CameraMonikerString);

        Assert.False(RecordingProfileDetector.IsRecommendationDifferent(config, recommendation));
        Assert.Equal(1920, config.CameraConfigs["camera-1"].FrameWidth);
        Assert.Equal(1080, config.CameraConfigs["camera-1"].FrameHeight);
        Assert.Equal(15, config.CameraConfigs["camera-1"].Fps);
    }

    [Fact]
    public void BenchmarkCacheMapsCameraFpsToResolutionCapacity()
    {
        var config = new AppConfig();
        DateTime testedAt = new(2026, 7, 29, 12, 0, 0);
        RecordingProfileDetector.UpdateBenchmarkCache(
            config,
            "libx264",
            30,
            [
                Benchmark(new NativeCameraMode(3840, 2160, 15), 10),
                Benchmark(new NativeCameraMode(2560, 1440, 15), 30),
                Benchmark(new NativeCameraMode(1920, 1080, 15), 80)
            ],
            testedAt);

        bool found = RecordingProfileDetector.TryRecommendFromCache(
            config,
            "libx264",
            30,
            [
                new NativeCameraMode(3840, 2160, 20),
                new NativeCameraMode(2560, 1440, 20),
                new NativeCameraMode(1920, 1080, 20)
            ],
            out NativeCameraMode recommended);

        Assert.True(found);
        Assert.Equal(new NativeCameraMode(2560, 1440, 20), recommended);
    }

    [Fact]
    public void BenchmarkCacheKeepsInsufficientResultForManualWarning()
    {
        var config = new AppConfig();
        var mode = new NativeCameraMode(3840, 2160, 15);
        RecordingProfileDetector.UpdateBenchmarkCache(
            config,
            "libx264",
            25,
            [Benchmark(mode, 17)],
            new DateTime(2026, 7, 29, 12, 0, 0));

        bool found = RecordingProfileDetector.TryGetCachedBenchmark(
            config,
            "libx264",
            25,
            mode,
            out RecordingBenchmarkCacheEntry cached);

        Assert.True(found);
        Assert.Equal(17, cached.MeasuredEncodingFps);
        Assert.False(RecordingProfileDetector.CachedBenchmarkSupportsFrameRate(cached, 15));
    }

    [Fact]
    public void Schema1BenchmarkCacheIsIgnored()
    {
        var config = new AppConfig
        {
            RecordingBenchmarkCache =
            [
                new RecordingBenchmarkCacheEntry
                {
                    SchemaVersion = 1,
                    Encoder = "libx264",
                    VideoCqp = 25,
                    Width = 1920,
                    Height = 1080,
                    CompletedSuccessfully = true,
                    MeasuredEncodingFps = 100,
                    TestedAt = DateTime.Now
                }
            ]
        };

        Assert.False(RecordingProfileDetector.TryGetCachedBenchmark(
            config,
            "libx264",
            25,
            new NativeCameraMode(1920, 1080, 15),
            out _));
    }

    [Fact]
    public void BenchmarkCacheSeparatesEncoderAndQuality()
    {
        var config = new AppConfig();
        var mode = new NativeCameraMode(1920, 1080, 15);
        RecordingProfileDetector.UpdateBenchmarkCache(
            config,
            "libx264",
            30,
            [Benchmark(mode, 30)],
            new DateTime(2026, 7, 29, 12, 0, 0));

        Assert.False(RecordingProfileDetector.TryGetCachedBenchmark(
            config,
            "h264_nvenc",
            30,
            mode,
            out _));
        Assert.False(RecordingProfileDetector.TryGetCachedBenchmark(
            config,
            "libx264",
            25,
            mode,
            out _));
    }

    [Fact]
    public void FindFfmpegKeepsProgramDirectoryPriority()
    {
        string root = CreateTempDirectory();
        try
        {
            string tools = Directory.CreateDirectory(Path.Combine(root, "tools")).FullName;
            string toolsFfmpeg = Path.Combine(tools, "ffmpeg.exe");
            string baseFfmpeg = Path.Combine(root, "ffmpeg.exe");
            File.WriteAllText(toolsFfmpeg, "");
            File.WriteAllText(baseFfmpeg, "");

            Assert.Equal(toolsFfmpeg, AppPaths.FindFFmpeg(root, ""));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindFfmpegSearchesProjectAncestorsBeforePath()
    {
        string root = CreateTempDirectory();
        try
        {
            string baseDir = Directory.CreateDirectory(
                Path.Combine(root, "a", "b", "c")).FullName;
            string projectFfmpeg = Path.Combine(root, "ffmpeg.exe");
            string pathDir = Directory.CreateDirectory(Path.Combine(root, "path")).FullName;
            File.WriteAllText(projectFfmpeg, "");
            File.WriteAllText(Path.Combine(pathDir, "ffmpeg.exe"), "");

            Assert.Equal(projectFfmpeg, AppPaths.FindFFmpeg(baseDir, pathDir));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindFfmpegFallsBackToSystemPath()
    {
        string root = CreateTempDirectory();
        try
        {
            string baseDir = Directory.CreateDirectory(Path.Combine(root, "base")).FullName;
            string pathDir = Directory.CreateDirectory(Path.Combine(root, "path")).FullName;
            string expected = Path.Combine(pathDir, "ffmpeg.exe");
            File.WriteAllText(expected, "");

            Assert.Equal(expected, AppPaths.FindFFmpeg(baseDir, pathDir));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingFfmpegDoesNotProduceValidatedEncoderOrSuccessState()
    {
        EncoderDetectionResult result =
            MainViewModel.DetectAvailableEncodersSync(null);

        Assert.False(result.FfmpegAvailable);
        Assert.False(result.Succeeded);
        Assert.Empty(result.ValidatedEncoders);
        Assert.Null(result.NvencDriverIssue);
    }

    private static RealtimeEncodingBenchmarkResult Benchmark(
        NativeCameraMode mode,
        double measuredFps,
        bool completed = true,
        int encodedFrames = RecordingProfileDetector.BenchmarkFrameCount)
    {
        double elapsed = measuredFps > 0 ? encodedFrames / measuredFps : 0;
        return new RealtimeEncodingBenchmarkResult(
            mode,
            completed,
            elapsed,
            encodedFrames,
            measuredFps,
            RecordingProfileDetector.RequiredEncodingSpeed,
            "");
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ExpressPackingMonitoring.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
