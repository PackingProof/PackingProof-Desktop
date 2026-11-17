using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VideoDurationBackfillServiceTests
{
    [Fact]
    public async Task RunOnce_RepairsMissingDurationAndEndTime()
    {
        string root = CreateTemporaryDirectory();
        VideoDatabase? database = null;
        try
        {
            string videoPath = Path.Combine(root, "interrupted.mp4");
            await File.WriteAllBytesAsync(
                videoPath,
                [1, 2, 3, 4],
                TestContext.Current.CancellationToken);
            database = new VideoDatabase(Path.Combine(root, "videos.db"));
            DateTime startTime = new(2026, 11, 15, 21, 4, 44);
            const string sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            Assert.True(database.TryInsertImportedVideoRecord(
                "TEST-ORDER",
                "发货",
                videoPath,
                new FileInfo(videoPath).Length,
                startTime,
                0,
                sha256));
            var service = new VideoDurationBackfillService(
                database,
                _ => new CompletedVideoMetadata(1920, 1080, 47.25, 60));

            VideoDurationBackfillResult result =
                await service.RunOnceAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, result.Candidates);
            Assert.Equal(1, result.Updated);
            VideoRecord record = Assert.IsType<VideoRecord>(
                database.GetVideoByContentSha256(sha256));
            Assert.Equal(47.25, record.DurationSeconds, precision: 3);
            Assert.Equal(startTime.AddSeconds(47), record.EndTime);

            VideoDurationBackfillResult repeated =
                await service.RunOnceAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, repeated.Candidates);
        }
        finally
        {
            database?.Dispose();
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RunOnce_DoesNotRetryMissingFileWithinCooldown()
    {
        string root = CreateTemporaryDirectory();
        VideoDatabase? database = null;
        try
        {
            string missingPath = Path.Combine(root, "missing.mp4");
            database = new VideoDatabase(Path.Combine(root, "videos.db"));
            const string sha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            Assert.True(database.TryInsertImportedVideoRecord(
                "MISSING-ORDER",
                "发货",
                missingPath,
                4,
                new DateTime(2026, 11, 15, 20, 0, 0),
                0,
                sha256));
            int probeCount = 0;
            var service = new VideoDurationBackfillService(
                database,
                _ =>
                {
                    probeCount++;
                    return null;
                });

            VideoDurationBackfillResult first =
                await service.RunOnceAsync(TestContext.Current.CancellationToken);
            VideoDurationBackfillResult second =
                await service.RunOnceAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, first.Candidates);
            Assert.Equal(1, first.Missing);
            Assert.Equal(0, second.Candidates);
            Assert.Equal(0, probeCount);
        }
        finally
        {
            database?.Dispose();
            TryDeleteDirectory(root);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "packingproof-duration-backfill-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, true);
        }
        catch (IOException)
        {
            // SQLite 连接池可能还在释放文件句柄；临时目录留给系统清理。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
