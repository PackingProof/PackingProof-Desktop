using ExpressPackingMonitoring.Services.Extensions;
using System.Net;
using System.Security.Cryptography;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionMarketClientTests
{
    [Fact]
    public async Task CatalogFallsBackFromGiteeToSignedGithubCopy()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        byte[] catalog = await File.ReadAllBytesAsync(Fixture("catalog.v1.json"), cancellationToken);
        byte[] signature = await File.ReadAllBytesAsync(Fixture("catalog.v1.sig"), cancellationToken);
        var handler = new RouteHandler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            if (url.StartsWith(ExtensionMarketClient.GiteeRegistryBase, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return Bytes(url.EndsWith("catalog.v1.sig", StringComparison.Ordinal) ? signature : catalog);
        });
        using var client = new HttpClient(handler);
        string cache = CreateTemporaryDirectory();
        try
        {
            var market = new ExtensionMarketClient(client, cache);
            ExtensionMarketSession result = await market.LoadCatalogAsync(cancellationToken);
            Assert.False(result.IsCached);
            Assert.Equal("packingproof.qqbot", Assert.Single(result.Catalog.Extensions).Id);
            Assert.StartsWith(ExtensionMarketClient.GiteeRegistryBase, handler.Requests[0], StringComparison.Ordinal);
            Assert.Contains(handler.Requests, value => value.StartsWith(ExtensionMarketClient.GithubRegistryBase, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(cache, true);
        }
    }

    [Fact]
    public async Task PackageDownloadRetriesGithubWhenGiteeHashDoesNotMatch()
    {
        byte[] good = "good"u8.ToArray();
        byte[] bad = "fail"u8.ToArray();
        var handler = new RouteHandler(request =>
            Bytes(request.RequestUri!.Host.Contains("gitee", StringComparison.OrdinalIgnoreCase) ? bad : good));
        using var client = new HttpClient(handler);
        string cache = CreateTemporaryDirectory();
        try
        {
            var market = new ExtensionMarketClient(client, cache);
            var release = new ExtensionMarketRelease
            {
                Version = "1.0.0",
                Size = good.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(good)).ToLowerInvariant(),
                Downloads = new ExtensionMarketDownloads
                {
                    Gitee = new ExtensionMarketDownload { Provider = "gitee", Url = "https://gitee.com/example/demo.ppext" },
                    Github = new ExtensionMarketDownload { Provider = "github", Url = "https://github.com/example/demo.ppext" }
                }
            };
            CancellationToken cancellationToken = TestContext.Current.CancellationToken;
            string path = await market.DownloadPackageAsync(release, cancellationToken: cancellationToken);
            Assert.Equal(good, await File.ReadAllBytesAsync(path, cancellationToken));
            Assert.Equal("gitee.com", new Uri(handler.Requests[0]).Host);
            Assert.Equal("github.com", new Uri(handler.Requests[1]).Host);
        }
        finally
        {
            Directory.Delete(cache, true);
        }
    }

    [Fact]
    public async Task SignedCatalogRejectsChangedBytes()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        byte[] catalog = await File.ReadAllBytesAsync(Fixture("catalog.v1.json"), cancellationToken);
        byte[] signature = await File.ReadAllBytesAsync(Fixture("catalog.v1.sig"), cancellationToken);
        catalog[10] ^= 1;
        Assert.Throws<InvalidDataException>(() => ExtensionMarketClient.VerifyCatalog(catalog, signature));
    }

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private static string Fixture(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ExtensionMarket", fileName);

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "packingproof-market-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(route(request));
        }
    }
}
