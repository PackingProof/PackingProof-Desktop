using ExpressPackingMonitoring.Services;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class AppPatchDownloadServiceTests
{
    [Fact]
    public async Task ValidPatchIsPublishedForLauncherInstallation()
    {
        using var fixture = new AppPatchFixture();
        byte[] package = "valid-patch"u8.ToArray();
        fixture.AddRelease("1.2.3", "0.0.0", package);

        AppPatchPreparationResult result = await fixture.PrepareAsync("1.2.3");

        Assert.True(result.Status == AppPatchPreparationStatus.Ready, result.Message);
        Assert.Equal(
            package,
            File.ReadAllBytes(Path.Combine(
                fixture.PendingDirectory,
                "ExpressPackingMonitoring_AppPatch_v1.2.3.zip")));
        Assert.True(File.Exists(Path.Combine(fixture.PendingDirectory, "update_manifest.json")));
    }

    [Fact]
    public async Task VersionBelowBaselineUsesFullPackageWithoutDownloadingPatch()
    {
        using var fixture = new AppPatchFixture();
        fixture.AddRelease("1.2.3", "999.0.0", "unused"u8.ToArray());

        AppPatchPreparationResult result = await fixture.PrepareAsync("1.2.3");

        Assert.Equal(AppPatchPreparationStatus.FullPackageRequired, result.Status);
        Assert.Contains("低于增量更新基线", result.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.PendingDirectory));
    }

    [Fact]
    public async Task FailedHashValidationPreservesExistingPendingDirectory()
    {
        using var fixture = new AppPatchFixture();
        Directory.CreateDirectory(fixture.PendingDirectory);
        string sentinel = Path.Combine(fixture.PendingDirectory, "published-by-another-task.txt");
        File.WriteAllText(sentinel, "keep", Encoding.UTF8);
        fixture.AddRelease(
            "1.2.3",
            "0.0.0",
            "corrupt"u8.ToArray(),
            advertisedHash: new string('a', 64));

        AppPatchPreparationResult result = await fixture.PrepareAsync("1.2.3");

        Assert.Equal(AppPatchPreparationStatus.Failed, result.Status);
        Assert.Equal("keep", File.ReadAllText(sentinel, Encoding.UTF8));
    }

    [Fact]
    public async Task LaterFailedTaskPreservesEarlierValidPublishedPatch()
    {
        using var fixture = new AppPatchFixture();
        byte[] firstPackage = "first-valid-patch"u8.ToArray();
        fixture.AddRelease("1.2.3", "0.0.0", firstPackage);
        Assert.Equal(
            AppPatchPreparationStatus.Ready,
            (await fixture.PrepareAsync("1.2.3")).Status);

        fixture.AddRelease(
            "1.2.4",
            "0.0.0",
            "second-corrupt-patch"u8.ToArray(),
            advertisedHash: new string('b', 64));
        AppPatchPreparationResult second = await fixture.PrepareAsync("1.2.4");

        Assert.Equal(AppPatchPreparationStatus.Failed, second.Status);
        Assert.Equal(
            firstPackage,
            File.ReadAllBytes(Path.Combine(
                fixture.PendingDirectory,
                "ExpressPackingMonitoring_AppPatch_v1.2.3.zip")));
    }

    [Fact]
    public void UpdateManifestAssetPrefersVersionedNameAndRejectsHttp()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """
            {
              "assets": [
                { "name": "update.json", "browser_download_url": "https://example.com/update.json" },
                { "name": "update_v1.2.3.json", "browser_download_url": "https://example.com/versioned.json" },
                { "name": "update_v1.2.3.json", "browser_download_url": "http://example.com/insecure.json" }
              ]
            }
            """);

        Assert.Equal(
            "https://example.com/versioned.json",
            UpdateCheckService.ReadUpdateManifestAssetUrl(document.RootElement, "1.2.3"));
    }

    private sealed class AppPatchFixture : IDisposable
    {
        private const string ManifestBase = "https://updates.example/";
        private readonly string _root;
        private readonly RoutingHandler _handler = new();
        private readonly HttpClient _client;
        private readonly AppPatchDownloadService _service;

        internal AppPatchFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "packingproof-app-update-tests", Guid.NewGuid().ToString("N"));
            string appDirectory = Path.Combine(_root, "install", "app");
            Directory.CreateDirectory(appDirectory);
            File.WriteAllBytes(
                Path.Combine(_root, "install", "ExpressPackingMonitoring.exe"),
                "launcher"u8.ToArray());
            Directory.CreateDirectory(UpdatesDirectory);
            _client = new HttpClient(_handler);
            _service = new AppPatchDownloadService(_client, UpdatesDirectory, appDirectory);
        }

        internal string UpdatesDirectory => Path.Combine(_root, "updates");
        internal string PendingDirectory => Path.Combine(UpdatesDirectory, "pending");

        internal void AddRelease(
            string version,
            string baseline,
            byte[] package,
            string? advertisedHash = null)
        {
            string manifestUrl = ManifestBase + $"update-{version}.json";
            string packageUrl = ManifestBase + $"patch-{version}.zip";
            string hash = advertisedHash
                ?? Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
            string manifest =
                $$"""
                {
                  "latest_version": "{{version}}",
                  "patch_baseline_version": "{{baseline}}",
                  "patch_supported": true,
                  "full_download_page": "https://example.com/releases",
                  "patch_package": {
                    "type": "baseline_patch",
                    "url": "{{packageUrl}}",
                    "sha256": "{{hash}}",
                    "size": {{package.Length}}
                  }
                }
                """;
            _handler.Add(manifestUrl, Encoding.UTF8.GetBytes(manifest), "application/json");
            _handler.Add(packageUrl, package, "application/zip");
        }

        internal Task<AppPatchPreparationResult> PrepareAsync(string version)
        {
            return _service.PrepareAsync(new UpdateCheckResult
            {
                HasUpdate = true,
                LatestVersion = version,
                DownloadUrl = "https://example.com/releases",
                UpdateManifestUrl = ManifestBase + $"update-{version}.json"
            });
        }

        public void Dispose()
        {
            _client.Dispose();
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (byte[] Content, string ContentType)> _responses =
            new(StringComparer.OrdinalIgnoreCase);

        internal void Add(string url, byte[] content, string contentType)
        {
            _responses[url] = (content, contentType);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri != null
                && _responses.TryGetValue(request.RequestUri.AbsoluteUri, out var response))
            {
                var content = new ByteArrayContent(response.Content);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(response.ContentType);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content,
                    RequestMessage = request
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request
            });
        }
    }
}
