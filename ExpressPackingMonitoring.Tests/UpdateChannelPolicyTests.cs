using System.Net;
using System.Text;
using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 更新渠道：预览版（prerelease）默认不推给客户端，测试机用 UPDATE_ALLOW_PRERELEASE 打开；
/// 草稿永远不推。这里盯两件事：开关取值解析，以及客户端真的把开关落到了挑选逻辑上。
/// </summary>
public sealed class UpdateChannelPolicyTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" yes ", true)]
    [InlineData("on", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("off", false)]
    [InlineData("no", false)]
    [InlineData("maybe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AllowPrereleaseToggle_AcceptsOnlyExplicitTruthyValues(string? value, bool expected)
    {
        Assert.Equal(expected, UpdateChannelPolicy.ParseToggle(value));
    }

    /// <summary>
    /// 默认客户端只挑正式版；把预览版放开的客户端才挑得到列表里更高的预览版。
    /// </summary>
    [Fact]
    public async Task MetadataClient_SkipsPrereleaseUnlessClientAllowsIt()
    {
        using var handler = new PrereleaseReleaseHandler();
        using var http = new HttpClient(handler);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        var defaultClient = new UpdateMetadataClient(http);
        using ResolvedUpdateManifest stable = await defaultClient.FetchLatestManifestAsync(
            ["https://updates.example/releases/latest"],
            cancellationToken);
        Assert.Equal("999.0.75", stable.LatestVersion);

        var testMachine = new UpdateMetadataClient(http, allowPrerelease: true);
        using ResolvedUpdateManifest preview = await testMachine.FetchLatestManifestAsync(
            ["https://updates.example/releases/latest"],
            cancellationToken);
        Assert.Equal("999.0.76", preview.LatestVersion);
    }

    /// <summary>假发布源：列表里最新的一版是预览版，上一版是正式版。</summary>
    private sealed class PrereleaseReleaseHandler : HttpMessageHandler
    {
        private const string ReleaseList = """
            [
              {
                "tag_name": "v999.0.76",
                "prerelease": true,
                "assets": [
                  {
                    "name": "update_v999.0.76.json",
                    "browser_download_url": "https://updates.example/update_v999.0.76.json"
                  }
                ]
              },
              {
                "tag_name": "v999.0.75",
                "prerelease": false,
                "assets": [
                  {
                    "name": "update_v999.0.75.json",
                    "browser_download_url": "https://updates.example/update_v999.0.75.json"
                  }
                ]
              }
            ]
            """;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            string body = url.Contains("/releases", StringComparison.OrdinalIgnoreCase)
                ? ReleaseList
                : """{"latest_version":"999.0.75"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
