using System.Net;
using System.Text;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 现场反馈：打开“接收预览版更新”后点检查更新，仍然说已是最新。两条都要守住：
/// 手动检查每次都真的去问（以前套了 300 秒去抖、直接返回上一次的缓存结果），
/// 而且用调用方给的渠道（设置页传当前开关状态，用户不用先保存）。
/// </summary>
public sealed class ManualUpdateCheckTests
{
    [Fact]
    public async Task ManualCheck_QueriesEveryTimeAndHonoursThePrereleaseSwitch()
    {
        using var handler = new ReleaseHandler();
        using var client = new HttpClient(handler);
        var service = new UpdateCheckService(client);

        UpdateCheckResult formal = await service.CheckManualAsync(
            allowPrerelease: false,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(formal.HasUpdate);
        int requestsAfterFormal = handler.Requests.Count;
        Assert.True(requestsAfterFormal > 0, "第一次手动检查没有真的发请求");

        // 立刻再点一次：必须真的再查一遍（以前这里会被去抖挡住，仍报“已是最新”）
        UpdateCheckResult preview = await service.CheckManualAsync(
            allowPrerelease: true,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(preview.HasUpdate);
        Assert.Equal("v999.0.0", preview.LatestVersion);
        Assert.True(
            handler.Requests.Count > requestsAfterFormal,
            "第二次手动检查又被缓存挡住了，没有真的查");
    }

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.AbsoluteUri ?? "");
            // 最新的那个是预览版，第二新的才是正式版——渠道开关决定挑哪个。
            const string json =
                """
                [
                  {
                    "tag_name": "v999.0.0",
                    "name": "preview",
                    "prerelease": true,
                    "body": "",
                    "html_url": "https://example.test/releases/tag/v999.0.0",
                    "assets": [
                      {
                        "name": "update_v999.0.0.json",
                        "browser_download_url": "https://example.test/update_v999.0.0.json"
                      }
                    ]
                  },
                  {
                    "tag_name": "v0.0.76",
                    "name": "formal",
                    "prerelease": false,
                    "body": "",
                    "html_url": "https://example.test/releases/tag/v0.0.76",
                    "assets": [
                      {
                        "name": "update_v0.0.76.json",
                        "browser_download_url": "https://example.test/update_v0.0.76.json"
                      }
                    ]
                  }
                ]
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
