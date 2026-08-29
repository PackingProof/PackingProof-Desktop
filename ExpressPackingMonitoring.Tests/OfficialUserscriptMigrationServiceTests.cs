using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Services.Extensions;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class OfficialUserscriptMigrationServiceTests
{
    [Theory]
    [InlineData("/PackingProof-Order-Integration-KDZS.user.js", OfficialUserscriptMigrationService.LegacyRequestId)]
    [InlineData("/kuaidizs-order-push.user.js", OfficialUserscriptMigrationService.LegacyRequestId)]
    [InlineData("/api/userscripts/custom-123/download", "custom-123")]
    public void DownloadRoutesRecognizeLegacyAndManagedPaths(string path, string expectedId)
    {
        Assert.True(OfficialUserscriptMigrationService.TryParseDownloadPath(path, out string scriptId));
        Assert.Equal(expectedId, scriptId);
        Assert.True(OfficialUserscriptMigrationService.IsDownloadPathAllowed(path, "GET"));
        Assert.False(OfficialUserscriptMigrationService.IsDownloadPathAllowed(path, "POST"));
    }

    [Fact]
    public void HeartbeatClassificationSeparatesVersionedAndEarlyOfficialScripts()
    {
        var versioned = new ConnectedClientHeartbeat
        {
            ClientType = "userscript",
            DisplayName = OfficialUserscriptMigrationService.CurrentDisplayName,
            AppVersion = "2.14.7"
        };
        var early = new ConnectedClientInfo(
            "userscript-legacy",
            "userscript",
            OfficialUserscriptMigrationService.CurrentDisplayName,
            "127.0.0.1",
            DateTimeOffset.UtcNow,
            "userscript-legacy",
            "",
            0,
            [],
            "");

        Assert.True(OfficialUserscriptMigrationService.IsVersionedOfficialHeartbeat(versioned));
        Assert.True(OfficialUserscriptMigrationService.IsEarlyOfficialHeartbeat(early));
        versioned.DisplayName = "第三方脚本";
        Assert.False(OfficialUserscriptMigrationService.IsVersionedOfficialHeartbeat(versioned));
    }

    [Fact]
    public async Task ConcurrentMigrationInstallsSignedCandidateOnlyOnce()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string packagePath = CreateUserscriptPackage(root);
            string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath))).ToLowerInvariant();
            var source = new StubPackageSource(new OfficialUserscriptPackageDownload(
                packagePath,
                "PackingProof 快递助手订单联动",
                "2.15",
                sha256));
            var userscripts = new UserscriptCatalog(Path.Combine(root, "userscripts"));
            var installation = new ExtensionInstallationService(
                Path.Combine(root, "extensions"),
                userscripts,
                new ExtensionPackageService());
            var migration = new OfficialUserscriptMigrationService(source, installation);

            InstalledExtensionRecord[] records = await Task.WhenAll(
                migration.EnsureInstalledAsync(TestContext.Current.CancellationToken),
                migration.EnsureInstalledAsync(TestContext.Current.CancellationToken));

            Assert.Equal(1, source.DownloadCount);
            Assert.All(records, record =>
            {
                Assert.Equal("packingproof.kdzs", record.Id);
                Assert.Equal("2.15", record.Version);
                Assert.Equal("userscript", record.Type);
            });
            Assert.Single(installation.GetInstalled());
            Assert.Single(userscripts.GetCustomScripts());
            Assert.False(File.Exists(packagePath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateUserscriptPackage(string root)
    {
        string packagePath = Path.Combine(root, "packingproof.kdzs-2.15.ppext");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        WriteEntry(archive, "manifest.json", JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            format = "packingproof-extension",
            packageFormatVersion = 1,
            id = "packingproof.kdzs",
            version = "2.15",
            type = "userscript",
            installation = new { mode = "userscript-import", payloadPath = "payload/main.user.js" },
            compatibility = new
            {
                minPackingProofVersion = "0.0.63",
                platforms = new { userscript = new[] { "any" } }
            },
            access = new
            {
                packingProofPermissions = Array.Empty<string>(),
                packingProofCapabilities = Array.Empty<string>(),
                systemAccess = Array.Empty<string>()
            }
        }));
        WriteEntry(
            archive,
            "payload/main.user.js",
            "// ==UserScript==\n// @name PackingProof 快递助手订单联动\n// @namespace https://github.com/PackingProof\n// @version 2.15\n// PACKING_PROOF_CONNECT_TARGETS\n// PACKING_PROOF_UPDATE_URLS\n// ==/UserScript==\n");
        return packagePath;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "packingproof-userscript-migration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubPackageSource(OfficialUserscriptPackageDownload package)
        : IOfficialUserscriptPackageSource
    {
        internal int DownloadCount { get; private set; }

        public Task<OfficialUserscriptPackageDownload> DownloadLatestAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadCount++;
            return Task.FromResult(package);
        }
    }
}
