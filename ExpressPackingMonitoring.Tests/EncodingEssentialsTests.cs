using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class EncodingEssentialsTests
{
    [Fact]
    public void EncoderDetection_DoesNotStartPermanentlyBusyOrBlockSettings()
    {
        string source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "ExpressPackingMonitoring", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("private volatile bool _isEncoderDetectRunning;", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (_isEncoderDetectRunning)\r\n            {\r\n                ShowToast(\"处理中：编码器环境检测中",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (_isEncoderDetectRunning)\n            {\n                ShowToast(\"处理中：编码器环境检测中",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EncoderDetection_AllEntryPointsUseSharedPolicyService()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..");
        string mainEncoder = File.ReadAllText(Path.Combine(
            root, "ExpressPackingMonitoring", "ViewModels", "MainViewModel.Encoder.cs"));
        string wizard = File.ReadAllText(Path.Combine(
            root, "ExpressPackingMonitoring", "UI", "FirstUseSetupWizardWindow.xaml.cs"));

        Assert.Contains("EncoderProfileDetectionService.DetectAsync", mainEncoder, StringComparison.Ordinal);
        Assert.Contains("EncoderProfileDetectionService.DetectAsync", wizard, StringComparison.Ordinal);
        Assert.DoesNotContain("MainViewModel.DetectAvailableEncodersSync()", wizard, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordingProfileDetector.Benchmark(", wizard, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticSelection_PrefersHardwareH265OverFasterHardwareH264()
    {
        EncodingHelper.EncoderSelection? selection = EncodingHelper.SelectEncoder(
        [
            Candidate("hevc_nvenc", 45, true),
            Candidate("h264_qsv", 120, true),
            Candidate("libx264", 200, true)
        ], "auto", "");

        Assert.NotNull(selection);
        Assert.Equal("hevc_nvenc", selection.Encoder);
    }

    [Fact]
    public void AutomaticSelection_ChoosesFastestHardwareWithinSameCodec()
    {
        EncodingHelper.EncoderSelection? selection = EncodingHelper.SelectEncoder(
        [
            Candidate("hevc_nvenc", 45, true),
            Candidate("hevc_qsv", 70, true),
            Candidate("h264_qsv", 140, true)
        ], "auto", "");

        Assert.NotNull(selection);
        Assert.Equal("hevc_qsv", selection.Encoder);
    }

    [Fact]
    public void AutomaticSelection_UsesFastestCpuWhenNoHardwareQualifies()
    {
        EncodingHelper.EncoderSelection? selection = EncodingHelper.SelectEncoder(
        [
            Candidate("h264_qsv", 10, false),
            Candidate("libx264", 75, true),
            Candidate("libx265", 60, true)
        ], "auto", "");

        Assert.NotNull(selection);
        Assert.Equal("libx264", selection.Encoder);
    }

    [Fact]
    public void AutomaticSelection_WhenNothingQualifies_UsesFastestH264OrH265()
    {
        EncodingHelper.EncoderSelection? selection = EncodingHelper.SelectEncoder(
        [
            Candidate("hevc_qsv", 12, false),
            Candidate("h264_qsv", 26, false),
            Candidate("libx264", 19, false),
            Candidate("av1_nvenc", 80, false)
        ], "auto", "");

        Assert.NotNull(selection);
        Assert.Equal("h264_qsv", selection.Encoder);
        Assert.False(selection.MeetsRealtimeRequirement);
    }

    [Fact]
    public void AutomaticSelection_ExcludesAv1()
    {
        EncodingHelper.EncoderSelection? selection = EncodingHelper.SelectEncoder(
        [
            Candidate("av1_nvenc", 300, true),
            Candidate("h264_qsv", 80, true)
        ], "auto", "");

        Assert.NotNull(selection);
        Assert.Equal("h264_qsv", selection.Encoder);
    }

    [Fact]
    public void ManualSelection_IsPreservedOnlyWhenItMeetsRealtimeRequirement()
    {
        EncodingHelper.EncoderSelection? valid = EncodingHelper.SelectEncoder(
            [Candidate("av1_nvenc", 80, true)], "manual", "av1_nvenc");
        EncodingHelper.EncoderSelection? invalid = EncodingHelper.SelectEncoder(
            [Candidate("av1_nvenc", 20, false)], "manual", "av1_nvenc");

        Assert.NotNull(valid);
        Assert.True(valid.IsManual);
        Assert.Equal("av1_nvenc", valid.Encoder);
        Assert.Null(invalid);
    }

    [Fact]
    public void FieldCase_OnlyValidatedQsvH264_DoesNotSelectCpuH265()
    {
        EncodingHelper.EncoderSelection? selection = EncodingHelper.SelectEncoder(
        [
            Candidate("h264_qsv", 80, true),
            Candidate("libx265", 17, false),
            Candidate("libx264", 25, false)
        ], "auto", "");

        Assert.NotNull(selection);
        Assert.Equal("h264_qsv", selection.Encoder);
    }

    [Fact]
    public void CachedFieldCase_OnlyQualifiedQsvH264_DoesNotSelectCpuH265()
    {
        var config = new AppConfig
        {
            IsEncoderDetected = true,
            EncoderDetectionCacheVersion = AppConfig.CurrentEncoderSelectionCacheVersion,
            ValidatedEncodersCache = ["h264_qsv", "libx264", "libx265"],
            VideoCqp = 30
        };
        NativeCameraMode mode = new(1224, 2176, 60);
        DateTime testedAt = DateTime.Now;
        CacheBenchmark(config, "h264_qsv", mode, 120, testedAt);
        CacheBenchmark(config, "libx264", mode, 40, testedAt);
        CacheBenchmark(config, "libx265", mode, 35, testedAt);

        bool resolved = MainViewModel.TryResolveCachedEncoder(
            config,
            config.ValidatedEncodersCache,
            mode,
            out EncodingHelper.EncoderSelection? selection);

        Assert.True(resolved);
        Assert.NotNull(selection);
        Assert.Equal("h264_qsv", selection.Encoder);
    }

    [Fact]
    public void CachedSelection_RequiresAllCandidateBenchmarksAtExactFrameRate()
    {
        var config = new AppConfig
        {
            IsEncoderDetected = true,
            EncoderDetectionCacheVersion = AppConfig.CurrentEncoderSelectionCacheVersion,
            ValidatedEncodersCache = ["h264_qsv", "libx264"]
        };
        NativeCameraMode testedMode = new(1920, 1080, 15);
        CacheBenchmark(config, "h264_qsv", testedMode, 90, DateTime.Now);
        CacheBenchmark(config, "libx264", testedMode, 60, DateTime.Now);

        Assert.False(MainViewModel.TryResolveCachedEncoder(
            config,
            config.ValidatedEncodersCache,
            new NativeCameraMode(1920, 1080, 30),
            out _));
    }

    [Fact]
    public void CachedManualSelection_IsRetainedWhenItPassesCapabilityAndPerformanceChecks()
    {
        var config = new AppConfig
        {
            IsEncoderDetected = true,
            EncoderDetectionCacheVersion = AppConfig.CurrentEncoderSelectionCacheVersion,
            VideoEncoderSelectionMode = "manual",
            ManualVideoEncoder = "av1_qsv",
            ValidatedEncodersCache = ["h264_qsv", "av1_qsv"]
        };
        NativeCameraMode mode = new(1920, 1080, 30);
        CacheBenchmark(config, "h264_qsv", mode, 80, DateTime.Now);
        CacheBenchmark(config, "av1_qsv", mode, 50, DateTime.Now);

        Assert.True(MainViewModel.TryResolveCachedEncoder(
            config,
            config.ValidatedEncodersCache,
            mode,
            out EncodingHelper.EncoderSelection? selection));
        Assert.NotNull(selection);
        Assert.True(selection.IsManual);
        Assert.Equal("av1_qsv", selection.Encoder);
    }

    [Fact]
    public void VisibleOptions_ExcludeUnqualifiedAndKeepOnlyFallbackWhenAllFail()
    {
        var selection = new EncodingHelper.EncoderSelection("h264_qsv", false, false, "fallback");
        List<VideoEncoderOption> options = MainViewModel.BuildVisibleEncoderOptions(
        [
            Candidate("h264_qsv", 26, false),
            Candidate("libx265", 17, false),
            Candidate("av1_nvenc", 80, false)
        ], selection, 30);

        VideoEncoderOption option = Assert.Single(options);
        Assert.Equal("h264_qsv", option.Value);
        Assert.False(option.MeetsRealtimeRequirement);
    }

    [Fact]
    public void ConfigurationNormalizesNewEncoderSelectionFields()
    {
        var config = new AppConfig
        {
            EncoderDetectionCacheVersion = AppConfig.CurrentEncoderSelectionCacheVersion,
            VideoEncoderSelectionMode = "UNEXPECTED",
            ManualVideoEncoder = " H264_QSV ",
            EffectiveVideoEncoder = " HEVC_QSV "
        };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        Assert.Equal("auto", config.VideoEncoderSelectionMode);
        Assert.Equal("h264_qsv", config.ManualVideoEncoder);
        Assert.Equal("hevc_qsv", config.EffectiveVideoEncoder);
    }

    [Fact]
    public void LegacyEncoderCache_IsClearedAndForcesRedetection()
    {
        var config = new AppConfig
        {
            EncoderDetectionCacheVersion = 3,
            IsEncoderDetected = true,
            EffectiveVideoEncoder = "libx265",
            EncoderOptionsCache = [new VideoEncoderOption { Value = "cpu" }],
            ValidatedEncodersCache = ["libx265"],
            RecordingBenchmarkCache =
            [
                new RecordingBenchmarkCacheEntry
                {
                    SchemaVersion = 3,
                    Encoder = "libx265",
                    Width = 1920,
                    Height = 1080,
                    Fps = 15
                }
            ]
        };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        Assert.False(config.IsEncoderDetected);
        Assert.Empty(config.EncoderOptionsCache);
        Assert.Empty(config.ValidatedEncodersCache);
        Assert.Empty(config.RecordingBenchmarkCache);
        Assert.Equal("", config.EffectiveVideoEncoder);
        Assert.Equal(AppConfig.CurrentEncoderSelectionCacheVersion, config.EncoderDetectionCacheVersion);
    }

    [Fact]
    public void LegacyEncoderCacheWithoutVersion_IsAlsoClearedAndForcesRedetection()
    {
        var config = new AppConfig
        {
            IsEncoderDetected = true,
            EncoderDetectionCacheVersion = 0,
            EffectiveVideoEncoder = "libx265",
            ValidatedEncodersCache = ["libx265"]
        };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        Assert.False(config.IsEncoderDetected);
        Assert.Empty(config.ValidatedEncodersCache);
        Assert.Equal("", config.EffectiveVideoEncoder);
        Assert.Equal(AppConfig.CurrentEncoderSelectionCacheVersion, config.EncoderDetectionCacheVersion);
    }

    [Fact]
    public void EncoderDetectionCacheVersion_IsBumpedForUnifiedSelectionPolicy()
    {
        Assert.True(MainViewModel.CurrentEncoderDetectionCacheVersion >= 4);
    }

    [Fact]
    public void NvencDriverCompatibilityError_ProducesActionableWarningWithoutCpuFallbackClaim()
    {
        const string stderr = "Driver does not support the required nvenc API version. Required: 13.1 Found: 13.0 " +
                              "The minimum required Nvidia driver for nvenc is 610.0 or newer";

        NvencDriverCompatibilityIssue? issue = MainViewModel.ParseNvencDriverCompatibilityIssue(stderr);

        Assert.NotNull(issue);
        Assert.Equal("13.1", issue.RequiredApiVersion);
        Assert.Equal("13.0", issue.DetectedApiVersion);
        Assert.Equal("610.0", issue.MinimumDriverVersion);

        var config = new AppConfig();
        MainViewModel.UpdateEncoderDriverWarning(config, issue);
        string? message = MainViewModel.BuildEncoderDriverWarningMessage(config);

        Assert.Contains("需要 13.1", message, StringComparison.Ordinal);
        Assert.Contains("当前驱动仅提供 13.0", message, StringComparison.Ordinal);
        Assert.DoesNotContain("CPU", message, StringComparison.Ordinal);
    }

    private static EncodingHelper.EncoderCandidate Candidate(string encoder, double fps, bool qualifies)
    {
        return new EncodingHelper.EncoderCandidate(
            encoder,
            EncodingHelper.GetCodecFromEncoder(encoder),
            EncodingHelper.IsHardwareEncoder(encoder),
            fps,
            qualifies);
    }

    private static void CacheBenchmark(
        AppConfig config,
        string encoder,
        NativeCameraMode mode,
        double fps,
        DateTime testedAt)
    {
        RecordingProfileDetector.UpdateBenchmarkCache(
            config,
            encoder,
            config.VideoCqp,
            [new RealtimeEncodingBenchmarkResult(mode, true, 1, 180, fps, 1.2, "test")],
            testedAt);
    }
}
