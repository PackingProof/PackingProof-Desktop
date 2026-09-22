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
        Assert.Equal(ReleaseListUrls(), handler.Requests);
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

        Assert.Equal(ReleaseListUrls()[1], resolved.SourceUrl);
        Assert.Equal(githubManifest, resolved.ManifestUrl);
        Assert.Equal(
            [
                ReleaseListUrls()[0],
                giteeManifest,
                ReleaseListUrls()[1],
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

    private static string[] ReleaseListUrls() =>
        UpdateCheckOptions.ToReleaseListUrls(UpdateCheckOptions.ResolveUpdateCheckUrls(null, null)).ToArray();

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
            if (string.Equals(url, ReleaseListUrls()[0], StringComparison.OrdinalIgnoreCase))
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

            string manifestUrl = url == ReleaseListUrls()[0]
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
}
