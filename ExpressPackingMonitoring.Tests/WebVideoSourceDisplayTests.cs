using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class WebVideoSourceDisplayTests
{
    [Theory]
    [InlineData("pc", "", "", "", "电脑1", "电脑1")]
    [InlineData("pc", "", "旧机器名", "pc", "电脑2", "电脑2")]
    [InlineData("pc", "", "旧机器名", "pc", "", "旧机器名")]
    [InlineData("external", "phone-id", "手机3", "mobile", "电脑1", "手机3")]
    [InlineData("external", "pc-id", "电脑4", "pc", "电脑1", "电脑4")]
    [InlineData("external", "", "", "pc", "电脑1", "电脑设备")]
    [InlineData("external", "", "", "mobile", "电脑1", "手机设备")]
    public void SourceDisplayNameUsesCurrentHostNicknameAndPreservesRemoteNickname(
        string sourceType,
        string deviceId,
        string deviceName,
        string deviceKind,
        string localNodeName,
        string expected)
    {
        Assert.Equal(
            expected,
            WebServer.ResolveVideoSourceDisplayName(
                sourceType,
                deviceId,
                deviceName,
                deviceKind,
                localNodeName));
    }
}
