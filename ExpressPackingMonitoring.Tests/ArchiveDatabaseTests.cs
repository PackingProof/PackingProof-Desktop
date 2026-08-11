using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
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

    [Fact]
    public void MarkVideoDeleted_WritesReasonCode()
    {
        DateTime now = DateTime.Now;
        long id = InsertLocal(@"\\nas\share\x.mp4", now.AddMinutes(-60), now.AddMinutes(-50));
        string filePath = _database.GetVideoById(id)!.FilePath;

        _database.MarkVideoDeleted(
            filePath,
            "硬循环清理（NAS 不可用）",
            RecordingDeletionReasonCode.CapacityEmergencyCleanupUnarchived);

        VideoRecord deleted = _database.QueryVideos(null, null).Single(record => record.Id == id);
        Assert.Equal(
            RecordingDeletionReasonCode.CapacityEmergencyCleanupUnarchived,
            deleted.DeleteReasonCode);
        Assert.Equal("硬循环清理（NAS 不可用）", deleted.DeleteReason);
    }

    [Fact]
    public void MarkVideoDeleted_WritesCapacityCleanupUnarchivedCode()
    {
        DateTime now = DateTime.Now;
        long id = InsertLocal(@"\\nas\share\z.mp4", now.AddMinutes(-60), now.AddMinutes(-50));
        string filePath = _database.GetVideoById(id)!.FilePath;

        _database.MarkVideoDeleted(
            filePath,
            "容量清理（NAS 不可用或未配置）",
            RecordingDeletionReasonCode.CapacityCleanupUnarchived);

        VideoRecord deleted = _database.QueryVideos(null, null).Single(record => record.Id == id);
        Assert.Equal(
            RecordingDeletionReasonCode.CapacityCleanupUnarchived,
            deleted.DeleteReasonCode);
    }

    [Fact]
    public void GetEmergencyCleanupCandidates_FiltersByArchiveStatus()
    {
        DateTime now = DateTime.Now;
        long failedId = InsertLocal(@"\\nas\share\f.mp4", now.AddHours(-3), now.AddHours(-2));
        long pendingId = InsertLocal(@"\\nas\share\p.mp4", now.AddHours(-2), now.AddHours(-1));
        long localId = InsertLocal(@"\\nas\share\l.mp4", now.AddHours(-1), now.AddMinutes(-50));
        _database.UpdateArchiveState(failedId, VideoArchiveStatus.Failed, attemptedAt: now);
        _database.UpdateArchiveState(pendingId, VideoArchiveStatus.Pending, attemptedAt: now);
        DateTime cutoff = now.AddMinutes(-30);

        Assert.Equal(
            failedId,
            Assert.Single(_database.GetEmergencyCleanupCandidates(
                cutoff,
                200,
                VideoArchiveStatus.Failed)).Id);
        Assert.Equal(
            pendingId,
            Assert.Single(_database.GetEmergencyCleanupCandidates(
                cutoff,
                200,
                VideoArchiveStatus.Pending)).Id);
        Assert.Equal(
            localId,
            Assert.Single(_database.GetEmergencyCleanupCandidates(
                cutoff,
                200,
                VideoArchiveStatus.LocalOnly)).Id);
    }

    [Fact]
    public void GetBackfillCandidates_OnlyReturnsLocalOnlyMp4WithoutArchivePath()
    {
        DateTime now = DateTime.Now;
        long eligible = InsertLocal("", now.AddHours(-3), now.AddHours(-2));
        long alreadyTargeted = InsertLocal(@"\\nas\share\a.mp4", now.AddHours(-2), now.AddHours(-1));
        long pendingNoPath = InsertLocal("", now.AddHours(-1), now.AddMinutes(-50));
        _database.MarkArchivePending(pendingNoPath);

        IReadOnlyList<VideoRecord> candidates = _database.GetBackfillCandidates(200);

        Assert.Contains(candidates, record => record.Id == eligible);
        Assert.DoesNotContain(candidates, record => record.Id == alreadyTargeted);
        Assert.DoesNotContain(candidates, record => record.Id == pendingNoPath);
    }

    [Fact]
    public void SetArchiveTarget_OnlyUpdatesEmptyLocalOnlyRecord()
    {
        DateTime now = DateTime.Now;
        long eligible = InsertLocal("", now.AddHours(-3), now.AddHours(-2));
        long targeted = InsertLocal(@"\\nas\share\old.mp4", now.AddHours(-2), now.AddHours(-1));
        _database.UpdateArchiveState(
            targeted,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now);

        Assert.Equal(
            1,
            _database.SetArchiveTarget(eligible, @"\\nas\share\2026-08-08\单号A.mp4"));
        Assert.Equal(
            0,
            _database.SetArchiveTarget(targeted, @"\\nas\share\other.mp4"));

        VideoRecord updated = _database.GetVideoById(eligible)!;
        Assert.Equal(@"\\nas\share\2026-08-08\单号A.mp4", updated.ArchivePath);
        Assert.Equal(VideoArchiveStatus.Pending, updated.ArchiveStatus);
        Assert.Equal(
            @"\\nas\share\old.mp4",
            _database.GetVideoById(targeted)!.ArchivePath);
    }

    [Fact]
    public void GetManualCleanupCandidates_FiltersCutoffRootsStatusAndOrdersTiers()
    {
        DateTime now = DateTime.Now;
        string rootA = Path.Combine(_directory, "rootA");
        string rootB = Path.Combine(_directory, "rootB");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);

        long verifiedId = InsertRecordAt(
            Path.Combine(rootA, "verified.mp4"),
            now.AddDays(-40),
            now.AddDays(-39));
        long failedId = InsertRecordAt(
            Path.Combine(rootA, "failed.mp4"),
            now.AddDays(-20),
            now.AddDays(-19));
        long pendingId = InsertRecordAt(
            Path.Combine(rootB, "pending.mp4"),
            now.AddDays(-10),
            now.AddDays(-9));
        long outsideId = InsertRecordAt(
            Path.Combine(_directory, "outside.mp4"),
            now.AddDays(-5),
            now.AddDays(-4));
        long recentId = InsertRecordAt(
            Path.Combine(rootA, "recent.mp4"),
            now.AddDays(-2),
            now.AddDays(-1));
        _database.UpdateArchiveState(
            verifiedId,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddDays(-39));
        _database.UpdateArchiveState(failedId, VideoArchiveStatus.Failed, attemptedAt: now);
        _database.UpdateArchiveState(pendingId, VideoArchiveStatus.Pending, attemptedAt: now);

        DateTime cutoff = now.AddDays(-3);
        IReadOnlyList<string> roots = [rootA, rootB];
        IReadOnlyList<VideoRecord> candidates =
            _database.GetManualCleanupCandidates(cutoff, roots, 200);

        Assert.Equal(
            [verifiedId, failedId, pendingId],
            candidates.Select(record => record.Id));
        Assert.DoesNotContain(candidates, record => record.Id == outsideId);
        Assert.DoesNotContain(candidates, record => record.Id == recentId);

        IReadOnlyList<VideoRecord> verifiedOnly =
            _database.GetManualCleanupCandidates(
                cutoff,
                roots,
                200,
                VideoArchiveStatus.Verified);
        Assert.Equal(verifiedId, Assert.Single(verifiedOnly).Id);
    }

    [Fact]
    public void GetManualCleanupPreview_CountsBytesAndUnarchived()
    {
        DateTime now = DateTime.Now;
        string rootA = Path.Combine(_directory, "rootA");
        Directory.CreateDirectory(rootA);
        long verifiedId = InsertRecordAt(
            Path.Combine(rootA, "v.mp4"),
            now.AddDays(-40),
            now.AddDays(-39));
        long pendingId = InsertRecordAt(
            Path.Combine(rootA, "p.mp4"),
            now.AddDays(-20),
            now.AddDays(-19));
        long localId = InsertRecordAt(
            Path.Combine(rootA, "l.mp4"),
            now.AddDays(-10),
            now.AddDays(-9));
        _database.UpdateArchiveState(
            verifiedId,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddDays(-39));
        _database.UpdateArchiveState(pendingId, VideoArchiveStatus.Pending, attemptedAt: now);

        ManualCleanupPreview preview =
            _database.GetManualCleanupPreview(now.AddDays(-3), new[] { rootA });

        Assert.Equal(3, preview.Count);
        Assert.Equal(300, preview.Bytes);
        Assert.Equal(2, preview.UnarchivedCount);
    }

    [Fact]
    public void ReconcileMissingLocalFile_OnlyFixesCleanableStates()
    {
        DateTime now = DateTime.Now;
        long verifiedId = InsertLocal(@"\\nas\share\rv.mp4", now.AddHours(-3), now.AddHours(-2));
        long copyingId = InsertLocal(@"\\nas\share\rc.mp4", now.AddHours(-2), now.AddHours(-1));
        _database.UpdateArchiveState(
            verifiedId,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-2));
        _database.UpdateArchiveState(copyingId, VideoArchiveStatus.Copying, attemptedAt: now);

        Assert.Equal(
            1,
            _database.ReconcileMissingLocalFile(
                verifiedId,
                "本地文件已缺失，状态自动修复",
                RecordingDeletionReasonCode.ManualCleanup));
        Assert.Equal(
            0,
            _database.ReconcileMissingLocalFile(
                copyingId,
                "不应修复",
                RecordingDeletionReasonCode.ManualCleanup));

        VideoRecord repaired = _database.GetVideoById(verifiedId)!;
        Assert.Equal(VideoArchiveStatus.LocalDeleted, repaired.ArchiveStatus);
        Assert.Equal(RecordingDeletionReasonCode.ManualCleanup, repaired.DeleteReasonCode);
        Assert.NotNull(repaired.LocalCopyDeletedAt);
        Assert.Equal(
            VideoArchiveStatus.Copying,
            _database.GetVideoById(copyingId)!.ArchiveStatus);
    }

    private long InsertRecordAt(
        string filePath,
        DateTime startTime,
        DateTime endTime)
    {
        long id = _database.InsertVideoRecord(
            "单号M",
            "发货",
            "h264",
            "libx264",
            filePath,
            startTime,
            archivePath: "");
        _database.UpdateVideoRecordOnStop(id, endTime, 10, 100, "手动");
        return id;
    }

    [Fact]
    public void MarkLocalCopyDeleted_WritesReasonCode()
    {
        DateTime now = DateTime.Now;
        long id = InsertLocal(@"\\nas\share\x.mp4", now.AddMinutes(-60), now.AddMinutes(-50));
        _database.UpdateArchiveState(id, VideoArchiveStatus.Verified, contentSha256: "h", completedAt: now);

        _database.MarkLocalCopyDeleted(
            id,
            "全局配额清理",
            RecordingDeletionReasonCode.CapacityCleanupVerified);

        Assert.Equal(
            RecordingDeletionReasonCode.CapacityCleanupVerified,
            _database.GetVideoById(id)!.DeleteReasonCode);
    }

    [Fact]
    public void MarkRecordDeletedById_WritesReasonCode()
    {
        DateTime now = DateTime.Now;
        long id = InsertLocal(@"\\nas\share\x.mp4", now.AddMinutes(-60), now.AddMinutes(-50));

        _database.MarkRecordDeletedById(id, "用户删除", RecordingDeletionReasonCode.UserRequested);

        VideoRecord deleted = _database.QueryVideos(null, null).Single(record => record.Id == id);
        Assert.Equal(RecordingDeletionReasonCode.UserRequested, deleted.DeleteReasonCode);
    }

    [Fact]
    public void GetEmergencyCleanupCandidates_FiltersConflictAndRecentAndOrdersOldestFirst()
    {
        DateTime now = DateTime.Now;
        long oldLocal = InsertLocal(@"\\nas\a.mp4", now.AddHours(-3), now.AddHours(-2));
        long recent = InsertLocal(@"\\nas\b.mp4", now.AddMinutes(-40), now.AddMinutes(-10));
        long oldFailed = InsertLocal(@"\\nas\c.mp4", now.AddHours(-4), now.AddHours(-3));
        long oldConflict = InsertLocal(@"\\nas\d.mp4", now.AddHours(-3), now.AddHours(-2));
        long oldPending = InsertLocal(@"\\nas\e.mp4", now.AddHours(-2), now.AddHours(-1));
        _database.UpdateArchiveState(oldFailed, VideoArchiveStatus.Failed, error: "离线", incrementRetry: true);
        _database.UpdateArchiveState(oldConflict, VideoArchiveStatus.Conflict, error: "冲突");
        _database.MarkArchivePending(oldPending);

        IReadOnlyList<VideoRecord> candidates = _database.GetEmergencyCleanupCandidates(
            now - LocalCopyCleanupPolicy.EmergencyDeleteGracePeriod);

        Assert.Equal(
            [oldFailed, oldLocal, oldPending],
            candidates.Select(record => record.Id));
    }

    [Fact]
    public void LastArchiveProbeAt_IsStoredAndRead()
    {
        DateTime now = DateTime.Now;
        long id = InsertLocal(@"\\nas\share\x.mp4", now.AddMinutes(-60), now.AddMinutes(-50));

        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now,
            lastProbeAt: now);
        Assert.NotNull(_database.GetVideoById(id)!.LastArchiveProbeAt);

        _database.UpdateLastArchiveProbeAt(id, now.AddMinutes(1));
        Assert.NotNull(_database.GetVideoById(id)!.LastArchiveProbeAt);
    }

    [Fact]
    public void UnfinalizedRecord_IsExcludedUntilCompleted()
    {
        DateTime now = DateTime.Now;
        string path = Path.Combine(_directory, "crash.mkv");
        long id = _database.InsertVideoRecord(
            "单号C",
            "发货",
            "h264",
            "libx264",
            path,
            now.AddMinutes(-5),
            archivePath: @"\\nas\share\2026-08-11\crash.mkv");

        // 未 UpdateVideoRecordOnStop（模拟崩溃后未定稿）
        Assert.Equal(VideoArchiveStatus.LocalOnly, _database.GetVideoById(id)!.ArchiveStatus);
        Assert.Empty(_database.GetPendingArchives(20, now));

        _database.UpdateVideoRecordOnStop(id, now, 10, 100, "程序退出");
        _database.MarkArchivePending(id);

        Assert.Contains(
            _database.GetPendingArchives(20, now),
            record => record.Id == id);
    }

    [Fact]
    public void NasFull_IsExcludedFromQueueAndReleased()
    {
        DateTime now = DateTime.Now;
        long id = InsertLocal(@"\\nas\share\y.mp4", now.AddMinutes(-60), now.AddMinutes(-50));
        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.NASFull,
            error: "NAS 空间不足",
            attemptedAt: now);

        Assert.Empty(_database.GetPendingArchives(20, now));
        Assert.Equal(VideoArchiveStatus.NASFull, _database.GetVideoById(id)!.ArchiveStatus);

        int released = _database.ReleaseNasFullRecords();
        Assert.Equal(1, released);
        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.Pending, record.ArchiveStatus);
        Assert.Equal(0, record.ArchiveRetryCount);
        Assert.Contains(
            _database.GetPendingArchives(20, now),
            candidate => candidate.Id == id);
    }

}
