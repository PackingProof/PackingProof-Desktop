using ExpressPackingMonitoring.Services.Extensions;
using System.Security.Cryptography;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class DefaultScriptExtensionBootstrapServiceTests
{
    [Fact]
    public void PackagedManifestContainsCurrentScripts()
    {
        DefaultScriptExtensionManifest manifest = DefaultScriptExtensionManifest.Load(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring",
            "Resources",
            "DefaultScriptExtensions.json"));

        Assert.Equal(
            ["packingproof.kdzs", "gude55.pinduoduo-orders"],
            manifest.ScriptIds);
    }

    [Fact]
    public async Task EnsureInstalled_InstallsMissingManifestScriptsOnly()
    {
        var market = new FakeMarket();
        market.AddScript("packingproof.kdzs", "2.18");
        market.AddScript("gude55.pinduoduo-orders", "3.6");
        market.AddAdapter("packingproof.qqbot", "0.0.13");
        var installation = new FakeInstallation();

        var service = new DefaultScriptExtensionBootstrapService(
            market,
            installation,
            "v0.0.101",
            CreateManifest(["packingproof.kdzs", "gude55.pinduoduo-orders"]));
        DefaultScriptExtensionBootstrapResult result =
            await service.EnsureInstalledAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["packingproof.kdzs", "gude55.pinduoduo-orders"], result.Installed);
        Assert.Equal(result.Installed, result.Satisfied);
        Assert.Empty(result.Failures);
        Assert.Equal(
            ["packingproof.kdzs", "gude55.pinduoduo-orders"],
            installation.Installs.Select(item => item.Id));
    }

    [Fact]
    public async Task EnsureInstalled_SkipsCatalogWhenManifestIsAlreadyInstalled()
    {
        var market = new FakeMarket();
        var installation = new FakeInstallation();
        installation.Add("packingproof.kdzs", "2.18", "userscript");
        installation.Add("gude55.pinduoduo-orders", "3.6", "userscript");

        var service = new DefaultScriptExtensionBootstrapService(
            market,
            installation,
            "v0.0.101",
            CreateManifest(["packingproof.kdzs", "gude55.pinduoduo-orders"]));
        DefaultScriptExtensionBootstrapResult result =
            await service.EnsureInstalledAsync(TestContext.Current.CancellationToken);

        Assert.False(market.CatalogRequested);
        Assert.Empty(result.Installed);
        Assert.Equal(
            ["packingproof.kdzs", "gude55.pinduoduo-orders"],
            result.Satisfied);
    }

    [Fact]
    public async Task EnsureInstalled_RejectsAdapterRegisteredUnderScriptId()
    {
        var market = new FakeMarket();
        market.AddAdapter("packingproof.kdzs", "2.18");
        market.AddScript("gude55.pinduoduo-orders", "3.6");
        var installation = new FakeInstallation();

        var service = new DefaultScriptExtensionBootstrapService(
            market,
            installation,
            "v0.0.101",
            CreateManifest(["packingproof.kdzs", "gude55.pinduoduo-orders"]));
        DefaultScriptExtensionBootstrapResult result =
            await service.EnsureInstalledAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["gude55.pinduoduo-orders"], result.Installed);
        Assert.Contains("packingproof.kdzs", Assert.Single(result.Failures));
        Assert.DoesNotContain(installation.Installs, item => item.Id == "packingproof.kdzs");
    }

    [Fact]
    public async Task EnsureInstalled_PrefersBundledPackageWithoutLoadingMarket()
    {
        string root = CreateTemporaryDirectory();
        string packageDirectory = Path.Combine(
            root,
            DefaultScriptExtensionManifest.BundledPackageDirectoryName);
        Directory.CreateDirectory(packageDirectory);
        byte[] bytes = "offline package"u8.ToArray();
        string packagePath = Path.Combine(packageDirectory, "local-script-1.0.ppext");
        File.WriteAllBytes(packagePath, bytes);
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string manifestPath = CreateBundledManifest(
            root,
            "local.script",
            "local-script-1.0.ppext",
            bytes.Length,
            sha256);
        var market = new FakeMarket();
        var installation = new FakeInstallation();
        var service = new DefaultScriptExtensionBootstrapService(
            market,
            installation,
            "v0.0.101",
            manifestPath);

        DefaultScriptExtensionBootstrapResult result =
            await service.EnsureInstalledAsync(TestContext.Current.CancellationToken);

        Assert.False(market.CatalogRequested);
        Assert.Equal(["local.script"], result.Installed);
        Assert.Empty(result.Failures);
        Assert.Equal(packagePath, Assert.Single(installation.Installs).PackagePath);
    }

    [Fact]
    public void ManifestRejectsUnsupportedSchemaAndInvalidIds()
    {
        string unsupported = CreateManifest(["packingproof.kdzs"], schemaVersion: 2);
        string invalid = CreateManifest(["packingproof/../adapter"]);

        Assert.Throws<InvalidDataException>(() => DefaultScriptExtensionManifest.Load(unsupported));
        Assert.Throws<InvalidDataException>(() => DefaultScriptExtensionManifest.Load(invalid));
    }

    [Fact]
    public void ManifestRejectsBundledPackagesOverOneMiB()
    {
        string root = CreateTemporaryDirectory();
        string manifestPath = CreateBundledManifest(
            root,
            "local.script",
            "local-script-1.0.ppext",
            DefaultScriptExtensionManifest.MaxBundledPackageBytes + 1,
            new string('a', 64));

        Assert.Throws<InvalidDataException>(() => DefaultScriptExtensionManifest.Load(manifestPath));
    }

    [Fact]
    public void BootstrapIsWiredIntoStartupAndBothGuideEntrypoints()
    {
        string mainViewModel = RepositorySource.ReadMainViewModelParts("Integration", "Media");
        string printWorkstation = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml.cs"));

        Assert.Contains("QueueDefaultScriptExtensionsBootstrap();", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("QueueDefaultScriptExtensionsBootstrap();", printWorkstation, StringComparison.Ordinal);
        Assert.Contains("EnsureInstalledAsync(timeout.Token)", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("EnsureInstalledAsync(timeout.Token)", printWorkstation, StringComparison.Ordinal);
        Assert.DoesNotContain("new ExtensionMarketWindow(\"packingproof.kdzs\")", mainViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagingScriptEnforcesOneMiBLimitAndReusesOnlySelectedPackages()
    {
        string script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "Publish-CleanPackage.ps1"));

        Assert.Contains("Copy-DefaultScriptPackages", script, StringComparison.Ordinal);
        Assert.Contains("$scriptPackageMaximumBytes = 1MB", script, StringComparison.Ordinal);
        Assert.Contains("default-script-packages", script, StringComparison.Ordinal);
        Assert.Contains("Bundled default script packages exceed the 1 MiB hard limit", script, StringComparison.Ordinal);
    }

    private sealed class FakeMarket : IExtensionMarketSource
    {
        private readonly Dictionary<string, ExtensionMarketCatalogItem> _items = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExtensionMarketDetails> _details = new(StringComparer.Ordinal);

        internal bool CatalogRequested { get; private set; }

        internal void AddScript(string id, string version) => Add(id, version, "userscript");

        internal void AddAdapter(string id, string version) => Add(id, version, "external-adapter");

        private void Add(string id, string version, string type)
        {
            _items[id] = new ExtensionMarketCatalogItem
            {
                Id = id,
                Name = id,
                Type = type,
                LatestVersion = version
            };
            _details[id] = new ExtensionMarketDetails
            {
                Extension = new ExtensionMarketDescriptor { Id = id, Name = id, Type = type },
                Versions =
                {
                    new ExtensionMarketVersionEntry
                    {
                        Status = "available",
                        Release = new ExtensionMarketRelease
                        {
                            Version = version,
                            Sha256 = $"sha-{version}",
                            Size = 10,
                            Compatibility = new ExtensionMarketCompatibility
                            {
                                MinPackingProofVersion = "0.0.63"
                            }
                        }
                    }
                }
            };
        }

        public Task<ExtensionMarketSession> LoadCatalogAsync(CancellationToken cancellationToken = default)
        {
            CatalogRequested = true;
            return Task.FromResult(new ExtensionMarketSession(
                new ExtensionMarketCatalog { Extensions = _items.Values.ToList() },
                false,
                []));
        }

        public Task<ExtensionMarketDetails> LoadDetailsAsync(
            ExtensionMarketSession session,
            ExtensionMarketCatalogItem item,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_details[item.Id]);

        public Task<string> DownloadPackageAsync(
            ExtensionMarketRelease release,
            IProgress<ExtensionPackageProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string path = Path.Combine(Path.GetTempPath(), $"default-script-{Guid.NewGuid():N}.ppext");
            File.WriteAllText(path, release.Version);
            return Task.FromResult(path);
        }
    }

    private sealed class FakeInstallation : IExtensionInstallationTarget
    {
        private readonly List<InstalledExtensionRecord> _installed = [];

        internal List<InstalledCall> Installs { get; } = [];

        internal void Add(string id, string version, string type) =>
            _installed.Add(new InstalledExtensionRecord { Id = id, Version = version, Type = type });

        public IReadOnlyList<InstalledExtensionRecord> GetInstalled() => _installed;

        public ExtensionInstallResult Install(
            string packagePath,
            string displayName,
            string? expectedId = null,
            string? expectedVersion = null,
            string? expectedType = null,
            string? expectedSha256 = null)
        {
            Installs.Add(new InstalledCall(
                packagePath,
                expectedId ?? "",
                expectedVersion ?? "",
                expectedType ?? "",
                expectedSha256 ?? ""));
            return new ExtensionInstallResult(
                new InstalledExtensionRecord
                {
                    Id = expectedId ?? "",
                    Version = expectedVersion ?? "",
                    Type = expectedType ?? ""
                },
                []);
        }
    }

    private sealed record InstalledCall(
        string PackagePath,
        string Id,
        string Version,
        string Type,
        string Sha256);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到解决方案目录");
    }

    private static string CreateManifest(IReadOnlyList<string> scriptIds, int schemaVersion = 1)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"packingproof-default-scripts-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            $$"""
              {
                "schemaVersion": {{schemaVersion}},
                "scriptIds": [{{string.Join(",", scriptIds.Select(id => $"\"{id}\""))}}]
              }
              """);
        return path;
    }

    private static string CreateBundledManifest(
        string root,
        string id,
        string fileName,
        long size,
        string sha256)
    {
        string path = Path.Combine(root, "DefaultScriptExtensions.json");
        File.WriteAllText(
            path,
            $$"""
              {
                "schemaVersion": 1,
                "scriptIds": ["{{id}}"],
                "packages": [
                  {
                    "id": "{{id}}",
                    "version": "1.0",
                    "fileName": "{{fileName}}",
                    "size": {{size}},
                    "sha256": "{{sha256}}"
                  }
                ]
              }
              """);
        return path;
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"packingproof-default-script-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
