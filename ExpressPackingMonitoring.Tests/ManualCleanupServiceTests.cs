using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using System;
using System.IO;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ManualCleanupServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "epm-manual-cleanup-" + Guid.NewGuid().ToString("N"));
    private readonly string _localRoot;
    private readonly string _nasRoot;
    private readonly VideoDatabase _database;

    public ManualCleanupServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _localRoot = Path.Combine(_directory, "local");
        _nasRoot = Path.Combine(_directory, "nas");
        Directory.CreateDirectory(_localRoot);
        Directory.CreateDirectory(_nasRoot);
        _database = new VideoDatabase(Path.Combine(_directory, "videos.db"));
    }

    public void Dispose()
    {
        _database.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private long InsertRecord(
        string fileName,
        string content,
        DateTime endTime,
        string archiveStatus,
        bool createNasCopy = false)
    {
        string localPath = Path.Combine(_localRoot, fileName);
        File.WriteAllText(localPath, content);
        string archivePath = createNasCopy
            ? Path.Combine(_nasRoot, endTime.ToString("yyyy-MM-dd"), fileName)
            : "";
        long id = _database.InsertVideoRecord(
            "单号" + fileName,
            "发货",
            "h264",
            "libx264",
            localPath,
            endTime.AddMinutes(-10),
            archivePath: archivePath);
        _database.UpdateVideoRecordOnStop(id, endTime, 10, content.Length, "手动");
        if (archiveStatus == VideoArchiveStatus.Verified)
        {
            if (createNasCopy)
            {
                string nasPath = Path.Combine(
                    _nasRoot,
                    endTime.ToString("yyyy-MM-dd"),
                    fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(nasPath)!);
                File.WriteAllText(nasPath, content);
            }
            _database.UpdateArchiveState(
                id,
                VideoArchiveStatus.Verified,
                contentSha256: "h",
                completedAt: endTime);
        }
        else if (archiveStatus == VideoArchiveStatus.Failed)
        {
            _database.UpdateArchiveState(
                id,
                VideoArchiveStatus.Failed,
                attemptedAt: endTime);
        }
        else if (archiveStatus == VideoArchiveStatus.Pending)
        {
            _database.UpdateArchiveState(
                id,
                VideoArchiveStatus.Pending,
                attemptedAt: endTime);
        }
        else if (archiveStatus == VideoArchiveStatus.Copying
                 || archiveStatus == VideoArchiveStatus.Verifying)
        {
            _database.UpdateArchiveState(
                id,
                archiveStatus,
                attemptedAt: endTime);
        }
        return id;
    }

    [Fact]
    public void VerifiedOnly_WhenDeciderFalse_KeepsUnarchived()
    {
        DateTime now = DateTime.Now;
        long verified = InsertRecord(
            "v.mp4",
            "verified-content",
            now.AddDays(-10),
            VideoArchiveStatus.Verified,
            createNasCopy: true);
        long pending = InsertRecord(
            "p.mp4",
            "pending-content",
            now.AddDays(-9),
            VideoArchiveStatus.Pending);
        var service = new ManualCleanupService(_database);

        ManualCleanupResult result = service.Run(
            new ManualCleanupOptions(ManualCleanupKind.ByTime, now.AddDays(-3), 0),
            new[] { _localRoot },
            _ => false);

        VideoRecord verifiedRecord = _database.GetVideoById(verified)!;
        VideoRecord pendingRecord = _database.GetVideoById(pending)!;
        Assert.False(File.Exists(verifiedRecord.FilePath));
        Assert.Equal(VideoArchiveStatus.LocalDeleted, verifiedRecord.ArchiveStatus);
        Assert.Equal(RecordingDeletionReasonCode.ManualCleanup, verifiedRecord.DeleteReasonCode);
        Assert.True(File.Exists(pendingRecord.FilePath));
        Assert.Equal(VideoArchiveStatus.Pending, pendingRecord.ArchiveStatus);
        Assert.Equal(1, result.CleanedCount);
        Assert.Equal(1, result.UnarchivedRemainingCount);
        Assert.True(result.HasUnarchivedOlderThanCutoff);
    }

    [Fact]
    public void SpaceRelease_StopsBeforeAskingWhenTargetReached()
    {
        DateTime now = DateTime.Now;
        long verified = InsertRecord(
            "v.mp4",
            new string('a', 2048),
            now.AddDays(-10),
            VideoArchiveStatus.Verified,
            createNasCopy: true);
        long pending = InsertRecord(
            "p.mp4",
            "pending",
            now.AddDays(-9),
            VideoArchiveStatus.Pending);
        var service = new ManualCleanupService(_database);
        bool deciderCalled = false;

        ManualCleanupResult result = service.Run(
            new ManualCleanupOptions(ManualCleanupKind.BySpace, DateTime.MaxValue, 1024),
            new[] { _localRoot },
            _ =>
            {
                deciderCalled = true;
                return true;
            });

        Assert.False(deciderCalled);
        Assert.True(result.TargetReached);
        Assert.Equal(
            VideoArchiveStatus.LocalDeleted,
            _database.GetVideoById(verified)!.ArchiveStatus);
        Assert.Equal(
            VideoArchiveStatus.Pending,
            _database.GetVideoById(pending)!.ArchiveStatus);
        Assert.Equal(1, result.UnarchivedRemainingCount);
    }

    [Fact]
    public void SpaceRelease_AsksAndCleansUnarchivedWhenConfirmed()
    {
        DateTime now = DateTime.Now;
        long verified = InsertRecord(
            "v.mp4",
            "vv",
            now.AddDays(-10),
            VideoArchiveStatus.Verified,
            createNasCopy: true);
        long failed = InsertRecord(
            "f.mp4",
            "ff",
            now.AddDays(-8),
            VideoArchiveStatus.Failed);
        long pending = InsertRecord(
            "p.mp4",
            "pp",
            now.AddDays(-7),
            VideoArchiveStatus.Pending);
        long local = InsertRecord(
            "l.mp4",
            "ll",
            now.AddDays(-6),
            VideoArchiveStatus.LocalOnly);
        var service = new ManualCleanupService(_database);
        ManualCleanupPrompt? prompt = null;

        ManualCleanupResult result = service.Run(
            new ManualCleanupOptions(ManualCleanupKind.BySpace, DateTime.MaxValue, 10_000_000),
            new[] { _localRoot },
            value =>
            {
                prompt = value;
                return true;
            });

        Assert.NotNull(prompt);
        Assert.Equal(3, prompt!.UnarchivedCount);
        foreach (long id in new[] { failed, pending, local })
        {
            Assert.Equal(
                VideoArchiveStatus.LocalDeleted,
                _database.GetVideoById(id)!.ArchiveStatus);
        }
        Assert.Equal(4, result.CleanedCount);
        Assert.Equal(0, result.UnarchivedRemainingCount);
    }

    [Fact]
    public void TimeCleanup_DoesNotAskWhenNoUnarchivedOlderThanCutoff()
    {
        DateTime now = DateTime.Now;
        long verified = InsertRecord(
            "v.mp4",
            "verified",
            now.AddDays(-10),
            VideoArchiveStatus.Verified,
            createNasCopy: true);
        long recentLocal = InsertRecord(
            "r.mp4",
            "recent",
            now.AddDays(-1),
            VideoArchiveStatus.LocalOnly);
        var service = new ManualCleanupService(_database);
        bool deciderCalled = false;

        ManualCleanupResult result = service.Run(
            new ManualCleanupOptions(ManualCleanupKind.ByTime, now.AddDays(-3), 0),
            new[] { _localRoot },
            _ =>
            {
                deciderCalled = true;
                return true;
            });

        Assert.False(deciderCalled);
        Assert.Equal(
            VideoArchiveStatus.LocalDeleted,
            _database.GetVideoById(verified)!.ArchiveStatus);
        Assert.Equal(
            VideoArchiveStatus.LocalOnly,
            _database.GetVideoById(recentLocal)!.ArchiveStatus);
        Assert.Equal(0, result.UnarchivedRemainingCount);
    }

    [Fact]
    public void MissingLocalFile_IsReconciledAndDoesNotBreakOtherRecords()
    {
        DateTime now = DateTime.Now;
        long verified = InsertRecord(
            "v.mp4",
            "vv",
            now.AddDays(-10),
            VideoArchiveStatus.Verified,
            createNasCopy: true);
        long missing = InsertRecord(
            "m.mp4",
            "mm",
            now.AddDays(-9),
            VideoArchiveStatus.Pending);
        File.Delete(_database.GetVideoById(missing)!.FilePath);
        long local = InsertRecord(
            "l.mp4",
            "ll",
            now.AddDays(-8),
            VideoArchiveStatus.LocalOnly);
        var service = new ManualCleanupService(_database);

        ManualCleanupResult result = service.Run(
            new ManualCleanupOptions(ManualCleanupKind.ByTime, now.AddDays(-3), 0),
            new[] { _localRoot },
            _ => true);

        VideoRecord repaired = _database.GetVideoById(missing)!;
        Assert.Equal(VideoArchiveStatus.BackupLost, repaired.ArchiveStatus);
        Assert.Equal(1, result.RepairedCount);
        Assert.Equal(
            VideoArchiveStatus.LocalDeleted,
            _database.GetVideoById(verified)!.ArchiveStatus);
        Assert.Equal(
            VideoArchiveStatus.LocalDeleted,
            _database.GetVideoById(local)!.ArchiveStatus);
        Assert.Equal(
            RecordingDeletionReasonCode.ManualCleanup,
            _database.GetVideoById(local)!.DeleteReasonCode);
        Assert.Equal(2, result.CleanedCount);
    }

    [Fact]
    public void CopyingAndVerifyingRecords_AreNeverTouched()
    {
        DateTime now = DateTime.Now;
        long copying = InsertRecord(
            "c.mp4",
            "cc",
            now.AddDays(-10),
            VideoArchiveStatus.Copying);
        long verifying = InsertRecord(
            "x.mp4",
            "xx",
            now.AddDays(-9),
            VideoArchiveStatus.Verifying);
        var service = new ManualCleanupService(_database);

        ManualCleanupResult result = service.Run(
            new ManualCleanupOptions(ManualCleanupKind.ByTime, now.AddDays(-3), 0),
            new[] { _localRoot },
            _ => true);

        VideoRecord copyingRecord = _database.GetVideoById(copying)!;
        VideoRecord verifyingRecord = _database.GetVideoById(verifying)!;
        Assert.Equal(VideoArchiveStatus.Copying, copyingRecord.ArchiveStatus);
        Assert.True(File.Exists(copyingRecord.FilePath));
        Assert.Equal(VideoArchiveStatus.Verifying, verifyingRecord.ArchiveStatus);
        Assert.True(File.Exists(verifyingRecord.FilePath));
        Assert.Equal(0, result.CleanedCount);
        Assert.Equal(0, result.UnarchivedRemainingCount);
    }

    [Fact]
    public void LockedFile_SkipsRecordAndKeepsOthersConsistent()
    {
        DateTime now = DateTime.Now;
        long locked = InsertRecord(
            "locked.mp4",
            "locked-content",
            now.AddDays(-10),
            VideoArchiveStatus.LocalOnly);
        long normal = InsertRecord(
            "n.mp4",
            "normal",
            now.AddDays(-9),
            VideoArchiveStatus.LocalOnly);
        using var hold = new FileStream(
            _database.GetVideoById(locked)!.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        var service = new ManualCleanupService(_database);

        ManualCleanupResult result = service.Run(
            new ManualCleanupOptions(ManualCleanupKind.ByTime, now.AddDays(-3), 0),
            new[] { _localRoot },
            _ => true);

        VideoRecord lockedRecord = _database.GetVideoById(locked)!;
        Assert.Equal(VideoArchiveStatus.LocalOnly, lockedRecord.ArchiveStatus);
        Assert.True(File.Exists(lockedRecord.FilePath));
        Assert.Equal(
            VideoArchiveStatus.LocalDeleted,
            _database.GetVideoById(normal)!.ArchiveStatus);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(1, result.CleanedCount);
    }

    [Fact]
    public void Preview_CountsEligibleRecords()
    {
        DateTime now = DateTime.Now;
        InsertRecord(
            "v.mp4",
            "verified",
            now.AddDays(-10),
            VideoArchiveStatus.Verified,
            createNasCopy: true);
        InsertRecord(
            "p.mp4",
            "pending",
            now.AddDays(-9),
            VideoArchiveStatus.Pending);
        InsertRecord(
            "r.mp4",
            "recent",
            now.AddDays(-1),
            VideoArchiveStatus.LocalOnly);
        var service = new ManualCleanupService(_database);

        ManualCleanupPreview preview = service.Preview(
            new ManualCleanupOptions(ManualCleanupKind.ByTime, now.AddDays(-3), 0),
            new[] { _localRoot });

        Assert.Equal(2, preview.Count);
        Assert.Equal(1, preview.UnarchivedCount);
    }
}
