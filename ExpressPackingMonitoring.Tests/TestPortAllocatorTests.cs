using System.Net;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 测试自己也要有守卫：这组用例专门盯着"端口分配"这个踩过的坑 ——
/// 同一用例连着分配两个端口时不能拿到同一个（否则第二个 HttpListener.Start()
/// 会报"与机器上已有注册冲突"），需要自己起监听器的用例必须拿到"已经绑定好"的监听器。
/// </summary>
[Collection("Web server tests")]
public sealed class TestPortAllocatorTests
{
    [Fact]
    public void AllocatedPortsAreUnique()
    {
        int first = TestPortAllocator.GetFreeTcpPort();
        int second = TestPortAllocator.GetFreeTcpPort();
        int third = TestPortAllocator.GetFreeTcpPort();

        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);
        Assert.NotEqual(first, third);
    }

    [Fact]
    public void StartHttpListener_ReturnsAnAlreadyBoundListener()
    {
        int taken = TestPortAllocator.GetFreeTcpPort();

        using HttpListener listener = TestPortAllocator.StartHttpListener(out int port);

        Assert.True(listener.IsListening);
        Assert.NotEqual(taken, port);

        // 已经在监听：同一个前缀再绑一次必须失败，证明监听器是真绑上了而不是只发了个号码
        using var conflict = new HttpListener();
        conflict.Prefixes.Add($"http://127.0.0.1:{port}/");
        Assert.Throws<HttpListenerException>(() => conflict.Start());
    }
}
