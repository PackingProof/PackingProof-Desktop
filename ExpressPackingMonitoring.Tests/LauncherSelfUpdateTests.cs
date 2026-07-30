using ExpressPackingMonitoring.Services;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class LauncherSelfUpdateTests
{
    [Fact]
    public void PackageInfo_IsOptionalAndRejectsUnsupportedOrIncompleteDescriptors()
    {
        using JsonDocument legacy = JsonDocument.Parse("""{"latest_version":"1.2.3"}""");
        Assert.Null(LauncherUpdateService.ParsePackageInfo(legacy.RootElement));

        using JsonDocument unsupported = JsonDocument.Parse(
            BuildDescriptor(protocolVersion: 2, new string('a', 64), new string('b', 64)));
        Assert.Null(LauncherUpdateService.ParsePackageInfo(unsupported.RootElement));

        using JsonDocument valid = JsonDocument.Parse(
            BuildDescriptor(protocolVersion: 1, new string('a', 64), new string('b', 64)));
        LauncherPackageInfo package = Assert.IsType<LauncherPackageInfo>(
            LauncherUpdateService.ParsePackageInfo(valid.RootElement));
        Assert.Equal("1.2.3", package.Version);
    }

    [Fact]
    public void StandardLayout_RequiresExistingRootLauncherBesideAppDirectory()
    {
        using var fixture = new Fixture();
        string appDirectory = Directory.CreateDirectory(Path.Combine(fixture.Root, "app")).FullName;

        Assert.False(LauncherUpdateService.TryResolveInstalledLauncher(
            appDirectory,
            out _));

        string launcherPath = Path.Combine(fixture.Root, LauncherUpdateService.LauncherFileName);
        File.WriteAllText(launcherPath, "old", Encoding.UTF8);
        Assert.True(LauncherUpdateService.TryResolveInstalledLauncher(
            appDirectory,
            out string resolved));
        Assert.Equal(Path.GetFullPath(launcherPath), resolved);
    }

    [Fact]
    public void ValidPackage_ReplacesLauncherAndRetainsBackup()
    {
        using var fixture = new Fixture();
        byte[] oldLauncher = Encoding.UTF8.GetBytes("old-launcher");
        byte[] newLauncher = Encoding.UTF8.GetBytes("new-launcher");
        string launcherPath = Path.Combine(fixture.Root, LauncherUpdateService.LauncherFileName);
        File.WriteAllBytes(launcherPath, oldLauncher);
        string packagePath = fixture.CreatePackage(newLauncher);
        string backupDirectory = Path.Combine(fixture.Root, "backups");
        LauncherPackageInfo package = fixture.Describe(packagePath, newLauncher);

        LauncherUpdateService.ApplyDownloadedPackage(
            package,
            packagePath,
            launcherPath,
            backupDirectory);

        Assert.Equal(newLauncher, File.ReadAllBytes(launcherPath));
        string backup = Assert.Single(Directory.GetFiles(backupDirectory, "launcher-*.exe"));
        Assert.Equal(oldLauncher, File.ReadAllBytes(backup));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidPackage_PreservesInstalledLauncher(bool addUnexpectedEntry)
    {
        using var fixture = new Fixture();
        byte[] oldLauncher = Encoding.UTF8.GetBytes("known-good-launcher");
        byte[] newLauncher = Encoding.UTF8.GetBytes("replacement-launcher");
        string launcherPath = Path.Combine(fixture.Root, LauncherUpdateService.LauncherFileName);
        File.WriteAllBytes(launcherPath, oldLauncher);
        string packagePath = fixture.CreatePackage(newLauncher, addUnexpectedEntry);
        LauncherPackageInfo package = fixture.Describe(packagePath, newLauncher);
        if (!addUnexpectedEntry)
            package = package with { ExecutableSha256 = new string('0', 64) };

        Assert.ThrowsAny<Exception>(() => LauncherUpdateService.ApplyDownloadedPackage(
            package,
            packagePath,
            launcherPath,
            Path.Combine(fixture.Root, "backups")));

        Assert.Equal(oldLauncher, File.ReadAllBytes(launcherPath));
    }

    private static string BuildDescriptor(int protocolVersion, string packageHash, string executableHash)
        => $$"""
        {
          "launcher_package": {
            "protocol_version": {{protocolVersion}},
            "version": "1.2.3",
            "url": "https://example.com/launcher.zip",
            "size": 123,
            "sha256": "{{packageHash}}",
            "executable_size": 45,
            "executable_sha256": "{{executableHash}}"
          }
        }
        """;

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"epm-launcher-update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreatePackage(byte[] launcher, bool addUnexpectedEntry = false)
        {
            string path = Path.Combine(Root, $"package-{Guid.NewGuid():N}.zip");
            using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
            WriteEntry(archive, LauncherUpdateService.LauncherFileName, launcher);
            if (addUnexpectedEntry)
                WriteEntry(archive, "unexpected.txt", Encoding.UTF8.GetBytes("unexpected"));
            return path;
        }

        public LauncherPackageInfo Describe(string packagePath, byte[] launcher)
        {
            string launcherSource = Path.Combine(Root, $"launcher-{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(launcherSource, launcher);
            return new LauncherPackageInfo(
                LauncherUpdateService.SupportedProtocolVersion,
                "1.2.3",
                "https://example.com/launcher.zip",
                new FileInfo(packagePath).Length,
                LauncherUpdateService.ComputeSha256(packagePath),
                launcher.Length,
                LauncherUpdateService.ComputeSha256(launcherSource));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }

        private static void WriteEntry(ZipArchive archive, string name, byte[] content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using Stream stream = entry.Open();
            stream.Write(content);
        }
    }
}
