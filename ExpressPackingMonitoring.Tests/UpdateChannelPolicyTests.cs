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
    /// 环境变量优先于应用设置：认得出就用它（"0" 是明确的关），认不出才回退到设置里的开关。
    /// </summary>
    [Theory]
    [InlineData("1", false, true)]
    [InlineData("true", false, true)]
    [InlineData("0", true, false)]
    [InlineData("false", true, false)]
    [InlineData("off", true, false)]
    [InlineData("", true, true)]
    [InlineData(null, true, true)]
    [InlineData("maybe", true, true)]
    [InlineData(null, false, false)]
    public void EnvironmentVariableTakesPrecedenceOverAppSetting(
        string? environmentValue,
        bool appConfigValue,
        bool expected)
    {
        Assert.Equal(
            expected,
            UpdateChannelPolicy.ResolveAllowPrerelease(environmentValue, appConfigValue));
    }

    /// <summary>应用配置里的"接收预览版更新"：读不到、坏了、字段不是布尔都当关闭。</summary>
    [Theory]
    [InlineData("{\"AllowPrereleaseUpdates\":true}", true)]
    [InlineData("{\"AllowPrereleaseUpdates\":false}", false)]
    [InlineData("{\"EnableAutoCheckUpdate\":true}", false)]
    [InlineData("{\"AllowPrereleaseUpdates\":\"true\"}", false)]
    [InlineData("not-json", false)]
    public void AppConfigToggle_ReadsOnlyBooleanTrue(string json, bool expected)
    {
        string path = Path.Combine(Path.GetTempPath(), $"eppm-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json, Encoding.UTF8);
        try
        {
            Assert.Equal(expected, UpdateChannelPolicy.AllowPrereleaseFromAppConfig(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AppConfigToggle_MissingFileFallsBackToOff()
    {
        string path = Path.Combine(Path.GetTempPath(), $"eppm-missing-{Guid.NewGuid():N}.json");

        Assert.False(UpdateChannelPolicy.AllowPrereleaseFromAppConfig(path));
    }

    /// <summary>启动器读 EnableAutoCheckUpdate 走的是同一个实现：默认开，配置里关掉才关。</summary>
    [Fact]
    public void AppConfigToggle_SupportsLauncherAutoCheckSwitch()
    {
        string path = Path.Combine(Path.GetTempPath(), $"eppm-auto-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"EnableAutoCheckUpdate\":false}", Encoding.UTF8);
        try
        {
            Assert.False(UpdateChannelPolicy.ReadAppConfigToggle(
                path, "EnableAutoCheckUpdate", fallback: true));
            Assert.True(UpdateChannelPolicy.ReadAppConfigToggle(
                path, "NotThere", fallback: true));
        }
        finally
        {
            File.Delete(path);
        }
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
