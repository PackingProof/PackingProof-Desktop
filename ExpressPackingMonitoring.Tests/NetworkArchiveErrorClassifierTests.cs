using System.ComponentModel;
using System.IO;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class NetworkArchiveErrorClassifierTests
{
    [Theory]
    [InlineData(53)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(67)]
    [InlineData(1231)]
    [InlineData(1232)]
    public void IOException_NetworkErrorCodes_AreUnreachable(int code)
    {
        var ex = new IOException(
            "network failure",
            unchecked((int)(0x80070000u | (uint)code)));

        Assert.True(NetworkArchiveErrorClassifier.IsTargetUnreachable(ex));
    }

    [Fact]
    public void IOException_DiskFullCode_IsNotUnreachable()
    {
        var ex = new IOException("磁盘空间不足", 112);

        Assert.False(NetworkArchiveErrorClassifier.IsTargetUnreachable(ex));
    }

    [Fact]
    public void PlainIOException_IsNotUnreachable()
    {
        var ex = new IOException("无法探测网络目标状态");

        Assert.False(NetworkArchiveErrorClassifier.IsTargetUnreachable(ex));
    }

    [Fact]
    public void DirectoryNotFoundException_IsUnreachable()
    {
        var ex = new DirectoryNotFoundException("找不到网络路径");

        Assert.True(NetworkArchiveErrorClassifier.IsTargetUnreachable(ex));
    }

    [Fact]
    public void Win32ExceptionInChain_IsUnreachable()
    {
        var ex = new IOException(
            "outer message",
            new Win32Exception(53, "找不到网络路径"));

        Assert.True(NetworkArchiveErrorClassifier.IsTargetUnreachable(ex));
    }
}
