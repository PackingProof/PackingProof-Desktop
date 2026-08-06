using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class FeedbackPackageServiceTests
{
    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "epm-feedback-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTempDirectory(string directory)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(directory, "videos.db")}");
        SqliteConnection.ClearPool(connection);
        try { Directory.Delete(directory, true); } catch { }
    }

    [Fact]
    public void CreatePackage_BundlesLogsRedactedConfigDatabaseAndInfo()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "log"));
            File.WriteAllText(
                Path.Combine(directory, "log", "runtime.log"),
                "line1\nline2\n",
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(directory, "config.json"),
                "{\"WebAccessKey\":\"secret-key-123456\",\"LastKnownHostAccessKey\":\"host-secret\",\"Language\":\"zh-Hans\"}",
                Encoding.UTF8);
            using (var database = new VideoDatabase(Path.Combine(directory, "videos.db")))
            {
                database.InsertVideoRecord(
                    "FEEDBACK-TEST",
                    "shipping",
                    "h264",
                    "libx264",
                    Path.Combine(directory, "video.mp4"),
                    DateTime.Now.AddHours(-1));
            }

            var service = new FeedbackPackageService(
                directory,
                appVersion: "v0.0.39",
                commitId: "abc12345");
            string zipPath = service.CreatePackage(out IReadOnlyList<string> warnings);

            Assert.True(File.Exists(zipPath));
            Assert.StartsWith("PackingProof_Feedback_", Path.GetFileName(zipPath));
            Assert.Empty(warnings);
            Assert.DoesNotContain(
                Directory.GetDirectories(Path.Combine(directory, "backups", "feedback")),
                dir => Path.GetFileName(dir).StartsWith("staging-", StringComparison.Ordinal));

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                string[] names = zip.Entries.Select(entry => entry.FullName).ToArray();
                Assert.Contains("config.json", names);
                Assert.Contains("log/runtime.log", names);
                Assert.Contains("videos.db", names);
                Assert.Contains("feedback-info.txt", names);

                string configText = ReadEntry(zip, "config.json");
                Assert.DoesNotContain("secret-key-123456", configText);
                Assert.DoesNotContain("host-secret", configText);
                Assert.Contains("已脱敏", configText);

                string infoText = ReadEntry(zip, "feedback-info.txt");
                Assert.Contains("v0.0.39", infoText);
                Assert.Contains("abc12345", infoText);
            }

            string snapshotPath = Path.Combine(directory, "snapshot.db");
            ExtractEntry(zipPath, "videos.db", snapshotPath);
            using var snapshot = new VideoDatabase(snapshotPath);
            Assert.True(snapshot.OrderIdExistsRecent("FEEDBACK-TEST"));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void CreatePackage_TailsOversizedLog()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "log"));
            var head = new byte[FeedbackPackageService.MaxLogBytes / 2];
            Array.Fill(head, (byte)'A');
            var tail = new byte[FeedbackPackageService.MaxLogBytes];
            Array.Fill(tail, (byte)'B');
            File.WriteAllBytes(
                Path.Combine(directory, "log", "big.log"),
                head.Concat(tail).ToArray());

            var service = new FeedbackPackageService(directory);
            string zipPath = service.CreatePackage(out _);

            string extracted = Path.Combine(directory, "big-tail.log");
            ExtractEntry(zipPath, "log/big.log", extracted);
            byte[] content = File.ReadAllBytes(extracted);
            Assert.Equal(FeedbackPackageService.MaxLogBytes, content.Length);
            Assert.All(content, b => Assert.Equal((byte)'B', b));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void CreatePackage_SkipsLockedLogWithWarning()
    {
        string directory = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "log"));
            string lockedPath = Path.Combine(directory, "log", "locked.log");
            File.WriteAllText(lockedPath, "locked content", Encoding.UTF8);
            using var lockStream = new FileStream(
                lockedPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);

            var service = new FeedbackPackageService(directory);
            string zipPath = service.CreatePackage(out IReadOnlyList<string> warnings);

            Assert.Contains(warnings, warning => warning.Contains("locked.log"));
            using var zip = ZipFile.OpenRead(zipPath);
            Assert.Null(zip.GetEntry("log/locked.log"));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void CreatePackage_MissingDatabaseReportsWarning()
    {
        string directory = CreateTempDirectory();
        try
        {
            var service = new FeedbackPackageService(directory);
            string zipPath = service.CreatePackage(out IReadOnlyList<string> warnings);

            Assert.Contains(warnings, warning => warning.Contains("videos.db"));
            using var zip = ZipFile.OpenRead(zipPath);
            Assert.Null(zip.GetEntry("videos.db"));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void CreatePackage_PrunesOldFeedbackPackagesButKeepsUnrelatedFiles()
    {
        string directory = CreateTempDirectory();
        try
        {
            string feedbackDir = Path.Combine(directory, "backups", "feedback");
            Directory.CreateDirectory(feedbackDir);
            for (int index = 0; index < 12; index++)
            {
                string path = Path.Combine(
                    feedbackDir,
                    $"PackingProof_Feedback_20260101-{index:D2}.zip");
                File.WriteAllText(path, "old", Encoding.UTF8);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30 + index));
            }
            string unrelated = Path.Combine(feedbackDir, "other.zip");
            File.WriteAllText(unrelated, "keep", Encoding.UTF8);

            var service = new FeedbackPackageService(directory);
            service.CreatePackage(out _);

            string[] remaining = Directory.GetFiles(feedbackDir, "PackingProof_Feedback_*.zip");
            Assert.Equal(FeedbackPackageService.MaxPackagesToKeep, remaining.Length);
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void RedactSensitiveConfig_MasksKeysAndKeepsOtherValues()
    {
        string redacted = FeedbackPackageService.RedactSensitiveConfig(
            "{\"WebAccessKey\":\"abc\",\"LastKnownHostAccessKey\":\"def\",\"Language\":\"zh-Hans\"}");

        Assert.DoesNotContain("abc", redacted);
        Assert.DoesNotContain("def", redacted);
        Assert.Contains("zh-Hans", redacted);
        Assert.Contains("已脱敏", redacted);
    }

    private static string ReadEntry(ZipArchive zip, string entryName)
    {
        ZipArchiveEntry? entry = zip.GetEntry(entryName);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void ExtractEntry(string zipPath, string entryName, string destinationPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry? entry = zip.GetEntry(entryName);
        Assert.NotNull(entry);
        using Stream source = entry!.Open();
        using var target = File.Create(destinationPath);
        source.CopyTo(target);
    }
}
