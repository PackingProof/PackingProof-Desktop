using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class EncodingEssentialsTests
{
    [Fact]
    public void Av1WithoutValidatedHardware_FallsBackToCpuH265()
    {
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "libx264",
            "libx265"
        };

        Assert.Equal("libx265", EncodingHelper.ResolveFallbackEncoder("auto", "av1", validated));
        Assert.Equal("libx265", EncodingHelper.GetCpuFallbackEncoder("av1"));
    }

    [Theory]
    [InlineData("av1_nvenc")]
    [InlineData("av1_amf")]
    [InlineData("av1_qsv")]
    public void Av1WithValidatedHardware_PreservesAv1(string encoder)
    {
        var validated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "libx264",
            "libx265",
            encoder
        };

        Assert.Equal(encoder, EncodingHelper.ResolveFallbackEncoder("auto", "av1", validated));
    }

    [Fact]
    public void UnsupportedAv1Configuration_IsPersistentlyMigratedToH265Auto()
    {
        var config = new AppConfig
        {
            VideoCodec = "av1",
            GpuEncoder = "cpu"
        };

        bool changed = EncodingHelper.ApplyUnsupportedAv1Fallback(
            config,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "libx264", "libx265" });

        Assert.True(changed);
        Assert.Equal("h265", config.VideoCodec);
        Assert.Equal("auto", config.GpuEncoder);
    }

    [Fact]
    public void ValidatedHardwareAv1Configuration_IsNotMigrated()
    {
        var config = new AppConfig
        {
            VideoCodec = "av1",
            GpuEncoder = "nvidia"
        };

        bool changed = EncodingHelper.ApplyUnsupportedAv1Fallback(
            config,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "av1_nvenc" });

        Assert.False(changed);
        Assert.Equal("av1", config.VideoCodec);
        Assert.Equal("nvidia", config.GpuEncoder);
    }

    [Fact]
    public void EncoderDetectionCacheVersion_IsBumpedForEssentialsBaseline()
    {
        Assert.True(MainViewModel.CurrentEncoderDetectionCacheVersion >= 2);
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
        Assert.Contains("已自动改用 CPU 软编码", message, StringComparison.Ordinal);
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
