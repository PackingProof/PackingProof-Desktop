using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Helpers;
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
    public void AutoSelection_RequiresValidPerformanceScore()
    {
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hevc_nvenc" };
        Assert.False(EncodingHelper.TryResolveEncoder("auto", "h265", validated,
            new Dictionary<string, double>(), out _));
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
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["h264_nvenc"] = 100
        };

        Assert.False(EncodingHelper.IsExactSelectionAvailable("auto", "h265", validated, scores));
        Assert.True(EncodingHelper.IsExactSelectionAvailable("auto", "h264", validated, scores));
    }

    [Fact]
    public void EncoderDetectionCacheVersion_IsBumpedForEssentialsBaseline()
    {
        Assert.True(MainViewModel.CurrentEncoderDetectionCacheVersion >= 4);
    }

    [Fact]
    public void NewConfiguration_DefaultsToScoredHardwareAutoAndH265()
    {
        var config = new AppConfig();
        Assert.Equal("auto", config.GpuEncoder);
        Assert.Equal("h265", config.VideoCodec);
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
