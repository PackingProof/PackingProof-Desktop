using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class EncodingEssentialsTests
{
    [Fact]
    public void AutoH265_SelectsHighestScoredHardwareH265()
    {
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hevc_nvenc", "hevc_qsv"
        };
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["hevc_nvenc"] = 120,
            ["hevc_qsv"] = 175
        };

        Assert.True(EncodingHelper.TryResolveEncoder("auto", "h265", validated, scores, out string encoder));
        Assert.Equal("hevc_qsv", encoder);
    }

    [Fact]
    public void AutoH265_UsesHighestScoredHardwareH264WhenNoHardwareH265Exists()
    {
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "libx265", "h264_nvenc", "h264_amf"
        };
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["h264_nvenc"] = 90,
            ["h264_amf"] = 130
        };

        Assert.True(EncodingHelper.TryResolveEncoder("auto", "h265", validated, scores, out string encoder));
        Assert.Equal("h264_amf", encoder);
    }

    [Fact]
    public void ExplicitHardwareH265_DoesNotFallBackToCpuH265()
    {
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "libx265", "h264_nvenc"
        };

        Assert.False(EncodingHelper.TryResolveEncoder("nvidia", "h265", validated,
            new Dictionary<string, double> { ["h264_nvenc"] = 100 }, out string encoder));
        Assert.Empty(encoder);
    }

    [Fact]
    public void ExplicitCpuH265_IsAllowedOnlyWhenValidated()
    {
        Assert.True(EncodingHelper.TryResolveEncoder("cpu", "h265",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "libx265" }, null, out string encoder));
        Assert.Equal("libx265", encoder);
    }

    [Fact]
    public void AutoSelection_FallsBackToValidatedHardwareWithoutScore()
    {
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hevc_nvenc" };
        Assert.True(EncodingHelper.TryResolveEncoder("auto", "h265", validated,
            new Dictionary<string, double>(), out string encoder));
        Assert.Equal("hevc_nvenc", encoder);
    }

    [Fact]
    public void AutoSelection_FallsBackToValidatedCpuWhenNoHardwareIsAvailable()
    {
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "libx264"
        };

        Assert.True(EncodingHelper.TryResolveEncoder(
            "auto",
            "h264",
            validated,
            new Dictionary<string, double>(),
            out string encoder));
        Assert.Equal("libx264", encoder);
    }

    [Fact]
    public void AutoSelection_ExcludesHardwareThatDidNotCompleteBenchmark()
    {
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hevc_nvenc", "libx265"
        };
        var autoEligible = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "libx265"
        };

        Assert.True(EncodingHelper.TryResolveEncoder(
            "auto",
            "h265",
            validated,
            new Dictionary<string, double>(),
            out string encoder,
            autoEligible));
        Assert.Equal("libx265", encoder);
    }

    [Fact]
    public void AutoAv1_FallsBackToH265BeforeH264()
    {
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "libx265", "libx264"
        };

        Assert.True(EncodingHelper.TryResolveEncoder(
            "auto",
            "av1",
            validated,
            new Dictionary<string, double>(),
            out string encoder));
        Assert.Equal("libx265", encoder);
    }

    [Fact]
    public void AutoAv1_PrefersH265CpuBeforeH264Hardware()
    {
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "h264_nvenc", "libx265"
        };

        Assert.True(EncodingHelper.TryResolveEncoder(
            "auto",
            "av1",
            validated,
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["h264_nvenc"] = 120
            },
            out string encoder));
        Assert.Equal("libx265", encoder);
    }

    [Fact]
    public void ExplicitCpuAv1_FallsBackToH265()
    {
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "libx265"
        };

        Assert.True(EncodingHelper.TryResolveEncoder(
            "cpu",
            "av1",
            validated,
            new Dictionary<string, double>(),
            out string encoder));
        Assert.Equal("libx265", encoder);
    }

    [Fact]
    public void ExplicitHardwareSelection_DoesNotRequirePerformanceScore()
    {
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "h264_qsv", "hevc_qsv"
        };

        Assert.True(EncodingHelper.TryResolveEncoder(
            "intel",
            "h264",
            validated,
            new Dictionary<string, double>(),
            out string encoder));
        Assert.Equal("h264_qsv", encoder);
        Assert.True(EncodingHelper.IsExactSelectionAvailable(
            "intel",
            "h264",
            validated));
    }

    [Fact]
    public void ValidatedHardware_IsListedEvenWhenScoringFails()
    {
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "libx264", "h264_qsv", "hevc_qsv"
        };

        List<GpuEncoderOption> options = MainViewModel.BuildGpuEncoderOptions(
            validated);

        Assert.Contains(options, option => option.Value == "auto");
        Assert.Contains(options, option => option.Value == "intel");
        Assert.Contains(options, option => option.Value == "cpu");
    }

    [Theory]
    [InlineData((int)RealtimeEncodingBenchmarkFailureKind.None, true)]
    [InlineData((int)RealtimeEncodingBenchmarkFailureKind.Timeout, true)]
    [InlineData((int)RealtimeEncodingBenchmarkFailureKind.Incomplete, true)]
    [InlineData((int)RealtimeEncodingBenchmarkFailureKind.HardFailure, false)]
    public void EncoderOptionVisibilityDistinguishesTransientAndHardFailures(
        int failureKindValue,
        bool expected)
    {
        var failureKind =
            (RealtimeEncodingBenchmarkFailureKind)failureKindValue;
        var benchmark = new RealtimeEncodingBenchmarkResult(
            new NativeCameraMode(1280, 720, 15),
            CompletedSuccessfully:
                failureKind == RealtimeEncodingBenchmarkFailureKind.None,
            ElapsedSeconds: 1,
            EncodedFrames:
                failureKind == RealtimeEncodingBenchmarkFailureKind.None ? 180 : 0,
            MeasuredEncodingFps:
                failureKind == RealtimeEncodingBenchmarkFailureKind.None ? 180 : 0,
            RequiredHeadroom: RecordingProfileDetector.RequiredEncodingSpeed,
            Detail: "",
            FailureKind: failureKind);

        Assert.Equal(
            expected,
            MainViewModel.ShouldExposeEncoderAfterBenchmark(benchmark));
    }

    [Fact]
    public void EncoderDetectionMergeKeepsKnownCapabilitiesWhenRefreshDegrades()
    {
        EncoderPerformanceCacheEntry timedOutHardware = new()
        {
            Encoder = "h264_qsv",
            CompletedSuccessfully = false,
            Detail = "timeout"
        };
        var detection = new EncoderDetectionResult(
            Options: [],
            ValidatedEncoders:
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "libx264"
                },
            AutoEligibleEncoders:
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "libx264"
                },
            PerformanceScores:
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            PerformanceResults: [],
            FfmpegAvailable: true,
            NvencDriverIssue: null,
            EncoderListReadSucceeded: false);
        Assert.False(detection.CacheCanBeReused);

        EncoderDetectionResult merged = MainViewModel.MergeWithKnownEncoders(
            detection,
            ["h264_qsv", "hevc_qsv"],
            ["hevc_qsv"],
            [timedOutHardware],
            new Dictionary<string, double>());

        Assert.Contains("h264_qsv", merged.ValidatedEncoders);
        Assert.DoesNotContain("h264_qsv", merged.AutoEligibleEncoders);
        Assert.Contains("hevc_qsv", merged.AutoEligibleEncoders);
        Assert.Contains(
            merged.PerformanceResults,
            entry => entry.Encoder == "h264_qsv"
                && !entry.CompletedSuccessfully);
        Assert.Contains(
            merged.Options,
            option => option.Value == "intel");
        Assert.Contains(merged.Options, option => option.Value == "auto");
    }

    [Fact]
    public void EncoderDetectionMergeDropsPreviouslyKnownEncoderNoLongerExposed()
    {
        var detection = new EncoderDetectionResult(
            Options: [],
            ValidatedEncoders:
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "libx264"
                },
            AutoEligibleEncoders:
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "libx264"
                },
            PerformanceScores:
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            PerformanceResults: [],
            FfmpegAvailable: true,
            NvencDriverIssue: null,
            EncoderListReadSucceeded: true);
        Assert.True(detection.CacheCanBeReused);

        EncoderDetectionResult merged = MainViewModel.MergeWithKnownEncoders(
            detection,
            ["h264_qsv", "hevc_qsv"],
            ["hevc_qsv"],
            [],
            new Dictionary<string, double>());

        Assert.DoesNotContain("h264_qsv", merged.ValidatedEncoders);
        Assert.DoesNotContain("hevc_qsv", merged.AutoEligibleEncoders);
        Assert.DoesNotContain(
            merged.Options,
            option => option.Value == "intel");
    }

    [Fact]
    public void EqualScores_UseStableEncoderNameTieBreak()
    {
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hevc_nvenc", "hevc_amf" };
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["hevc_nvenc"] = 100,
            ["hevc_amf"] = 100
        };
        Assert.True(EncodingHelper.TryResolveEncoder("auto", "h265", validated, scores, out string encoder));
        Assert.Equal("hevc_amf", encoder);
    }

    [Fact]
    public void ExactUiAvailability_DoesNotPresentH265WhenOnlyHardwareH264Exists()
    {
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264_nvenc" };

        Assert.False(EncodingHelper.IsExactSelectionAvailable("auto", "h265", validated));
        Assert.True(EncodingHelper.IsExactSelectionAvailable("auto", "h264", validated));
    }

    [Fact]
    public void EncoderDetectionCacheVersion_IsBumpedForEssentialsBaseline()
    {
        Assert.Equal(6, MainViewModel.CurrentEncoderDetectionCacheVersion);
    }

    [Fact]
    public void NewConfiguration_DefaultsToScoredHardwareAutoAndH264()
    {
        var config = new AppConfig();
        Assert.Equal("auto", config.GpuEncoder);
        Assert.Equal("h264", config.VideoCodec);
    }

    [Fact]
    public void NvencDriverCompatibilityError_ProducesActionableWarning()
    {
        const string stderr = "Driver does not support the required nvenc API version. Required: 13.1 Found: 13.0 " +
                              "The minimum required Nvidia driver for nvenc is 610.0 or newer";

        NvencDriverCompatibilityIssue? issue =
            MainViewModel.ParseNvencDriverCompatibilityIssue(stderr);

        Assert.NotNull(issue);
        Assert.Equal("13.1", issue.RequiredApiVersion);
        Assert.Equal("13.0", issue.DetectedApiVersion);
        Assert.Equal("610.0", issue.MinimumDriverVersion);

        var config = new AppConfig { GpuEncoder = "nvidia" };
        MainViewModel.UpdateEncoderDriverWarning(config, issue);
        string? message = MainViewModel.BuildEncoderDriverWarningMessage(config);

        Assert.Contains("需要 13.1", message, StringComparison.Ordinal);
        Assert.Contains("当前驱动仅提供 13.0", message, StringComparison.Ordinal);
        Assert.Contains("610.0", message, StringComparison.Ordinal);
        Assert.False(message?.Contains("自动改用 CPU", StringComparison.Ordinal) == true);
        Assert.Contains("选择其他可用编码器", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("No capable devices found")]
    [InlineData("Cannot load nvcuda.dll")]
    [InlineData("")]
    public void NonDriverVersionEncoderFailure_DoesNotClaimDriverIsTooOld(string stderr)
    {
        Assert.Null(MainViewModel.ParseNvencDriverCompatibilityIssue(stderr));
    }

    [Fact]
    public void NvencDriverWarning_IsOnlyShownForExplicitNvidiaSelection()
    {
        var config = new AppConfig { GpuEncoder = "auto" };
        MainViewModel.UpdateEncoderDriverWarning(
            config,
            new NvencDriverCompatibilityIssue("13.1", "13.0", "610.0"));

        Assert.Null(MainViewModel.BuildEncoderDriverWarningMessage(config));
    }
}
