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
        Assert.Equal("", package.GithubUrl);
        Assert.StartsWith(
            "https://github.com/PackingProof/PackingProof-Desktop/releases/download/v1.2.3/",
            LauncherUpdateService.GetDownloadRoute(package, 0).GithubUrl,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LauncherDownload_PrefersGithubThenUsesManifestFallbackAfterRepeatedFailures()
    {
        using var fixture = new Fixture();
        byte[] launcher = Encoding.UTF8.GetBytes("launcher-download");
        string packagePath = fixture.CreatePackage(launcher);
        LauncherPackageInfo package = fixture.Describe(packagePath, launcher) with
        {
            Url = "https://gitee.com/example/launcher.zip",
            GithubUrl = "https://github.com/example/launcher.zip"
        };

        LauncherDownloadRoute initial = LauncherUpdateService.GetDownloadRoute(package, 0);
        LauncherDownloadRoute secondFailure = LauncherUpdateService.GetDownloadRoute(package, 2);
        LauncherDownloadRoute threshold = LauncherUpdateService.GetDownloadRoute(package, 3);

        Assert.Equal(package.GithubUrl, initial.SelectedUrl);
        Assert.False(initial.PreferFallback);
        Assert.Equal(package.GithubUrl, secondFailure.SelectedUrl);
        Assert.False(secondFailure.PreferFallback);
        Assert.Equal(package.Url, threshold.SelectedUrl);
        Assert.True(threshold.PreferFallback);
    }

    [Fact]
    public void LauncherDownloadFailureState_IsScopedToImmutablePackageIdentity()
    {
        using var fixture = new Fixture();
        byte[] launcher = Encoding.UTF8.GetBytes("launcher-state");
        string packagePath = fixture.CreatePackage(launcher);
        LauncherPackageInfo package = fixture.Describe(packagePath, launcher);
        string statePath = Path.Combine(
            fixture.Root,
            LauncherUpdateService.DownloadFailureStateFileName);
        var state = new LauncherDownloadFailureState(package.Version, package.Sha256, 2);

        LauncherUpdateService.SaveDownloadFailureState(statePath, state);

        Assert.Equal(
            2,
            LauncherUpdateService.LoadDownloadFailureState(
                statePath,
                package).ConsecutiveGithubDownloadFailures);
        Assert.Equal(
            0,
            LauncherUpdateService.LoadDownloadFailureState(
                statePath,
                package with { Sha256 = new string('f', 64) }).ConsecutiveGithubDownloadFailures);

        LauncherUpdateService.ResetDownloadFailureState(statePath);
        Assert.False(File.Exists(statePath));
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
        string backup = Assert.Single(Directory.GetFiles(
            Path.Combine(backupDirectory, "launcher"),
            "launcher-*.exe"));
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

    [Fact]
    public void SuccessfulCheckCache_UsesAppVersionAndLauncherFileIdentity()
    {
        using var fixture = new Fixture();
        string launcherPath = Path.Combine(fixture.Root, LauncherUpdateService.LauncherFileName);
        string statePath = Path.Combine(fixture.Root, LauncherUpdateService.CheckStateFileName);
        File.WriteAllText(launcherPath, "launcher", Encoding.UTF8);

        LauncherUpdateService.SaveSuccessfulCheck(
            statePath,
            "1.2.3",
            launcherPath,
            new string('a', 64),
            "1.2.3");

        Assert.True(LauncherUpdateService.ShouldSkipSuccessfulCheck(
            statePath,
            "1.2.3",
            launcherPath));
        Assert.False(LauncherUpdateService.ShouldSkipSuccessfulCheck(
            statePath,
            "1.2.4",
            launcherPath));

        File.AppendAllText(launcherPath, "-changed", Encoding.UTF8);
        Assert.False(LauncherUpdateService.ShouldSkipSuccessfulCheck(
            statePath,
            "1.2.3",
            launcherPath));
    }

    [Fact]
    public void PendingDescriptor_RoundTripsVerifiedPackageWithoutNetworkState()
    {
        using var fixture = new Fixture();
        byte[] launcher = Encoding.UTF8.GetBytes("pending-launcher");
        string packagePath = fixture.CreatePackage(launcher);
        LauncherPackageInfo package = fixture.Describe(packagePath, launcher);
        string descriptorPath = Path.Combine(
            fixture.Root,
            LauncherUpdateService.PendingDescriptorFileName);

        LauncherUpdateService.SavePendingDescriptor(descriptorPath, package);

        Assert.Equal(package, LauncherUpdateService.LoadPendingDescriptor(descriptorPath));
    }

    [Fact]
    public void LegacyPendingDescriptor_DerivesGithubUrlWithoutMigration()
    {
        using var fixture = new Fixture();
        string descriptorPath = Path.Combine(
            fixture.Root,
            LauncherUpdateService.PendingDescriptorFileName);
        File.WriteAllText(
            descriptorPath,
            $$"""
            {
              "ProtocolVersion": 1,
              "Version": "1.2.3",
              "Url": "https://gitee.com/example/PackingProof_LauncherPatch_v1.2.3.zip",
              "Size": 123,
              "Sha256": "{{new string('a', 64)}}",
              "ExecutableSize": 45,
              "ExecutableSha256": "{{new string('b', 64)}}"
            }
            """,
            Encoding.UTF8);

        LauncherPackageInfo package = LauncherUpdateService.LoadPendingDescriptor(descriptorPath);
        LauncherDownloadRoute route = LauncherUpdateService.GetDownloadRoute(package, 0);

        Assert.Equal("", package.GithubUrl);
        Assert.Equal(
            "https://github.com/PackingProof/PackingProof-Desktop/releases/download/v1.2.3/PackingProof_LauncherPatch_v1.2.3.zip",
            route.GithubUrl);
    }

    [Fact]
    public void BackupRetention_DeletesOnlyOldGeneratedLauncherBackups()
    {
        using var fixture = new Fixture();
        string backupDirectory = Path.Combine(fixture.Root, "launcher-backups");
        Directory.CreateDirectory(backupDirectory);
        string unrelated = Path.Combine(backupDirectory, "database-backup.exe");
        File.WriteAllText(unrelated, "keep", Encoding.UTF8);
        var generated = new List<string>();
        for (int index = 0; index < 5; index++)
        {
            string path = Path.Combine(
                backupDirectory,
                $"launcher-20260731-00000{index}-{Guid.NewGuid():N}.exe");
            File.WriteAllText(path, index.ToString(), Encoding.UTF8);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index));
            generated.Add(path);
        }

        LauncherUpdateService.PruneRetainedBackups(backupDirectory);

        Assert.Equal(
            generated.Skip(2).OrderBy(path => path),
            Directory.GetFiles(backupDirectory, "launcher-*.exe").OrderBy(path => path));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void PendingCleanup_RejectsRootAndOutsideDirectories()
    {
        using var fixture = new Fixture();
        string pendingRoot = Directory.CreateDirectory(
            Path.Combine(fixture.Root, "pending")).FullName;
        string child = Directory.CreateDirectory(
            Path.Combine(pendingRoot, "1.2.3")).FullName;
        string outside = Directory.CreateDirectory(
            Path.Combine(fixture.Root, "recordings")).FullName;
        File.WriteAllText(Path.Combine(child, "temporary.txt"), "delete", Encoding.UTF8);
        File.WriteAllText(Path.Combine(outside, "recording.mp4"), "keep", Encoding.UTF8);

        Assert.False(LauncherUpdateService.TryDeleteDirectoryWithinRoot(
            pendingRoot,
            pendingRoot));
        Assert.False(LauncherUpdateService.TryDeleteDirectoryWithinRoot(
            outside,
            pendingRoot));
        Assert.True(LauncherUpdateService.TryDeleteDirectoryWithinRoot(
            child,
            pendingRoot));
        Assert.True(Directory.Exists(pendingRoot));
        Assert.True(File.Exists(Path.Combine(outside, "recording.mp4")));
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
            WriteEntry(
                archive,
                LauncherUpdateService.ManualInstallerCommandName,
                Encoding.UTF8.GetBytes("cmd"));
            WriteEntry(
                archive,
                LauncherUpdateService.ManualInstallerScriptName,
                Encoding.UTF8.GetBytes("script"));
            WriteEntry(
                archive,
                LauncherUpdateService.ManualInstallerManifestName,
                Encoding.UTF8.GetBytes("{}"));
            WriteEntry(
                archive,
                LauncherUpdateService.ManualInstallerNoticeName,
                Encoding.UTF8.GetBytes("notice"));
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
                "https://github.com/example/launcher.zip",
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
