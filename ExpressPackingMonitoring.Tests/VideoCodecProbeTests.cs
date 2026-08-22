using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VideoCodecProbeTests
{
    [Theory]
    [InlineData("Stream #0:0: Video: h264 (High), yuv420p", "h264")]
    [InlineData("Stream #0:0: Video: hevc (Main), yuv420p", "h265")]
    [InlineData("Stream #0:0: Video: av1 (Main), yuv420p", "av1")]
    [InlineData("Stream #0:0: Audio: aac, 48000 Hz", "")]
    public void ParseReadsVideoCodecWithoutScanningFrames(string output, string expected)
    {
        Assert.Equal(expected, VideoCodecProbe.Parse(output));
    }

    [Theory]
    [InlineData("h264", "h264")]
    [InlineData("HEVC", "h265")]
    [InlineData("av1", "av1")]
    [InlineData("vp9", "")]
    [InlineData(null, "")]
    public void UploadCodecNormalizationAcceptsOnlySupportedValues(string? value, string expected)
    {
        Assert.Equal(expected, MobileBackupService.NormalizeVideoCodec(value));
    }
}
