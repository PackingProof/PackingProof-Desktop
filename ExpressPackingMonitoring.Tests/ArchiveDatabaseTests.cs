using ExpressPackingMonitoring.Data;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ArchiveDatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "epm-archive-tests-" + Guid.NewGuid().ToString("N"));
    private readonly VideoDatabase _database;

    public ArchiveDatabaseTests()
    {
        Directory.CreateDirectory(_directory);
        _database = new VideoDatabase(Path.Combine(_directory, "videos.db"));
    }

    public void Dispose()
    {
        _database.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private long InsertLocal(string archivePath, DateTime startTime, DateTime endTime, string orderId = "单号A")
    {
        long id = _database.InsertVideoRecord(
            orderId,
            "发货",
            "h264",
            "libx264",
            Path.Combine(_directory, $"{orderId}.mp4"),
            startTime,
            archivePath: archivePath);
        _database.UpdateVideoRecordOnStop(id, endTime, 10, 100, "手动");
        return id;
    }

    [Fact]
    public void Insert_StoresArchiveMetadata()
    {
        DateTime now = DateTime.Now;
        long id = InsertLocal(@"\\nas\share\2026-08-11\a.mp4", now.AddMinutes(-10), now);

        VideoRecord record = _database.GetVideoById(id)!;

        Assert.Equal(@"\\nas\share\2026-08-11\a.mp4", record.ArchivePath);
        Assert.Equal(VideoArchiveStatus.LocalOnly, record.ArchiveStatus);
    }

    [Fact]
    public void PendingAndArchiveStateTransitions()
    {
        DateTime now = DateTime.Now;
        long id = InsertLocal(@"\\nas\share\2026-08-11\a.mp4", now.AddMinutes(-10), now);

        _database.MarkArchivePending(id);
        Assert.Equal(VideoArchiveStatus.Pending, _database.GetVideoById(id)!.ArchiveStatus);

        _database.UpdateArchiveState(id, VideoArchiveStatus.Copying, attemptedAt: now);
        VideoRecord copying = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.Copying, copying.ArchiveStatus);
        Assert.NotNull(copying.LastArchiveAttemptAt);

        _database.UpdateArchiveState(id, VideoArchiveStatus.Verified, contentSha256: "abc123", completedAt: now.AddMinutes(1));
        VideoRecord verified = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.Verified, verified.ArchiveStatus);
        Assert.Equal("abc123", verified.ContentSha256);
        Assert.NotNull(verified.ArchiveCompletedAt);
    }

    [Fact]
    public void Failed_RecordsRetryCountAndNextRetryAt()
    {
        DateTime now = DateTime.Now;
        long id = InsertLocal(@"\\nas\share\x.mp4", now.AddMinutes(-5), now);
        _database.MarkArchivePending(id);

        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Failed,
            error: "网络名不存在",
            incrementRetry: true,
            nextRetryAt: now.AddMinutes(5));

        VideoRecord failed = _database.GetVideoById(id)!;
        Assert.Equal(1, failed.ArchiveRetryCount);
        Assert.Equal("网络名不存在", failed.ArchiveError);
        Assert.NotNull(failed.NextRetryAt);
    }

    [Fact]
    public void GetPendingArchives_OrdersPendingNewestFirstThenDueFailed()
    {
        DateTime now = DateTime.Now;
        long olderPending = InsertLocal(@"\\nas\a.mp4", now.AddMinutes(-30), now.AddMinutes(-30));
        long newerPending = InsertLocal(@"\\nas\b.mp4", now.AddMinutes(-20), now.AddMinutes(-20));
        long dueFailed = InsertLocal(@"\\nas\c.mp4", now.AddMinutes(-10), now.AddMinutes(-10));
        long futureFailed = InsertLocal(@"\\nas\d.mp4", now.AddMinutes(-5), now.AddMinutes(-5));

        _database.MarkArchivePending(olderPending);
        _database.MarkArchivePending(newerPending);
        _database.UpdateArchiveState(
            dueFailed,
            VideoArchiveStatus.Failed,
            error: "离线",
            incrementRetry: true,
            nextRetryAt: now.AddMinutes(-1));
        _database.UpdateArchiveState(
            futureFailed,
            VideoArchiveStatus.Failed,
            error: "离线",
            incrementRetry: true,
            nextRetryAt: now.AddMinutes(30));

        IReadOnlyList<VideoRecord> pending = _database.GetPendingArchives(20, now);

        Assert.Equal(
            [newerPending, olderPending, dueFailed],
            pending.Select(record => record.Id));
    }

    [Fact]
    public void PendingDelete_Flow()
    {
        DateTime now = DateTime.Now;
        long id = InsertLocal(@"\\nas\share\x.mp4", now.AddMinutes(-5), now);
        _database.UpdateArchiveState(id, VideoArchiveStatus.Verified, contentSha256: "h", completedAt: now);

        _database.SetPendingArchiveDelete(id, now.AddMinutes(-1));

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.Deleting, record.ArchiveStatus);
        Assert.NotNull(record.PendingDeleteAt);
        Assert.Equal(id, Assert.Single(_database.GetPendingArchiveDeletes(now)).Id);
    }

    [Fact]
    public void MarkLocalCopyDeleted_KeepsRecordPlayableViaArchive()
    {
        DateTime now = DateTime.Now;
        long id = InsertLocal(@"\\nas\share\x.mp4", now.AddMinutes(-5), now);
        _database.UpdateArchiveState(id, VideoArchiveStatus.Verified, contentSha256: "h", completedAt: now);

        _database.MarkLocalCopyDeleted(id, "磁盘清理");

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.LocalDeleted, record.ArchiveStatus);
        Assert.NotNull(record.LocalCopyDeletedAt);
        Assert.Equal("磁盘清理", record.LocalDeleteReason);
        Assert.False(record.IsDeleted);
    }

    [Fact]
    public void MarkRecordDeletedById_WritesDeleteLog()
    {
        DateTime now = DateTime.Now;
        long id = InsertLocal(@"\\nas\share\x.mp4", now.AddMinutes(-5), now);

        _database.MarkRecordDeletedById(id, "用户删除");

        Assert.Null(_database.GetVideoById(id));
        Assert.Contains(_database.GetDeleteLogs(10), log => log.OrderId == "单号A");
    }

    [Fact]
    public void GetOldestVideos_IncludesArchiveMetadata()
    {
        DateTime now = DateTime.Now;
        long id = InsertLocal(@"\\nas\share\x.mp4", now.AddMinutes(-60), now.AddMinutes(-50));
        _database.UpdateArchiveState(id, VideoArchiveStatus.Verified, contentSha256: "h", completedAt: now);

        VideoRecord oldest = Assert.Single(_database.GetOldestVideos(10));

        Assert.Equal(VideoArchiveStatus.Verified, oldest.ArchiveStatus);
        Assert.Equal(@"\\nas\share\x.mp4", oldest.ArchivePath);
    }

    [Fact]
    public void UpdateVideoFilePath_SyncsArchivePathAndRequeuesVerifiedMkv()
    {
        DateTime now = DateTime.Now;
        string mkvPath = Path.Combine(_directory, "single.mkv");
        string mp4Path = Path.Combine(_directory, "single.mp4");
        long id = _database.InsertVideoRecord(
            "单号B",
            "发货",
            "h264",
            "libx264",
            mkvPath,
            now.AddMinutes(-5),
            archivePath: @"\\nas\share\2026-08-11\single.mkv");
        _database.UpdateVideoRecordOnStop(id, now, 10, 100, "手动");
        _database.UpdateArchiveState(id, VideoArchiveStatus.Verified, contentSha256: "h", completedAt: now);

        File.WriteAllText(mp4Path, "mp4");
        _database.UpdateVideoFilePath(mkvPath, mp4Path);

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(mp4Path, record.FilePath);
        Assert.Equal(@"\\nas\share\2026-08-11\single.mp4", record.ArchivePath);
        Assert.Equal(VideoArchiveStatus.Pending, record.ArchiveStatus);
        Assert.Equal("", record.ArchiveError);
    }

    [Fact]
    public void UpdateVideoFilePath_LocalOnlyMkvBecomesPendingMp4()
    {
        DateTime now = DateTime.Now;
        string mkvPath = Path.Combine(_directory, "local-only.mkv");
        string mp4Path = Path.Combine(_directory, "local-only.mp4");
        long id = _database.InsertVideoRecord(
            "单号C",
            "发货",
            "h264",
            "libx264",
            mkvPath,
            now.AddMinutes(-5),
            archivePath: @"\\nas\share\2026-08-11\local-only.mkv");
        _database.UpdateVideoRecordOnStop(id, now, 10, 100, "手动");
        Assert.Equal(VideoArchiveStatus.LocalOnly, _database.GetVideoById(id)!.ArchiveStatus);

        File.WriteAllText(mp4Path, "mp4");
        _database.UpdateVideoFilePath(mkvPath, mp4Path);

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(@"\\nas\share\2026-08-11\local-only.mp4", record.ArchivePath);
        Assert.Equal(VideoArchiveStatus.Pending, record.ArchiveStatus);
    }

    [Fact]
    public void LocalOnlyRecords_AreNotPickedByArchiveQueue()
    {
        DateTime now = DateTime.Now;
        InsertLocal(@"\\nas\share\2026-08-11\not-ready.mp4", now.AddMinutes(-5), now);

        Assert.Empty(_database.GetPendingArchives(20, now));
    }

    [Fact]
    public void MarkArchivePendingByFilePath_OnlyAdvancesLocalOnly()
    {
        DateTime now = DateTime.Now;
        long pendingId = InsertLocal(@"\\nas\share\a.mp4", now.AddMinutes(-5), now);
        long conflictId = InsertLocal(@"\\nas\share\b.mp4", now.AddMinutes(-5), now);
        _database.MarkArchivePending(pendingId);
        _database.UpdateArchiveState(conflictId, VideoArchiveStatus.Conflict, error: "冲突");

        VideoRecord pending = _database.GetVideoById(pendingId)!;
        VideoRecord conflict = _database.GetVideoById(conflictId)!;
        _database.MarkArchivePendingByFilePath(pending.FilePath);
        _database.MarkArchivePendingByFilePath(conflict.FilePath);

        Assert.Equal(VideoArchiveStatus.Pending, _database.GetVideoById(pendingId)!.ArchiveStatus);
        Assert.Equal(VideoArchiveStatus.Conflict, _database.GetVideoById(conflictId)!.ArchiveStatus);
    }
}
