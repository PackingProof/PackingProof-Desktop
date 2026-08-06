using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class NetworkCameraConfigurationTests
{
    [Theory]
    [InlineData("rtsp://192.168.1.10:554/stream", true)]
    [InlineData("rtsp://admin:pass@192.168.1.10/stream", true)]
    [InlineData("rtmp://192.168.1.10/live/stream", true)]
    [InlineData("http://192.168.1.10:8080/video.mjpg", true)]
    [InlineData("https://example.com/stream", true)]
    [InlineData("file:///C:/temp/a.mkv", false)]
    [InlineData("ftp://192.168.1.10/file", false)]
    [InlineData("192.168.1.10/stream", false)]
    [InlineData("", false)]
    [InlineData("rtsp:///missing-host", false)]
    public void TryNormalize_ValidatesSchemeAndHost(string url, bool expected)
    {
        bool ok = NetworkCameraUrlPolicy.TryNormalize(url, out string normalized, out _);
        Assert.Equal(expected, ok);
        if (ok)
            Assert.Equal(url.Trim(), normalized);
    }

    [Fact]
    public void SanitizeForLog_MasksPasswordAndKeepsHost()
    {
        string sanitized = NetworkCameraUrlPolicy.SanitizeForLog(
            "rtsp://admin:secret@192.168.1.10:554/stream?user=admin");

        Assert.DoesNotContain("secret", sanitized);
        Assert.Contains("admin:***@192.168.1.10", sanitized);
        Assert.Contains("/stream", sanitized);
    }

    [Fact]
    public void SanitizeForLog_KeepsUrlWithoutCredentials()
    {
        Assert.Equal(
            "rtsp://192.168.1.10:554/stream",
            NetworkCameraUrlPolicy.SanitizeForLog("rtsp://192.168.1.10:554/stream"));
    }

    [Fact]
    public void NormalizeAfterLoad_MigratesEmptyKindToNetworkWhenUrlPresent()
    {
        var config = new AppConfig { CameraSourceKind = "", NetworkCameraUrl = " rtsp://192.168.1.10/stream " };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        Assert.Equal("network", config.CameraSourceKind);
        Assert.Equal("rtsp://192.168.1.10/stream", config.NetworkCameraUrl);
        Assert.Equal("tcp", config.NetworkCameraRtspTransport);
    }

    [Fact]
    public void NormalizeAfterLoad_KeepsUsbForExistingConfigs()
    {
        var config = new AppConfig { CameraSourceKind = "", NetworkCameraUrl = "" };

        AppConfig.NormalizeAfterLoad(config);
        Assert.Equal("usb", config.CameraSourceKind);
    }

    [Fact]
    public void NormalizeAfterLoad_NormalizesTransport()
    {
        var config = new AppConfig
        {
            CameraSourceKind = "network",
            NetworkCameraUrl = "rtsp://192.168.1.10/stream",
            NetworkCameraRtspTransport = "UDP"
        };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        Assert.Equal("udp", config.NetworkCameraRtspTransport);
    }

    [Fact]
    public void GetCameraConfigKey_UsesNetworkPrefixForNetworkSource()
    {
        Assert.Equal(
            "network:rtsp://192.168.1.10/stream",
            AppConfig.GetCameraConfigKey("network", "rtsp://192.168.1.10/stream"));
        Assert.Equal(
            "@device_pnp:\\\\usb",
            AppConfig.GetCameraConfigKey("usb", "@device_pnp:\\\\usb"));
    }
}
