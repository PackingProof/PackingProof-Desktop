using ExpressPackingMonitoring.Config;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class AppPathsTtsCacheTests
{
    [Fact]
    public void PackagedInstallCopiesBundledTtsCacheWithoutDeletingSource()
    {
        using var fixture = new TempFixture();
        string sourceDirectory = Path.Combine(fixture.Root, "package", "tts_cache");
        string destinationDirectory = Path.Combine(fixture.Root, "LocalState", "cache", "tts");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "voices"));
        File.WriteAllBytes(
            Path.Combine(sourceDirectory, "prompt.mp3"),
            [1, 2, 3]);
        File.WriteAllText(
            Path.Combine(sourceDirectory, "voices", "manifest.json"),
            """{"version":1}""");
        File.WriteAllText(
            Path.Combine(sourceDirectory, "tts-cache-manifest.json"),
            """{"schemaVersion":1,"files":["prompt.mp3"]}""");

        int copied = AppPaths.CopyBundledTtsCacheForPackagedInstall(
            sourceDirectory,
            destinationDirectory,
            isPackaged: true);

        Assert.Equal(3, copied);
        Assert.Equal(
            [1, 2, 3],
            File.ReadAllBytes(Path.Combine(destinationDirectory, "prompt.mp3")));
        Assert.True(File.Exists(
            Path.Combine(destinationDirectory, "voices", "manifest.json")));
        Assert.True(File.Exists(Path.Combine(sourceDirectory, "prompt.mp3")));
        Assert.Equal(
            0,
            AppPaths.CopyBundledTtsCacheForPackagedInstall(
                sourceDirectory,
                destinationDirectory,
                isPackaged: true));
    }

    [Fact]
    public void PackagedTtsCacheCopyIsIdempotentAndKeepsExistingTarget()
    {
        using var fixture = new TempFixture();
        string sourceDirectory = Path.Combine(fixture.Root, "package", "tts_cache");
        string destinationDirectory = Path.Combine(fixture.Root, "LocalState", "cache", "tts");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "same.mp3"), "bundled");
        File.WriteAllText(Path.Combine(destinationDirectory, "same.mp3"), "bundled");

        int copied = AppPaths.CopyBundledTtsCacheForPackagedInstall(
            sourceDirectory,
            destinationDirectory,
            isPackaged: true);

        Assert.Equal(0, copied);
        Assert.Equal(
            "bundled",
            File.ReadAllText(Path.Combine(destinationDirectory, "same.mp3")));
        Assert.Equal(
            "bundled",
            File.ReadAllText(Path.Combine(sourceDirectory, "same.mp3")));
    }

    [Fact]
    public void PackagedTtsCacheRepairsSameLengthCorruptionAndCleansInterruptedTemp()
    {
        using var fixture = new TempFixture();
        string sourceDirectory = Path.Combine(fixture.Root, "package", "tts_cache");
        string destinationDirectory = Path.Combine(fixture.Root, "LocalState", "cache", "tts");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "prompt.mp3"), "healthy-audio");
        File.WriteAllText(Path.Combine(destinationDirectory, "prompt.mp3"), "corrupt-data!");
        string staleTemp = Path.Combine(
            destinationDirectory,
            "prompt.mp3.copy-stale.tmp");
        string activeTemp = Path.Combine(
            destinationDirectory,
            "prompt.mp3.copy-active.tmp");
        File.WriteAllText(staleTemp, "interrupted");
        File.WriteAllText(activeTemp, "in progress");
        File.SetLastWriteTimeUtc(staleTemp, DateTime.UtcNow.AddHours(-2));

        int copied = AppPaths.CopyBundledTtsCacheForPackagedInstall(
            sourceDirectory,
            destinationDirectory,
            isPackaged: true);

        Assert.Equal(1, copied);
        Assert.Equal(
            "healthy-audio",
            File.ReadAllText(Path.Combine(destinationDirectory, "prompt.mp3")));
        Assert.False(File.Exists(staleTemp));
        Assert.True(File.Exists(activeTemp));
    }

    [Fact]
    public void UnpackagedInstallDoesNotCopyBundledTtsCache()
    {
        using var fixture = new TempFixture();
        string sourceDirectory = Path.Combine(fixture.Root, "app", "tts_cache");
        string destinationDirectory = Path.Combine(fixture.Root, "cache", "tts");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "prompt.mp3"), "bundled");

        int copied = AppPaths.CopyBundledTtsCacheForPackagedInstall(
            sourceDirectory,
            destinationDirectory,
            isPackaged: false);

        Assert.Equal(0, copied);
        Assert.False(Directory.Exists(destinationDirectory));
    }

    [Fact]
    public void PackagedTtsCachePreparationIsScheduledAfterStartupBootstrap()
    {
        string repositoryRoot = FindRepositoryRoot();
        string appPaths = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "src",
                "ExpressPackingMonitoring",
                "Config",
                "AppPaths.cs"),
            System.Text.Encoding.UTF8);
        string app = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "src",
                "ExpressPackingMonitoring",
                "App.xaml.cs"),
            System.Text.Encoding.UTF8);

        Assert.DoesNotContain(
            "PreparePackagedTtsCache();",
            appPaths,
            StringComparison.Ordinal);
        Assert.Contains(
            "_ = Task.Run(() =>",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "AppPaths.PreparePackagedTtsCache()",
            app,
            StringComparison.Ordinal);
        int instanceLockIndex = app.IndexOf(
            "WorkstationInstanceCoordinator.TryCreate(",
            StringComparison.Ordinal);
        int cachePreparationIndex = app.IndexOf(
            "AppPaths.PreparePackagedTtsCache()",
            StringComparison.Ordinal);
        Assert.True(
            instanceLockIndex >= 0
            && cachePreparationIndex > instanceLockIndex);
    }

    [Fact]
    public void PackagedBundledTtsCacheIsNotMovedByLegacyMigration()
    {
        using var fixture = new TempFixture();
        string bundled = Path.Combine(fixture.Root, "WindowsApps", "app", "tts_cache");

        Assert.True(AppPaths.IsPackagedBundledTtsCacheSource(
            bundled,
            bundled,
            isPackaged: true));
        Assert.False(AppPaths.IsPackagedBundledTtsCacheSource(
            bundled,
            bundled,
            isPackaged: false));
        Assert.False(AppPaths.IsPackagedBundledTtsCacheSource(
            Path.Combine(fixture.Root, "legacy", "tts_cache"),
            bundled,
            isPackaged: true));
    }

    [Fact]
    public void PackagedBundledTtsCacheMigrationPreservesSourceDirectory()
    {
        using var fixture = new TempFixture();
        string bundled = Path.Combine(fixture.Root, "writable-package", "tts_cache");
        string destination = Path.Combine(fixture.Root, "LocalState", "cache", "tts");
        Directory.CreateDirectory(bundled);
        File.WriteAllText(Path.Combine(bundled, "prompt.mp3"), "bundled");

        AppPaths.MoveLegacyTtsCacheDirectory(
            bundled,
            destination,
            bundled,
            isPackaged: true);

        Assert.True(File.Exists(Path.Combine(bundled, "prompt.mp3")));
        Assert.False(Directory.Exists(destination));
    }

    private sealed class TempFixture : IDisposable
    {
        internal TempFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "packingproof-app-paths-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "ExpressPackingMonitoring.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            "ExpressPackingMonitoring repository root was not found.");
    }
}
