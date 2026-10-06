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
                "PackingProof_AppPatch_v1.2.3.zip")));
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
        Assert.Equal("https://backup.example/releases", result.FullDownloadFallbackUrl);
        Assert.False(Directory.Exists(fixture.PendingDirectory));
    }

    [Fact]
    public async Task BelowNewBaseline_WithUsableBaselinePatch_PreparesStepUpInsteadOfFullPackage()
    {
        using var fixture = new AppPatchFixture();
        byte[] baselinePackage = "baseline-step-patch"u8.ToArray();
        fixture.AddRelease("9.9.9", "9.0.0", "latest-patch"u8.ToArray());
        fixture.AddBaselineRelease("9.0.0", "0.0.0", baselinePackage);

        AppPatchPreparationResult result = await fixture.PrepareAsync("9.9.9");

        Assert.Equal(AppPatchPreparationStatus.Ready, result.Status);
        Assert.Contains("先升级到基线版本 9.0.0", result.Message, StringComparison.Ordinal);
        Assert.Contains("继续升级到最新版本 9.9.9", result.Message, StringComparison.Ordinal);
        Assert.Equal(
            baselinePackage,
            File.ReadAllBytes(Path.Combine(
                fixture.PendingDirectory,
                "PackingProof_AppPatch_v9.0.0.zip")));
        Assert.Contains(fixture.BaselineTagUrl("9.0.0"), fixture.Requests);
    }

    [Fact]
    public async Task BelowNewBaseline_WithoutUsableBaselinePatch_StillRequiresFullPackage()
    {
        using var fixture = new AppPatchFixture();
        fixture.AddRelease("9.9.9", "9.0.0", "latest-patch"u8.ToArray());

        AppPatchPreparationResult result = await fixture.PrepareAsync("9.9.9");

        Assert.Equal(AppPatchPreparationStatus.FullPackageRequired, result.Status);
        Assert.Contains("未找到可先升级到基线版本的增量包", result.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.PendingDirectory));
    }

    [Fact]
    public async Task BelowNewBaseline_WalksBackMultipleBaselineHops_ToNearestReachableStep()
    {
        using var fixture = new AppPatchFixture();
        byte[] firstHopPackage = "first-hop-patch"u8.ToArray();
        fixture.AddRelease("9.9.9", "9.0.0", "latest-patch"u8.ToArray());
        fixture.AddBaselineRelease("9.0.0", "8.0.0", "second-hop-patch"u8.ToArray());
        fixture.AddBaselineRelease("8.0.0", "0.0.0", firstHopPackage);

        AppPatchPreparationResult result = await fixture.PrepareAsync("9.9.9");

        Assert.Equal(AppPatchPreparationStatus.Ready, result.Status);
        Assert.Contains("先升级到基线版本 8.0.0", result.Message, StringComparison.Ordinal);
        Assert.Equal(
            firstHopPackage,
            File.ReadAllBytes(Path.Combine(
                fixture.PendingDirectory,
                "PackingProof_AppPatch_v8.0.0.zip")));
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
                "PackingProof_AppPatch_v1.2.3.zip")));
    }

    [Fact]
    public async Task GithubPatchFailure_FallsBackToGiteePackage()
    {
        using var fixture = new AppPatchFixture();
        byte[] package = "gitee-fallback-patch"u8.ToArray();
        (string githubUrl, string giteeUrl) = fixture.AddDualSourceRelease("1.2.3", package);

        AppPatchPreparationResult result = await fixture.PrepareAsync("1.2.3");

        Assert.Equal(AppPatchPreparationStatus.Ready, result.Status);
        Assert.True(fixture.Requests.IndexOf(githubUrl) >= 0);
        Assert.True(fixture.Requests.IndexOf(giteeUrl) > fixture.Requests.IndexOf(githubUrl));
        Assert.Equal(
            package,
            File.ReadAllBytes(Path.Combine(
                fixture.PendingDirectory,
                "PackingProof_AppPatch_v1.2.3.zip")));
    }

    /// <summary>
    /// 现场回归（0.0.74 的店里机器）：检查落在 GitHub 时清单地址在 github.com，店里打不开；
    /// 以前"读清单"这一步没有回退，整条更新直接失败，增量包那次回退永远轮不到。
    /// 现在必须自动改用 gitee.com 上的同一份清单，后面的增量包照常下载。
    /// </summary>
    [Fact]
    public async Task UnreachableGithubManifest_FallsBackToGiteeMirrorAndStillPreparesPatch()
    {
        using var fixture = new AppPatchFixture();
        byte[] package = "mirror-manifest-patch"u8.ToArray();
        fixture.AddRelease("1.2.3", "0.0.0", package);
        string brokenManifestUrl = fixture.AddUnreachableGithubManifest("1.2.3");

        AppPatchPreparationResult result =
            await fixture.PrepareWithManifestUrlAsync(
                "1.2.3",
                brokenManifestUrl,
                TestContext.Current.CancellationToken);

        Assert.True(result.Status == AppPatchPreparationStatus.Ready, result.Message);
        int mirrorIndex = fixture.Requests.IndexOf(fixture.GiteeManifestUrl("1.2.3"));
        Assert.Contains(brokenManifestUrl, fixture.Requests);
        Assert.True(mirrorIndex > fixture.Requests.IndexOf(brokenManifestUrl));
        Assert.True(mirrorIndex < fixture.Requests.IndexOf(fixture.PackageUrlFor("1.2.3")));
    }

    /// <summary>清单只给了一个 GitHub 附件地址时，增量包也要能按镜像换主机下到 Gitee。</summary>
    [Fact]
    public async Task GithubOnlyPatchSource_FallsBackToGiteeMirrorHost()
    {
        using var fixture = new AppPatchFixture();
        byte[] package = "mirror-patch"u8.ToArray();
        string githubPatchUrl = fixture.AddGithubOnlyRelease("1.2.3", package);

        AppPatchPreparationResult result = await fixture.PrepareAsync("1.2.3");

        Assert.True(result.Status == AppPatchPreparationStatus.Ready, result.Message);
        Assert.Contains(githubPatchUrl, fixture.Requests);
        Assert.Contains(fixture.GiteePatchUrl("1.2.3"), fixture.Requests);
    }

    /// <summary>
    /// 镜像也失败时不能反复重试、也不能吞掉错误：两个平台的清单附件各试一次，然后如实报失败。
    /// </summary>
    [Fact]
    public async Task ManifestMirrorAlsoUnavailable_ReportsFailureAfterOneMirrorAttempt()
    {
        using var fixture = new AppPatchFixture();
        fixture.AddRelease("1.2.3", "0.0.0", "unused-patch"u8.ToArray());
        string brokenManifestUrl = fixture.AddUnreachableBothPlatformManifests("1.2.3");

        AppPatchPreparationResult result =
            await fixture.PrepareWithManifestUrlAsync(
                "1.2.3",
                brokenManifestUrl,
                TestContext.Current.CancellationToken);

        Assert.Equal(AppPatchPreparationStatus.Failed, result.Status);
        Assert.Equal(1, fixture.Requests.Count(url => url == brokenManifestUrl));
        Assert.Equal(1, fixture.Requests.Count(url => url == fixture.GiteeManifestUrl("1.2.3")));
        Assert.DoesNotContain(fixture.PackageUrlFor("1.2.3"), fixture.Requests);
    }

    /// <summary>用户取消（关程序）时不能把取消当成"下载失败"再换镜像，必须立刻抛出取消。</summary>
    [Fact]
    public async Task CanceledPreparation_DoesNotFallBackToMirror()
    {
        using var fixture = new AppPatchFixture();
        fixture.AddRelease("1.2.3", "0.0.0", "unused-patch"u8.ToArray());
        string brokenManifestUrl = fixture.AddUnreachableGithubManifest("1.2.3");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.PrepareWithManifestUrlAsync("1.2.3", brokenManifestUrl, cts.Token));

        Assert.DoesNotContain(fixture.GiteeManifestUrl("1.2.3"), fixture.Requests);
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
        private const string ApiBase = "https://api.example/repos/packingproof/desktop";
        private readonly string _root;
        private readonly Dictionary<string, byte[]> _packages = new(StringComparer.OrdinalIgnoreCase);
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
        internal List<string> Requests => _handler.Requests;
        internal string SourceUrl => ApiBase + "/releases/latest";

        internal string ManifestUrlFor(string version) => ManifestBase + $"update-{version}.json";

        internal string PackageUrlFor(string version) => ManifestBase + $"patch-{version}.zip";

        internal string GithubManifestUrl(string version) =>
            $"https://github.com/PackingProof/PackingProof-Desktop/releases/download/v{version}/update_v{version}.json";

        internal string GiteeManifestUrl(string version) =>
            $"https://gitee.com/PackingProof/PackingProof-Desktop/releases/download/v{version}/update_v{version}.json";

        internal string GithubPatchUrl(string version) =>
            $"https://github.com/PackingProof/PackingProof-Desktop/releases/download/v{version}/PackingProof_AppPatch_v{version}.zip";

        internal string GiteePatchUrl(string version) =>
            $"https://gitee.com/PackingProof/PackingProof-Desktop/releases/download/v{version}/PackingProof_AppPatch_v{version}.zip";

        internal string BaselineTagUrl(string version)
        {
            return $"{ApiBase}/releases/tags/v{version}";
        }

        internal void AddRelease(
            string version,
            string baseline,
            byte[] package,
            string? advertisedHash = null)
        {
            _packages[version] = package;
            string manifestUrl = ManifestUrlFor(version);
            string packageUrl = PackageUrlFor(version);
            string manifest = BuildManifest(
                version,
                baseline,
                packageUrl,
                advertisedHash ?? Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
                package.Length);
            _handler.Add(manifestUrl, Encoding.UTF8.GetBytes(manifest), "application/json");
            _handler.Add(packageUrl, package, "application/zip");
        }

        /// <summary>
        /// 现场形态：检查给出的是 GitHub 的清单附件地址，而 github.com 那条链路打不开；
        /// gitee.com 上同一 tag、同一文件名的清单可以下载。返回 GitHub 那条地址。
        /// </summary>
        internal string AddUnreachableGithubManifest(string version)
        {
            byte[] package = _packages[version];
            string manifest = BuildManifest(
                version,
                "0.0.0",
                PackageUrlFor(version),
                Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
                package.Length);
            _handler.AddStatus(GithubManifestUrl(version), HttpStatusCode.BadGateway);
            _handler.Add(GiteeManifestUrl(version), Encoding.UTF8.GetBytes(manifest), "application/json");
            return GithubManifestUrl(version);
        }

        /// <summary>GitHub 与 Gitee 两侧的清单附件都打不开（镜像也救不回来）。</summary>
        internal string AddUnreachableBothPlatformManifests(string version)
        {
            _handler.AddStatus(GithubManifestUrl(version), HttpStatusCode.BadGateway);
            _handler.AddStatus(GiteeManifestUrl(version), HttpStatusCode.BadGateway);
            return GithubManifestUrl(version);
        }

        /// <summary>清单只给一个 GitHub 附件地址（打不开），镜像换成 Gitee 后才能下载增量包。</summary>
        internal string AddGithubOnlyRelease(string version, byte[] package)
        {
            _packages[version] = package;
            string manifest = BuildManifest(
                version,
                "0.0.0",
                GithubPatchUrl(version),
                Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
                package.Length);
            _handler.Add(ManifestUrlFor(version), Encoding.UTF8.GetBytes(manifest), "application/json");
            _handler.AddStatus(GithubPatchUrl(version), HttpStatusCode.BadGateway);
            _handler.Add(GiteePatchUrl(version), package, "application/zip");
            return GithubPatchUrl(version);
        }

        internal void AddBaselineRelease(
            string version,
            string baseline,
            byte[] package,
            string? advertisedHash = null)
        {
            AddRelease(version, baseline, package, advertisedHash);
            string manifestUrl = ManifestBase + $"update-{version}.json";
            string release =
                $$"""
                {
                  "tag_name": "v{{version}}",
                  "assets": [
                    {
                      "name": "update_v{{version}}.json",
                      "browser_download_url": "{{manifestUrl}}"
                    }
                  ]
                }
                """;
            _handler.Add(
                BaselineTagUrl(version),
                Encoding.UTF8.GetBytes(release),
                "application/json");
        }

        internal (string GithubUrl, string GiteeUrl) AddDualSourceRelease(
            string version,
            byte[] package)
        {
            string manifestUrl = ManifestBase + $"update-{version}.json";
            string githubUrl = ManifestBase + $"github-patch-{version}.zip";
            string giteeUrl = ManifestBase + $"gitee-patch-{version}.zip";
            string hash = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
            string manifest =
                $$"""
                {
                  "latest_version": "{{version}}",
                  "patch_baseline_version": "0.0.0",
                  "patch_supported": true,
                  "full_download_page": "https://example.com/releases",
                  "patch_package": {
                    "type": "baseline_patch",
                    "url": "{{giteeUrl}}",
                    "github_url": "{{githubUrl}}",
                    "gitee_url": "{{giteeUrl}}",
                    "sha256": "{{hash}}",
                    "size": {{package.Length}}
                  }
                }
                """;
            _handler.Add(manifestUrl, Encoding.UTF8.GetBytes(manifest), "application/json");
            _handler.AddStatus(githubUrl, HttpStatusCode.ServiceUnavailable);
            _handler.Add(giteeUrl, package, "application/zip");
            return (githubUrl, giteeUrl);
        }

        internal Task<AppPatchPreparationResult> PrepareAsync(string version)
        {
            return PrepareWithManifestUrlAsync(version, ManifestUrlFor(version), CancellationToken.None);
        }

        internal Task<AppPatchPreparationResult> PrepareWithManifestUrlAsync(
            string version,
            string manifestUrl)
        {
            return PrepareWithManifestUrlAsync(version, manifestUrl, CancellationToken.None);
        }

        internal Task<AppPatchPreparationResult> PrepareWithManifestUrlAsync(
            string version,
            string manifestUrl,
            CancellationToken cancellationToken)
        {
            return _service.PrepareAsync(new UpdateCheckResult
            {
                HasUpdate = true,
                LatestVersion = version,
                DownloadUrl = "https://example.com/releases",
                UpdateManifestUrl = manifestUrl,
                SourceUrl = SourceUrl
            },
            progress: null,
            cancellationToken);
        }

        private static string BuildManifest(
            string version,
            string baseline,
            string packageUrl,
            string sha256,
            long size)
        {
            return
                $$"""
                {
                  "latest_version": "{{version}}",
                  "patch_baseline_version": "{{baseline}}",
                  "patch_supported": true,
                  "full_download_page": "https://example.com/releases",
                  "full_download_fallback_page": "https://backup.example/releases",
                  "patch_package": {
                    "type": "baseline_patch",
                    "url": "{{packageUrl}}",
                    "sha256": "{{sha256}}",
                    "size": {{size}}
                  }
                }
                """;
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
        private readonly Dictionary<string, HttpStatusCode> _statuses =
            new(StringComparer.OrdinalIgnoreCase);

        internal List<string> Requests { get; } = [];

        internal void Add(string url, byte[] content, string contentType)
        {
            _responses[url] = (content, contentType);
        }

        internal void AddStatus(string url, HttpStatusCode status)
        {
            _statuses[url] = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<HttpResponseMessage>(cancellationToken);

            string url = request.RequestUri?.AbsoluteUri ?? "";
            Requests.Add(url);
            if (_statuses.TryGetValue(url, out HttpStatusCode status))
            {
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    RequestMessage = request
                });
            }

            if (request.RequestUri != null
                && _responses.TryGetValue(url, out var response))
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
