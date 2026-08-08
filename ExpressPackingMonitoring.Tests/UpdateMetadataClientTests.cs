using System.Net;
using System.Text;
using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class UpdateMetadataClientTests
{
    [Theory]
    [InlineData(
        "https://api.github.com/repos/PackingProof/PackingProof-Desktop/releases/latest",
        "0.0.50",
        "https://api.github.com/repos/PackingProof/PackingProof-Desktop/releases/tags/v0.0.50")]
    [InlineData(
        "https://gitee.com/api/v5/repos/PackingProof/PackingProof-Desktop/releases/latest",
        "v0.0.50",
        "https://gitee.com/api/v5/repos/PackingProof/PackingProof-Desktop/releases/tags/v0.0.50")]
    public void DeriveReleaseByTagUrl_SupportsGithubAndGiteeLatestEndpoints(
        string sourceUrl,
        string targetVersion,
        string expected)
    {
        Assert.Equal(expected, UpdateMetadataClient.DeriveReleaseByTagUrl(sourceUrl, targetVersion));
    }

    [Theory]
    [InlineData("https://updates.example/latest", "0.0.50")]
    [InlineData("http://api.example/releases/latest", "0.0.50")]
    [InlineData("https://api.github.com/repos/a/b/releases", "0.0.50")]
    public void DeriveReleaseByTagUrl_RejectsUnsupportedOrInsecureSources(
        string sourceUrl,
        string targetVersion)
    {
        Assert.Equal("", UpdateMetadataClient.DeriveReleaseByTagUrl(sourceUrl, targetVersion));
    }

    [Fact]
    public async Task ResolveManifestForVersion_ReturnsTargetReleaseManifest()
    {
        const string sourceUrl = "https://api.github.com/repos/a/b/releases/latest";
        const string manifestUrl = "https://download.example/update_v9.0.0.json";
        using var handler = new TagHandler(sourceUrl, "9.0.0", manifestUrl);
        using var client = new HttpClient(handler);
        var metadata = new UpdateMetadataClient(client);

        using ResolvedUpdateManifest? resolved =
            await metadata.TryResolveManifestForVersionAsync(
                [sourceUrl],
                "9.0.0",
                TestContext.Current.CancellationToken);

        Assert.NotNull(resolved);
        Assert.Equal(sourceUrl, resolved.SourceUrl);
        Assert.Equal(manifestUrl, resolved.ManifestUrl);
        Assert.Equal("9.0.0", resolved.LatestVersion);
        Assert.Equal(
            [
                "https://api.github.com/repos/a/b/releases/tags/v9.0.0",
                manifestUrl
            ],
            handler.Requests);
    }

    [Fact]
    public async Task ResolveManifestForVersion_ReturnsNullWhenReleaseOrManifestIsMissing()
    {
        const string sourceUrl = "https://api.github.com/repos/a/b/releases/latest";
        using var handler = new TagHandler(sourceUrl, "9.0.0", "", missingManifest: true);
        using var client = new HttpClient(handler);
        var metadata = new UpdateMetadataClient(client);

        using ResolvedUpdateManifest? resolved =
            await metadata.TryResolveManifestForVersionAsync(
                [sourceUrl],
                "9.0.0",
                TestContext.Current.CancellationToken);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task ResolveManifestForVersion_FallsBackAcrossSources()
    {
        const string giteeUrl = "https://gitee.example/releases/latest";
        const string githubUrl = "https://api.github.com/repos/a/b/releases/latest";
        const string manifestUrl = "https://download.example/update_v9.0.0.json";
        using var handler = new TagHandler(githubUrl, "9.0.0", manifestUrl);
        using var client = new HttpClient(handler);
        var metadata = new UpdateMetadataClient(client);

        using ResolvedUpdateManifest? resolved =
            await metadata.TryResolveManifestForVersionAsync(
                [giteeUrl, githubUrl],
                "9.0.0",
                TestContext.Current.CancellationToken);

        Assert.NotNull(resolved);
        Assert.Equal(githubUrl, resolved.SourceUrl);
        Assert.Equal(manifestUrl, resolved.ManifestUrl);
    }

    private sealed class TagHandler : HttpMessageHandler
    {
        private readonly string _sourceUrl;
        private readonly string _tag;
        private readonly string _manifestUrl;
        private readonly bool _missingManifest;

        internal TagHandler(
            string sourceUrl,
            string tag,
            string manifestUrl,
            bool missingManifest = false)
        {
            _sourceUrl = sourceUrl;
            _tag = tag;
            _manifestUrl = manifestUrl;
            _missingManifest = missingManifest;
        }

        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            Requests.Add(url);
            if (url == _manifestUrl)
            {
                return Task.FromResult(_missingManifest
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = request,
                        Content = new StringContent(
                            "{\"latest_version\":\"9.0.0\",\"patch_baseline_version\":\"0.0.0\",\"patch_supported\":true}",
                            Encoding.UTF8,
                            "application/json")
                    });
            }

            if (string.Equals(url, _sourceUrl, StringComparison.OrdinalIgnoreCase)
                || string.Equals(url, _sourceUrl.Replace("/latest", $"/tags/v{_tag}"), StringComparison.OrdinalIgnoreCase))
            {
                string release = _manifestUrl.Length == 0
                    ? $$"""{"tag_name": "v{{_tag}}", "assets": []}"""
                    : $$"""
                      {
                        "tag_name": "v{{_tag}}",
                        "assets": [
                          {
                            "name": "update_v{{_tag}}.json",
                            "browser_download_url": "{{_manifestUrl}}"
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

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request
            });
        }
    }
}
