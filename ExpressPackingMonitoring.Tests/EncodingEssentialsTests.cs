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
}
