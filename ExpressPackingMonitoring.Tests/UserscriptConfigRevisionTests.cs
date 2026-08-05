using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Data;
using Microsoft.Data.Sqlite;
using System.IO;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class UserscriptConfigRevisionTests
{
    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "epm-userscript-revision-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public void FirstFingerprintStartsWithRevisionZero()
    {
        string directory = CreateTempDirectory();
        try
        {
            var store = new UserscriptConfigRevisionStore(
                Path.Combine(directory, "userscript-config-revision.json"));

            Assert.Equal(0, store.GetRevision("fp-a"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void SameFingerprintDoesNotIncrementRevision()
    {
        string directory = CreateTempDirectory();
        try
        {
            var store = new UserscriptConfigRevisionStore(
                Path.Combine(directory, "userscript-config-revision.json"));

            Assert.Equal(0, store.GetRevision("fp-a"));
            Assert.Equal(0, store.GetRevision("fp-a"));
            Assert.Equal(0, store.GetRevision("fp-a"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ChangedFingerprintIncrementsRevisionAndNeverDecreases()
    {
        string directory = CreateTempDirectory();
        try
        {
            var store = new UserscriptConfigRevisionStore(
                Path.Combine(directory, "userscript-config-revision.json"));

            Assert.Equal(0, store.GetRevision("fp-a"));
            Assert.Equal(1, store.GetRevision("fp-b"));
            Assert.Equal(2, store.GetRevision("fp-a"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RevisionPersistsAcrossStoreInstances()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "userscript-config-revision.json");
            var first = new UserscriptConfigRevisionStore(path);
            first.GetRevision("fp-a");
            first.GetRevision("fp-b");

            var second = new UserscriptConfigRevisionStore(path);
            Assert.Equal(1, second.GetRevision("fp-b"));
            Assert.Equal(2, second.GetRevision("fp-c"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CorruptStateFileFallsBackToEmptyState()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "userscript-config-revision.json");
            File.WriteAllText(path, "not-json{");

            var store = new UserscriptConfigRevisionStore(path);
            Assert.Equal(0, store.GetRevision("fp-a"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void StoreCreatesNestedDirectoryLayout()
    {
        string directory = CreateTempDirectory();
        try
        {
            string revisionPath = Path.Combine(directory, "userscript-config", "revision.json");
            var store = new UserscriptConfigRevisionStore(revisionPath);

            Assert.Equal(0, store.GetRevision("fp-a"));
            Assert.True(File.Exists(revisionPath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RevisionFileSurvivesUploadCleanupWhenStoredInSubdirectory()
    {
        string directory = CreateTempDirectory();
        try
        {
            string revisionPath = Path.Combine(directory, "userscript-config", "revision.json");
            Directory.CreateDirectory(Path.GetDirectoryName(revisionPath)!);
            File.WriteAllText(revisionPath, "{\"Fingerprint\":\"fp-a\",\"Revision\":4}");
            File.SetLastWriteTimeUtc(revisionPath, DateTime.UtcNow.AddDays(-10));

            using (var database = new VideoDatabase(Path.Combine(directory, "videos.db")))
            {
                var service = new MobileBackupService(
                    database,
                    directory,
                    () => Path.Combine(directory, "recordings"),
                    _ => null);

                Assert.True(File.Exists(revisionPath));
                Assert.Equal(4, new UserscriptConfigRevisionStore(revisionPath).GetRevision("fp-a"));
            }
        }
        finally
        {
            // 只清本测试数据库对应连接串的池，避免全局 ClearAllPools 与并行测试竞争。
            using (var connection = new SqliteConnection(
                $"Data Source={Path.Combine(directory, "videos.db")}"))
            {
                SqliteConnection.ClearPool(connection);
            }
            Directory.Delete(directory, true);
        }
    }
}
