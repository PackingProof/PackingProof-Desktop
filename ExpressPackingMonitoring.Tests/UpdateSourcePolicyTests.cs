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
        Assert.Equal(
            [UpdateCheckOptions.DefaultGiteeCheckUrl, UpdateCheckOptions.DefaultGithubCheckUrl],
            handler.Requests);
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

        Assert.Equal(UpdateCheckOptions.DefaultGithubCheckUrl, resolved.SourceUrl);
        Assert.Equal(githubManifest, resolved.ManifestUrl);
        Assert.Equal(
            [
                UpdateCheckOptions.DefaultGiteeCheckUrl,
                giteeManifest,
                UpdateCheckOptions.DefaultGithubCheckUrl,
                githubManifest
            ],
            handler.Requests);
    }

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
            if (string.Equals(url, UpdateCheckOptions.DefaultGiteeCheckUrl, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    RequestMessage = request
                });
            }

            const string json =
                """
                {
                  "tag_name": "v999.0.0",
                  "name": "fallback",
                  "body": "",
                  "html_url": "https://github.com/PackingProof/PackingProof-Desktop/releases/tag/v999.0.0",
                  "assets": []
                }
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

            string manifestUrl = url == UpdateCheckOptions.DefaultGiteeCheckUrl
                ? giteeManifest
                : githubManifest;
            string release = $$"""
                {
                  "tag_name": "v999.0.0",
                  "assets": [
                    {
                      "name": "update_v999.0.0.json",
                      "browser_download_url": "{{manifestUrl}}"
                    }
                  ]
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(release, Encoding.UTF8, "application/json")
            });
        }
    }
}
