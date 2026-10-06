using System.IO;
using ExpressPackingMonitoring.Services.Extensions;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 后台自动更新只对"扩展市场里登记过"的扩展生效：市场里查不到的一律不动，
/// 版本不比当前新不动，市场要求更高版本的程序时跳过。
/// </summary>
public sealed class ExtensionAutoUpdateTests
{
    [Theory]
    [InlineData("2.17", "2.16", true)]
    [InlineData("v2.17", "2.16", true)]
    [InlineData("2.14.0", "2.14", false)]
    [InlineData("2.16", "2.16", false)]
    [InlineData("2.15", "2.16", false)]
    [InlineData("", "2.16", false)]
    [InlineData("abc", "2.16", false)]
    public void IsNewerVersion_ComparesNormalizedVersions(string candidate, string installed, bool expected) =>
        Assert.Equal(expected, ExtensionMarketVersionPolicy.IsNewerVersion(candidate, installed));

    [Theory]
    [InlineData("0.0.63", "v0.0.76", true)]
    [InlineData("0.0.76", "v0.0.76", true)]
    [InlineData("0.0.77", "v0.0.76", false)]
    [InlineData("", "v0.0.76", false)]
    public void IsAppCompatible_ComparesAgainstCurrentAppVersion(string minimum, string current, bool expected) =>
        Assert.Equal(expected, ExtensionMarketVersionPolicy.IsAppCompatible(minimum, current));

    [Fact]
    public void ShouldCheckNow_ThrottlesToOncePerDay()
    {
        var now = new DateTime(2026, 10, 6, 9, 0, 0);

        Assert.True(ExtensionAutoUpdateService.ShouldCheckNow("", now));
        Assert.True(ExtensionAutoUpdateService.ShouldCheckNow("不是时间", now));
        Assert.False(ExtensionAutoUpdateService.ShouldCheckNow("2026-10-06 07:00:00", now));
        Assert.True(ExtensionAutoUpdateService.ShouldCheckNow("2026-10-05 07:00:00", now));
    }

    [Fact]
    public async Task RunAsync_UpdatesOnlyRegisteredExtensionsThatHaveANewerVersion()
    {
        var market = new FakeMarket();
        market.Add("packingproof.kdzs", "2.17");
        market.Add("third.party", "1.0.0");

        var installation = new FakeInstallation();
        installation.Add("packingproof.kdzs", "2.16");
        installation.Add("third.party", "1.0.0");
        installation.Add("local.only", "1.0.0");

        var service = new ExtensionAutoUpdateService(market, installation, "v0.0.76");
        ExtensionAutoUpdateResult result = await service.RunAsync();

        Assert.False(result.Skipped);
        Assert.Equal(["packingproof.kdzs 2.16→2.17"], result.Updated);
        Assert.Empty(result.Failures);
        Assert.Equal(["packingproof.kdzs"], market.RequestedDetails);

        InstalledCall install = Assert.Single(installation.Installs);
        Assert.Equal("packingproof.kdzs", install.Id);
        Assert.Equal("2.17", install.Version);
        Assert.Equal("sha-2.17", install.Sha256);
    }

    [Fact]
    public async Task RunAsync_SkipsReleaseThatNeedsANewerPackingProof()
    {
        var market = new FakeMarket();
        market.Add("packingproof.kdzs", "2.17", minPackingProofVersion: "0.0.99");
        var installation = new FakeInstallation();
        installation.Add("packingproof.kdzs", "2.16");

        var service = new ExtensionAutoUpdateService(market, installation, "v0.0.76");
        ExtensionAutoUpdateResult result = await service.RunAsync();

        Assert.Empty(result.Updated);
        Assert.Empty(installation.Installs);
        Assert.Contains("需要 PackingProof 0.0.99", Assert.Single(result.Failures));
    }

