using System.Net;
using System.Text;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class UpdateSourcePolicyTests
{
    [Fact]
    public void DefaultMetadataSources_PreferGiteeThenFallbackToCurrentGithubRepository()
    {
        IReadOnlyList<string> urls = UpdateCheckOptions.ResolveUpdateCheckUrls(null, null);

        Assert.Equal(UpdateCheckOptions.DefaultGiteeCheckUrl, urls[0]);
        Assert.Equal(UpdateCheckOptions.DefaultGithubCheckUrl, urls[1]);
        Assert.DoesNotContain("m-RNA", string.Join("\n", urls), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CustomPrivateMetadataSource_DoesNotLeakToPublicFallbackUnlessConfigured()
    {
        IReadOnlyList<string> urls = UpdateCheckOptions.ResolveUpdateCheckUrls(
            "https://updates.example/latest",
            null);

        Assert.Equal(["https://updates.example/latest"], urls);
    }

    [Fact]
    public async Task MetadataCheck_FallsBackToGithubWhenGiteeIsRateLimited()
    {
        using var handler = new MetadataHandler();
        using var client = new HttpClient(handler);
        var service = new UpdateCheckService(client);

        UpdateCheckResult result = await service.FetchLatestReleaseAsync(TestContext.Current.CancellationToken);

        Assert.True(result.HasUpdate);
        Assert.Equal("v999.0.0", result.LatestVersion);
        // 列表按页取：每个源的第一条请求都是列表的 page=1
        Assert.Equal(ReleaseListUrls().Select(url => $"{url}&page=1"), handler.Requests);
    }

    /// <summary>
    /// 只发 macOS 的版本不能推给 Windows：Windows 只认带更新清单（update_v*.json）的版本，
    /// 否则用户会看到"有新版本"却下到 DMG。
    /// </summary>
    [Fact]
    public async Task MetadataCheck_SkipsMacOnlyRelease()
    {
        using var handler = new MacOnlyNewestHandler();
        using var client = new HttpClient(handler);
        var service = new UpdateCheckService(client);

        UpdateCheckResult result = await service.FetchLatestReleaseAsync(TestContext.Current.CancellationToken);

        Assert.True(result.HasUpdate);
        Assert.Equal("v999.0.98", result.LatestVersion);
    }

    [Fact]
    public async Task ManifestCheck_FallsBackToGithubWhenGiteeManifestIsUnavailable()
    {
        const string giteeManifest = "https://gitee.example/update_v999.0.0.json";
        const string githubManifest = "https://github.example/update_v999.0.0.json";
        using var handler = new ManifestHandler(giteeManifest, githubManifest);
        using var client = new HttpClient(handler);
        var metadata = new UpdateMetadataClient(client);

        using ResolvedUpdateManifest resolved = await metadata.FetchLatestManifestAsync(
            [UpdateCheckOptions.DefaultGiteeCheckUrl, UpdateCheckOptions.DefaultGithubCheckUrl],
            TestContext.Current.CancellationToken);

        // SourceUrl 仍是策略生成的列表地址（翻页是在这一层内部做的），请求序列才是带 page=1 的
        Assert.Equal(ReleaseListUrls()[1], resolved.SourceUrl);
        Assert.Equal(githubManifest, resolved.ManifestUrl);
        Assert.Equal(
            [
                PagedReleaseListUrls()[0],
                giteeManifest,
                PagedReleaseListUrls()[1],
                githubManifest
            ],
            handler.Requests);
    }

    /// <summary>
    /// 启动器的自动更新同样不能被"只发 macOS 的版本"卡住：它也要按新到旧找带更新清单的版本，
    /// 否则 /releases/latest 落到只带 DMG 的版本上会一直报"Release 缺少 update_v*.json"。
    /// </summary>
    [Fact]
    public async Task ManifestCheck_SkipsMacOnlyNewestRelease()
    {
        using var handler = new MacOnlyNewestManifestHandler();
        using var client = new HttpClient(handler);
        var metadata = new UpdateMetadataClient(client);

        using ResolvedUpdateManifest resolved = await metadata.FetchLatestManifestAsync(
            [UpdateCheckOptions.DefaultGiteeCheckUrl, UpdateCheckOptions.DefaultGithubCheckUrl],
            TestContext.Current.CancellationToken);

        Assert.Equal("0.0.72", resolved.LatestVersion);
    }

    /// <summary>
    /// 发布附件在两个平台是同构路径，镜像只换主机；认不出的地址（API、安装页、其它站点）不给镜像。
    /// </summary>
    [Theory]
    [InlineData(
        "https://github.com/PackingProof/PackingProof-Desktop/releases/download/v0.0.75/update_v0.0.75.json",
        "https://gitee.com/PackingProof/PackingProof-Desktop/releases/download/v0.0.75/update_v0.0.75.json")]
    [InlineData(
        "https://gitee.com/PackingProof/PackingProof-Desktop/releases/download/v0.0.75/PackingProof_AppPatch_v0.0.75.zip",
        "https://github.com/PackingProof/PackingProof-Desktop/releases/download/v0.0.75/PackingProof_AppPatch_v0.0.75.zip")]
    public void ReleaseAssetUrl_MirrorsToTheOtherPlatform(string url, string expected)
    {
        Assert.Equal(expected, UpdateEndpointPolicy.DeriveMirrorDownloadUrl(url));
    }

    [Theory]
    [InlineData("https://api.github.com/repos/PackingProof/PackingProof-Desktop/releases?per_page=100")]
    [InlineData("https://github.com/PackingProof/PackingProof-Desktop/releases/tag/v0.0.75")]
    [InlineData("https://example.com/releases/download/v0.0.75/update_v0.0.75.json")]
    [InlineData("http://github.com/PackingProof/PackingProof-Desktop/releases/download/v0.0.75/update.json")]
    [InlineData("")]
    [InlineData(null)]
    public void NonReleaseAssetUrl_HasNoMirror(string? url)
    {
        Assert.Equal("", UpdateEndpointPolicy.DeriveMirrorDownloadUrl(url));
    }

    /// <summary>
    /// 现场回归（0.0.74 的店里机器）：检查落在 GitHub 之后，清单地址就是 github.com 的附件，
    /// 而附件域名在店里打不开 —— 必须自动改用 gitee.com 上同一 tag、同一文件名的清单，
    /// 否则整条更新断在"读清单"这一步（用户看到"没有连接/没有回应"）。
    /// </summary>
    [Fact]
    public async Task ManifestDownload_FallsBackToMirrorHostWhenGithubAssetsAreUnreachable()
    {
        using var handler = new UnreachableGithubAssetHandler();
        using var client = new HttpClient(handler);
        var metadata = new UpdateMetadataClient(client);

        using ResolvedUpdateManifest resolved = await metadata.FetchLatestManifestAsync(
            [UpdateCheckOptions.DefaultGithubCheckUrl],
            TestContext.Current.CancellationToken);

        Assert.Equal(UnreachableGithubAssetHandler.GiteeManifestUrl, resolved.ManifestUrl);
        Assert.Equal(
            [
                PagedReleaseListUrls()[1],
                UnreachableGithubAssetHandler.GithubManifestUrl,
                UnreachableGithubAssetHandler.GiteeManifestUrl
            ],
            handler.Requests);
    }

    private static string[] ReleaseListUrls() =>
        UpdateCheckOptions.ToReleaseListUrls(UpdateCheckOptions.ResolveUpdateCheckUrls(null, null)).ToArray();

    /// <summary>
    /// Gitee 默认是"旧 → 新"，最新版被挤到最后一页；请求里必须显式要"新 → 旧"，
    /// 这样第一页就是最新版（GitHub 本来就是新 → 旧，不重复加参数）。
    /// </summary>
    [Fact]
    public void GiteeReleaseListUrl_AsksNewestFirst_AndGithubUrlStaysUntouched()
    {
        IReadOnlyList<string> urls = UpdateCheckOptions.ToReleaseListUrls(
            UpdateCheckOptions.GetUpdateCheckUrls());

        string giteeList = UpdateCheckOptions.DefaultGiteeCheckUrl[..^"/latest".Length];
        string githubList = UpdateCheckOptions.DefaultGithubCheckUrl[..^"/latest".Length];
        Assert.Equal($"{giteeList}?per_page=100&direction=desc", urls[0]);
        Assert.Equal($"{githubList}?per_page=100", urls[1]);
    }

    /// <summary>列表请求的第一个 URL：策略生成的列表地址 + 第一页。</summary>
    private static string[] PagedReleaseListUrls() =>
        ReleaseListUrls().Select(url => $"{url}&page=1").ToArray();

    [Fact]
    public void PackageDownloads_PreferGithubThenSwitchToGiteeAtFailureThreshold()
    {
        PackageDownloadRoute initial = PackageDownloadRoutePolicy.Resolve(
            "https://github.example/patch.zip",
            "https://gitee.example/patch.zip",
            null,
            null,
            consecutiveGithubFailures: 0,
            fallbackThreshold: 3);
        PackageDownloadRoute fallback = PackageDownloadRoutePolicy.Resolve(
            "https://github.example/patch.zip",
            "https://gitee.example/patch.zip",
            null,
            null,
            consecutiveGithubFailures: 3,
            fallbackThreshold: 3);

        Assert.Equal(initial.GithubUrl, initial.SelectedUrl);
        Assert.False(initial.PreferGitee);
        Assert.Equal(fallback.GiteeUrl, fallback.SelectedUrl);
        Assert.True(fallback.PreferGitee);
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            Requests.Add(url);
            // 列表按页取，Gitee 那条请求会带上 &page=1，所以按前缀判断
            if (url.StartsWith(ReleaseListUrls()[0], StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    RequestMessage = request
                });
            }

            const string json =
                """
                [
                  {
                    "tag_name": "v999.0.0",
                    "name": "fallback",
                    "body": "",
                    "html_url": "https://github.com/PackingProof/PackingProof-Desktop/releases/tag/v999.0.0",
                    "assets": [
                      {
                        "name": "update_v999.0.0.json",
                        "browser_download_url": "https://github.example/update_v999.0.0.json"
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

    /// <summary>最新版本只发 macOS（只有 DMG），Windows 应该挑到带更新清单的那个版本。</summary>
    private sealed class MacOnlyNewestHandler : HttpMessageHandler
    {
        private const string json =
            """
            [
              {
                "tag_name": "v999.0.99",
                "name": "mac only",
                "assets": [
                  {
                    "name": "PackingProof-macOS-999.0.99.dmg",
                    "browser_download_url": "https://github.example/PackingProof-macOS-999.0.99.dmg"
                  }
                ]
              },
              {
                "tag_name": "v999.0.98",
                "name": "windows",
                "assets": [
                  {
                    "name": "update_v999.0.98.json",
                    "browser_download_url": "https://github.example/update_v999.0.98.json"
                  }
                ]
              }
            ]
            """;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    /// <summary>最新版本只有 DMG 时，启动器要挑到带 update_v*.json 的版本并读它的清单。</summary>
    private sealed class MacOnlyNewestManifestHandler : HttpMessageHandler
    {
        private const string ManifestUrl = "https://github.example/update_v0.0.72.json";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            string json = url == ManifestUrl
                ? """{"latest_version":"0.0.72"}"""
                : """
                  [
                    {
                      "tag_name": "v0.0.73",
                      "assets": [
                        {
                          "name": "PackingProof-macOS-0.0.73.dmg",
                          "browser_download_url": "https://github.example/PackingProof-macOS-0.0.73.dmg"
                        }
                      ]
                    },
                    {
                      "tag_name": "v0.0.72",
                      "assets": [
                        {
                          "name": "update_v0.0.72.json",
                          "browser_download_url": "https://github.example/update_v0.0.72.json"
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

    private sealed class ManifestHandler(string giteeManifest, string githubManifest)
        : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            Requests.Add(url);
            if (url == giteeManifest)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    RequestMessage = request
                });
            }
            if (url == githubManifest)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent("{\"latest_version\":\"999.0.0\"}", Encoding.UTF8, "application/json")
                });
            }

            // 列表按页取，Gitee 那条请求会带上 &page=1，所以按前缀判断
            string manifestUrl = url.StartsWith(ReleaseListUrls()[0], StringComparison.OrdinalIgnoreCase)
                ? giteeManifest
                : githubManifest;
            string release = $$"""
                [
                  {
                    "tag_name": "v999.0.0",
                    "assets": [
                      {
                        "name": "update_v999.0.0.json",
                        "browser_download_url": "{{manifestUrl}}"
                      }
                    ]
                  }
                ]
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(release, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>检查源可用、但更新清单挂在打不开的 github.com 附件域名上（现场就是这条）。</summary>
    private sealed class UnreachableGithubAssetHandler : HttpMessageHandler
    {
        internal const string GithubManifestUrl =
            "https://github.com/PackingProof/PackingProof-Desktop/releases/download/v999.0.0/update_v999.0.0.json";
        internal const string GiteeManifestUrl =
            "https://gitee.com/PackingProof/PackingProof-Desktop/releases/download/v999.0.0/update_v999.0.0.json";

        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            Requests.Add(url);

            if (url == GithubManifestUrl)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    RequestMessage = request
                });
            }

            if (url == GiteeManifestUrl)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        "{\"latest_version\":\"999.0.0\"}",
                        Encoding.UTF8,
                        "application/json")
                });
            }

            string release = $$"""
                [
                  {
                    "tag_name": "v999.0.0",
                    "assets": [
                      {
                        "name": "update_v999.0.0.json",
                        "browser_download_url": "{{GithubManifestUrl}}"
                      }
                    ]
                  }
                ]
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(release, Encoding.UTF8, "application/json")
            });
        }
    }
}
