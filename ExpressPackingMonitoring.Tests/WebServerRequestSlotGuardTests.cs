using System;
using System.IO;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 守卫：请求槽位（并发限流）与“请求都结束了”事件必须覆盖整个请求处理过程。
/// 长轮询改成异步等待后，处理函数变成 async Task，调用处一旦不 await，
/// 槽位会在第一个 await 就释放：限流失效，关服时还会在请求没跑完就释放服务器资源。
/// </summary>
public sealed class WebServerRequestSlotGuardTests
{
    [Fact]
    public void RequestDispatch_AwaitsHandlerInsideTheRequestSlot()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "ExpressPackingMonitoring",
            "Services",
            "WebServer.cs");
        string source = File.ReadAllText(path, System.Text.Encoding.UTF8);

        Assert.Contains("await HandleRequest(ctx)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("try { HandleRequest(ctx); }", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(startPath));
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }
}