    [Fact]
    public async Task RunAsync_KeepsUpdatingOtherExtensionsWhenOneFails()
    {
        var market = new FakeMarket();
        market.Add("broken.id", "2.0.0");
        market.Add("packingproof.kdzs", "2.17");
        var installation = new FakeInstallation();
        installation.Add("broken.id", "1.0.0");
        installation.Add("packingproof.kdzs", "2.16");

        var service = new ExtensionAutoUpdateService(market, installation, "v0.0.76");
        ExtensionAutoUpdateResult result = await service.RunAsync();

        Assert.Equal(["packingproof.kdzs 2.16→2.17"], result.Updated);
        Assert.Contains("broken.id", Assert.Single(result.Failures));
        Assert.Equal("packingproof.kdzs", Assert.Single(installation.Installs).Id);
    }

    [Fact]
    public async Task RunAsync_SkipsCatalogLookupWhenNothingIsInstalled()
    {
        var market = new FakeMarket();
        var installation = new FakeInstallation();

        var service = new ExtensionAutoUpdateService(market, installation, "v0.0.76");
        ExtensionAutoUpdateResult result = await service.RunAsync();

        Assert.True(result.Skipped);
        Assert.Empty(market.RequestedDetails);
    }

    private sealed class FakeMarket : IExtensionMarketSource
    {
        private readonly Dictionary<string, ExtensionMarketCatalogItem> _items = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ExtensionMarketDetails> _details = new(StringComparer.OrdinalIgnoreCase);

        internal List<string> RequestedDetails { get; } = [];

        internal void Add(string id, string latestVersion, string minPackingProofVersion = "0.0.63")
        {
            _items[id] = new ExtensionMarketCatalogItem
            {
                Id = id,
                Name = id,
                Type = "userscript",
                LatestVersion = latestVersion
            };
            _details[id] = new ExtensionMarketDetails
            {
                Versions =
                {
                    new ExtensionMarketVersionEntry
                    {
                        Status = "available",
                        Release = new ExtensionMarketRelease
                        {
                            Version = latestVersion,
                            Sha256 = $"sha-{latestVersion}",
                            Size = 10,
                            Compatibility = new ExtensionMarketCompatibility
                            {
                                MinPackingProofVersion = minPackingProofVersion
                            }
                        }
                    }
                }
            };
        }

        public Task<ExtensionMarketSession> LoadCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExtensionMarketSession(
                new ExtensionMarketCatalog { Extensions = _items.Values.ToList() },
                false,
                []));

        public Task<ExtensionMarketDetails> LoadDetailsAsync(
            ExtensionMarketSession session,
            ExtensionMarketCatalogItem item,
            CancellationToken cancellationToken = default)
        {
            RequestedDetails.Add(item.Id);
            return Task.FromResult(_details[item.Id]);
        }

        public Task<string> DownloadPackageAsync(
            ExtensionMarketRelease release,
            IProgress<ExtensionPackageProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string path = Path.Combine(Path.GetTempPath(), $"fake-{Guid.NewGuid():N}.ppext");
            File.WriteAllText(path, release.Version);
            return Task.FromResult(path);
        }
    }

    private sealed class FakeInstallation : IExtensionInstallationTarget
    {
        private readonly List<InstalledExtensionRecord> _installed = [];

        internal List<InstalledCall> Installs { get; } = [];

        internal void Add(string id, string version) =>
            _installed.Add(new InstalledExtensionRecord { Id = id, Version = version, Type = "userscript" });

        public IReadOnlyList<InstalledExtensionRecord> GetInstalled() => _installed;

        public ExtensionInstallResult Install(
            string packagePath,
            string displayName,
            string? expectedId = null,
            string? expectedVersion = null,
            string? expectedType = null,
            string? expectedSha256 = null)
        {
            if (string.Equals(expectedId, "broken.id", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装失败");

            Installs.Add(new InstalledCall(expectedId ?? "", expectedVersion ?? "", expectedSha256 ?? ""));
            return new ExtensionInstallResult(
                new InstalledExtensionRecord { Id = expectedId ?? "", Version = expectedVersion ?? "" },
                []);
        }
    }

    internal sealed record InstalledCall(string Id, string Version, string Sha256);
}
