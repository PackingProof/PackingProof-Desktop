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
    public void RerouteArchivePath_UpdatesExistingArchivePath()
    {
        DateTime now = DateTime.Now;
        long id = InsertLocal(@"\\nas\share\old.mp4", now.AddHours(-3), now.AddHours(-2));

        Assert.Equal(
            1,
            _database.RerouteArchivePath(id, @"\\nas2\share\new.mp4"));

        Assert.Equal(
            @"\\nas2\share\new.mp4",
            _database.GetVideoById(id)!.ArchivePath);
    }

    [Fact]
    public void GetArchiveQueueSummary_CountsRemainingStatusesOnly()
    {
        DateTime now = DateTime.Now;
        long pending = InsertLocal(@"\\nas\p.mp4", now.AddHours(-6), now.AddHours(-5), orderId: "单号P");
        long copying = InsertLocal(@"\\nas\c.mp4", now.AddHours(-5), now.AddHours(-4), orderId: "单号C");
        long verifying = InsertLocal(@"\\nas\v.mp4", now.AddHours(-4), now.AddHours(-3), orderId: "单号V");
        long failed = InsertLocal(@"\\nas\f.mp4", now.AddHours(-3), now.AddHours(-2), orderId: "单号F");
        long nasFull = InsertLocal(@"\\nas\n.mp4", now.AddHours(-2), now.AddHours(-1), orderId: "单号N");
        long localOnly = InsertLocal(@"\\nas\lo.mp4", now.AddHours(-9), now.AddHours(-8), orderId: "单号LO");
        long verified = InsertLocal(@"\\nas\ok.mp4", now.AddHours(-1), now, orderId: "单号OK");
        long conflict = InsertLocal(@"\\nas\x.mp4", now.AddHours(-7), now.AddHours(-6), orderId: "单号X");
        long deleted = InsertLocal(@"\\nas\d.mp4", now.AddHours(-8), now.AddHours(-7), orderId: "单号D");

        _database.UpdateArchiveState(pending, VideoArchiveStatus.Pending, attemptedAt: now);
        _database.UpdateArchiveState(copying, VideoArchiveStatus.Copying, attemptedAt: now);
        _database.UpdateArchiveState(verifying, VideoArchiveStatus.Verifying, attemptedAt: now);
        _database.UpdateArchiveState(failed, VideoArchiveStatus.Failed, attemptedAt: now);
        _database.UpdateArchiveState(nasFull, VideoArchiveStatus.NASFull, attemptedAt: now);
        _database.UpdateArchiveState(
            verified,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now);
        _database.UpdateArchiveState(conflict, VideoArchiveStatus.Conflict, attemptedAt: now);
        string deletedPath = _database.GetVideoById(deleted)!.FilePath;
        _database.MarkVideoDeleted(deletedPath, "测试删除");

        ArchiveQueueSummary summary = _database.GetArchiveQueueSummary();

        Assert.Equal(1, summary.PendingCount);
        Assert.Equal(2, summary.UploadingCount);
        Assert.Equal(1, summary.FailedCount);
        Assert.Equal(1, summary.NasFullCount);
        Assert.Equal(1, summary.LocalOnlyCount);
        Assert.Equal(1, summary.ConflictCount);
        Assert.Equal(0, summary.PendingVerificationCount);
        Assert.Equal(0, summary.LostCount);
        Assert.Equal(0, summary.CleanedUnbackedCount);
        Assert.Equal(7, summary.RemainingCount);
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

    [Fact]
    public void GetNasCleanupCandidates_IncludesVerifiedAndArchivedLocalDeleted()
    {
        DateTime now = DateTime.Now;
        string root = @"\\nas\share";
        long verified = InsertLocal(
            @"\\nas\share\2026-08-11\a.mp4",
            now.AddHours(-3),
            now.AddHours(-2));
        _database.UpdateArchiveState(
            verified,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-2));

        long localDeleted = InsertLocal(
            @"\\nas\share\2026-08-11\b.mp4",
            now.AddHours(-4),
            now.AddHours(-3));
        _database.UpdateArchiveState(
            localDeleted,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-3));
        _database.MarkLocalCopyDeleted(localDeleted, "容量清理");

        long unarchivedLocalDeleted = InsertLocal(
            @"\\nas\share\2026-08-11\c.mp4",
            now.AddHours(-5),
            now.AddHours(-4));
        _database.MarkLocalCopyDeleted(unarchivedLocalDeleted, "手动清理"); // ArchiveCompletedAt 为空 → 排除

        long pending = InsertLocal(
            @"\\nas\share\2026-08-11\d.mp4",
            now.AddHours(-2),
            now.AddHours(-1));
        _database.MarkArchivePending(pending);

        long outside = InsertLocal(
            @"\\other\share\x.mp4",
            now.AddHours(-6),
            now.AddHours(-5));
        _database.UpdateArchiveState(
            outside,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-5));

        long deleted = InsertLocal(
            @"\\nas\share\2026-08-11\e.mp4",
            now.AddHours(-7),
            now.AddHours(-6));
        _database.UpdateArchiveState(
            deleted,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-6));
        _database.MarkRecordDeletedById(
            deleted,
            "用户删除",
            RecordingDeletionReasonCode.UserRequested);

        IReadOnlyList<VideoRecord> candidates =
            _database.GetNasCleanupCandidates(root);

        Assert.Equal([localDeleted, verified], candidates.Select(record => record.Id));
    }

    [Fact]
    public void MarkNasCopyDeleted_LocalExists_SetsNasDeletedAndLogs()
    {
        DateTime now = DateTime.Now;
        string localPath = Path.Combine(_directory, "nas-delete-local-exists.mp4");
        File.WriteAllText(localPath, "x");
        string archivePath = @"\\nas\share\2026-08-11\n1.mp4";
        long id = _database.InsertVideoRecord(
            "单号N1",
            "发货",
            "h264",
            "libx264",
            localPath,
            now.AddHours(-3),
            archivePath: archivePath);
        _database.UpdateVideoRecordOnStop(id, now.AddHours(-2), 10, 100, "手动");
        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-2));

        _database.MarkNasCopyDeleted(
            id,
            archivePath,
            "NAS 容量循环清理",
            RecordingDeletionReasonCode.NasCapacityCleanup);

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.NasDeleted, record.ArchiveStatus);
        Assert.False(record.IsDeleted);
        Assert.Equal(archivePath, record.ArchivePath);
        Assert.Contains(
            _database.GetDeleteLogs(10),
            log => log.FilePath == archivePath);
    }

    [Fact]
    public void MarkNasCopyDeleted_LocalMissing_MarksDeletedAndLogs()
    {
        DateTime now = DateTime.Now;
        string localPath = Path.Combine(_directory, "nas-delete-local-missing.mp4");
        string archivePath = @"\\nas\share\2026-08-11\n2.mp4";
        long id = _database.InsertVideoRecord(
            "单号N2",
            "发货",
            "h264",
            "libx264",
            localPath,
            now.AddHours(-3),
            archivePath: archivePath);
        _database.UpdateVideoRecordOnStop(id, now.AddHours(-2), 10, 100, "手动");
        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-2));
        _database.MarkLocalCopyDeleted(id, "容量清理");

        _database.MarkNasCopyDeleted(
            id,
            archivePath,
            "NAS 归档文件不存在，检测到外部删除或缺失（非本程序删除）",
            RecordingDeletionReasonCode.NasCopyMissingReconcile);

        VideoRecord record = _database.QueryVideos(null, null)
            .Single(item => item.Id == id);
        Assert.True(record.IsDeleted);
        Assert.Equal(
            RecordingDeletionReasonCode.NasCopyMissingReconcile,
            record.DeleteReasonCode);
        Assert.Contains(
            _database.GetDeleteLogs(10),
            log => log.FilePath == archivePath);
    }

    [Fact]
    public void MarkNasCopyDeleted_IsIdempotent()
    {
        DateTime now = DateTime.Now;
        string localPath = Path.Combine(_directory, "nas-delete-idempotent.mp4");
        File.WriteAllText(localPath, "x");
        string archivePath = @"\\nas\share\2026-08-11\n3.mp4";
        long id = _database.InsertVideoRecord(
            "单号N3",
            "发货",
            "h264",
            "libx264",
            localPath,
            now.AddHours(-3),
            archivePath: archivePath);
        _database.UpdateVideoRecordOnStop(id, now.AddHours(-2), 10, 100, "手动");
        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-2));

        _database.MarkNasCopyDeleted(
            id,
            archivePath,
            "NAS 容量循环清理",
            RecordingDeletionReasonCode.NasCapacityCleanup);
        _database.MarkNasCopyDeleted(
            id,
            archivePath,
            "NAS 容量循环清理",
            RecordingDeletionReasonCode.NasCapacityCleanup);

        Assert.Equal(
            1,
            _database.GetDeleteLogs(10)
                .Count(log => log.FilePath == archivePath));
        Assert.Equal(
            VideoArchiveStatus.NasDeleted,
            _database.GetVideoById(id)!.ArchiveStatus);
    }

    [Fact]
    public void MarkNasCleanedRecordDeleted_MarksDeletedAndIsIdempotent()
    {
        DateTime now = DateTime.Now;
        string localPath = Path.Combine(_directory, "nas-cleaned-final.mp4");
        File.WriteAllText(localPath, "x");
        string archivePath = @"\\nas\share\2026-08-11\n4.mp4";
        long id = _database.InsertVideoRecord(
            "单号N4",
            "发货",
            "h264",
            "libx264",
            localPath,
            now.AddHours(-3),
            archivePath: archivePath);
        _database.UpdateVideoRecordOnStop(id, now.AddHours(-2), 10, 100, "手动");
        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-2));
        _database.MarkNasCopyDeleted(
            id,
            archivePath,
            "NAS 容量循环清理",
            RecordingDeletionReasonCode.NasCapacityCleanup);

        _database.MarkNasCleanedRecordDeleted(
            id,
            archivePath,
            "本地循环清理（NAS 副本已循环清理）",
            RecordingDeletionReasonCode.NasCapacityCleanup);
        _database.MarkNasCleanedRecordDeleted(
            id,
            archivePath,
            "本地循环清理（NAS 副本已循环清理）",
            RecordingDeletionReasonCode.NasCapacityCleanup);

        Assert.True(
            _database.QueryVideos(null, null)
                .Single(item => item.Id == id)
                .IsDeleted);
        Assert.Equal(
            2, // 一条来自 NAS 副本删除，一条来自本地终态删除；重复调用不再追加
            _database.GetDeleteLogs(10)
                .Count(log => log.FilePath == archivePath));
    }

    [Fact]
    public void GetEmergencyCleanupCandidates_IncludesNasDeleted()
    {
        DateTime now = DateTime.Now;
        string localPath = Path.Combine(_directory, "emergency-nasdeleted.mp4");
        File.WriteAllText(localPath, "x");
        string archivePath = @"\\nas\share\2026-08-11\n5.mp4";
        long id = _database.InsertVideoRecord(
            "单号N5",
            "发货",
            "h264",
            "libx264",
            localPath,
            now.AddHours(-4),
            archivePath: archivePath);
        _database.UpdateVideoRecordOnStop(id, now.AddHours(-3), 10, 100, "手动");
        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-3));
        _database.MarkNasCopyDeleted(
            id,
            archivePath,
            "NAS 容量循环清理",
            RecordingDeletionReasonCode.NasCapacityCleanup);

        IReadOnlyList<VideoRecord> candidates = _database.GetEmergencyCleanupCandidates(
            now - LocalCopyCleanupPolicy.EmergencyDeleteGracePeriod);

        Assert.Contains(candidates, record => record.Id == id);
    }

    [Fact]
    public void GetManualCleanupCandidates_IncludesNasDeleted()
    {
        DateTime now = DateTime.Now;
        string localPath = Path.Combine(_directory, "manual-nasdeleted.mp4");
        File.WriteAllText(localPath, "x");
        string archivePath = @"\\nas\share\2026-08-11\n6.mp4";
        long id = _database.InsertVideoRecord(
            "单号N6",
            "发货",
            "h264",
            "libx264",
            localPath,
            now.AddHours(-4),
            archivePath: archivePath);
        _database.UpdateVideoRecordOnStop(id, now.AddHours(-3), 10, 100, "手动");
        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-3));
        _database.MarkNasCopyDeleted(
            id,
            archivePath,
            "NAS 容量循环清理",
            RecordingDeletionReasonCode.NasCapacityCleanup);

        IReadOnlyList<VideoRecord> candidates = _database.GetManualCleanupCandidates(
            DateTime.MaxValue,
            new[] { _directory + Path.DirectorySeparatorChar },
            200,
            VideoArchiveStatus.NasDeleted);

        Assert.Contains(candidates, record => record.Id == id);
    }

    [Fact]
    public void GetReconcileCandidates_ReturnsOnlyStaleArchivedRecords()
    {
        DateTime now = DateTime.Now;
        long freshVerified = InsertLocal(
            @"\\nas\share\2026-08-11\r1.mp4",
            now.AddHours(-3),
            now.AddHours(-2));
        _database.UpdateArchiveState(
            freshVerified,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-2));
        _database.UpdateLastArchiveProbeAt(freshVerified, now.AddHours(-1));

        long staleVerified = InsertLocal(
            @"\\nas\share\2026-08-11\r2.mp4",
            now.AddHours(-6),
            now.AddHours(-5));
        _database.UpdateArchiveState(
            staleVerified,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-5));
        _database.UpdateLastArchiveProbeAt(staleVerified, now.AddHours(-25));

        long staleLocalDeleted = InsertLocal(
            @"\\nas\share\2026-08-11\r3.mp4",
            now.AddHours(-8),
            now.AddHours(-7));
        _database.UpdateArchiveState(
            staleLocalDeleted,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-7));
        _database.MarkLocalCopyDeleted(staleLocalDeleted, "容量清理");

        long unarchivedLocalDeleted = InsertLocal(
            @"\\nas\share\2026-08-11\r4.mp4",
            now.AddHours(-9),
            now.AddHours(-8));
        _database.MarkLocalCopyDeleted(unarchivedLocalDeleted, "手动清理");

        IReadOnlyList<VideoRecord> candidates = _database.GetReconcileCandidates(
            now - LocalCopyCleanupPolicy.UnconfirmedRemoteCleanupGrace);

        Assert.Equal(
            [staleLocalDeleted, staleVerified],
            candidates.Select(record => record.Id));
    }

    [Fact]
    public void MarkLocalMissingUnverified_KeepsRetryFieldsAndWritesLog()
    {
        DateTime now = DateTime.Now;
        string archivePath = @"\\nas\share\2026-08-11\nu.mp4";
        long id = InsertLocal(archivePath, now.AddHours(-3), now.AddHours(-2));
        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-2),
            incrementRetry: true,
            nextRetryAt: now.AddHours(1));
        int retryBefore = _database.GetVideoById(id)!.ArchiveRetryCount;

        _database.MarkLocalMissingUnverified(
            id,
            archivePath,
            "本地副本缺失，等待确认 NAS 归档",
            "");
        _database.MarkLocalMissingUnverified(
            id,
            archivePath,
            "本地副本缺失，等待确认 NAS 归档",
            "");

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.LocalMissingUnverified, record.ArchiveStatus);
        Assert.Equal(retryBefore, record.ArchiveRetryCount);
        Assert.NotNull(record.NextRetryAt); // 重试字段保持原值
        Assert.Equal(archivePath, record.FilePath == null ? "" : record.ArchivePath);
        Assert.Equal(
            1,
            _database.GetDeleteLogs(10)
                .Count(log => log.FilePath == archivePath));
        Assert.False(record.IsDeleted);
        Assert.NotEqual("", record.FilePath); // FilePath 是历史元数据，永不清空
    }

    [Fact]
    public void MarkBackupLost_KeepsRetryFieldsAndPreservesFilePath()
    {
        DateTime now = DateTime.Now;
        string archivePath = @"\\nas\share\2026-08-11\bl.mp4";
        long id = InsertLocal(archivePath, now.AddHours(-3), now.AddHours(-2));
        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-2),
            incrementRetry: true,
            nextRetryAt: now.AddHours(1));
        string filePath = _database.GetVideoById(id)!.FilePath;

        _database.MarkBackupLost(
            id,
            archivePath,
            "本地与 NAS 均无可信副本",
            RecordingDeletionReasonCode.BackupLost);
        _database.MarkBackupLost(
            id,
            archivePath,
            "本地与 NAS 均无可信副本",
            RecordingDeletionReasonCode.BackupLost);

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.BackupLost, record.ArchiveStatus);
        Assert.Equal(filePath, record.FilePath);
        Assert.Equal(1, record.ArchiveRetryCount); // 重试字段保持原值
        Assert.Equal(
            1,
            _database.GetDeleteLogs(10)
                .Count(log => log.FilePath == archivePath));
    }

    [Fact]
    public void MarkLocalCleanupConfirmed_UpgradesUnconfirmedReason()
    {
        DateTime now = DateTime.Now;
        string archivePath = @"\\nas\share\2026-08-11\lc.mp4";
        long id = InsertLocal(archivePath, now.AddHours(-3), now.AddHours(-2));
        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-2));
        _database.MarkLocalCopyDeleted(
            id,
            "容量清理（NAS 不可达，未确认）",
            RecordingDeletionReasonCode.CapacityCleanupUnconfirmedRemote);

        _database.MarkLocalCleanupConfirmed(id, now);

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.LocalDeleted, record.ArchiveStatus);
        Assert.Equal(
            RecordingDeletionReasonCode.CapacityCleanupVerified,
            record.DeleteReasonCode);
        Assert.NotNull(record.LastArchiveProbeAt);
    }

    [Fact]
    public void GetArchiveQueueSummary_FullMixedStatusCounts()
    {
        DateTime now = DateTime.Now;
        long localOnly = InsertLocal(@"\\nas\1.mp4", now.AddHours(-20), now.AddHours(-19), orderId: "单号1");
        long pending = InsertLocal(@"\\nas\2.mp4", now.AddHours(-19), now.AddHours(-18), orderId: "单号2");
        long copying = InsertLocal(@"\\nas\3.mp4", now.AddHours(-18), now.AddHours(-17), orderId: "单号3");
        long failed = InsertLocal(@"\\nas\4.mp4", now.AddHours(-17), now.AddHours(-16), orderId: "单号4");
        long nasFull = InsertLocal(@"\\nas\5.mp4", now.AddHours(-16), now.AddHours(-15), orderId: "单号5");
        long conflict = InsertLocal(@"\\nas\6.mp4", now.AddHours(-15), now.AddHours(-14), orderId: "单号6");
        long unconfirmed = InsertLocal(@"\\nas\7.mp4", now.AddHours(-14), now.AddHours(-13), orderId: "单号7");
        long pendingVerification = InsertLocal(@"\\nas\8.mp4", now.AddHours(-13), now.AddHours(-12), orderId: "单号8");
        long backupLost = InsertLocal(@"\\nas\9.mp4", now.AddHours(-12), now.AddHours(-11), orderId: "单号9");
        long manualCleaned = InsertLocal(@"\\nas\10.mp4", now.AddHours(-11), now.AddHours(-10), orderId: "单号10");
        long verified = InsertLocal(@"\\nas\11.mp4", now.AddHours(-10), now.AddHours(-9), orderId: "单号11");
        long nasDeleted = InsertLocal(@"\\nas\12.mp4", now.AddHours(-9), now.AddHours(-8), orderId: "单号12");
        long deleted = InsertLocal(@"\\nas\13.mp4", now.AddHours(-8), now.AddHours(-7), orderId: "单号13");
        long recording = _database.InsertVideoRecord(
            "单号rec",
            "发货",
            "h264",
            "libx264",
            Path.Combine(_directory, "rec.mp4"),
            now.AddHours(-7),
            archivePath: @"\\nas\14.mp4");

        // 先为需要“历史完成证据”的记录统一置 Verified
        foreach (long id in new[]
                 {
                     unconfirmed, verified, nasDeleted, deleted
                 })
        {
            _database.UpdateArchiveState(
                id,
                VideoArchiveStatus.Verified,
                contentSha256: "h",
                completedAt: now.AddHours(-6));
        }
        _database.MarkArchivePending(pending);
        _database.UpdateArchiveState(copying, VideoArchiveStatus.Copying, attemptedAt: now);
        _database.UpdateArchiveState(failed, VideoArchiveStatus.Failed, attemptedAt: now);
        _database.UpdateArchiveState(nasFull, VideoArchiveStatus.NASFull, attemptedAt: now);
        _database.UpdateArchiveState(conflict, VideoArchiveStatus.Conflict, attemptedAt: now);
        _database.MarkLocalCopyDeleted(
            unconfirmed,
            "容量清理（未确认）",
            RecordingDeletionReasonCode.CapacityCleanupUnconfirmedRemote);
        _database.MarkLocalMissingUnverified(
            pendingVerification,
            @"\\nas\8.mp4",
            "待核实",
            "");
        _database.MarkBackupLost(
            backupLost,
            @"\\nas\9.mp4",
            "丢失",
            RecordingDeletionReasonCode.BackupLost);
        _database.MarkLocalCopyDeleted(
            manualCleaned,
            "手动清理",
            RecordingDeletionReasonCode.ManualCleanup);
        // verified 保持 Verified；nasDeleted 转 NasDeleted（本地仍在）
        _database.MarkNasCopyDeleted(
            nasDeleted,
            @"\\nas\12.mp4",
            "NAS 容量循环清理",
            RecordingDeletionReasonCode.NasCapacityCleanup);
        _database.MarkVideoDeleted(
            _database.GetVideoById(deleted)!.FilePath,
            "测试删除");
        // recording：EndTime 为空 → 不计入

        ArchiveQueueSummary summary = _database.GetArchiveQueueSummary();

        Assert.Equal(1, summary.PendingCount);
        Assert.Equal(1, summary.UploadingCount);
        Assert.Equal(1, summary.FailedCount);
        Assert.Equal(1, summary.NasFullCount);
        Assert.Equal(1, summary.LocalOnlyCount);
        Assert.Equal(1, summary.ConflictCount);
        Assert.Equal(2, summary.PendingVerificationCount); // LocalMissingUnverified + LocalDeleted/Unconfirmed
        Assert.Equal(1, summary.LostCount);
        Assert.Equal(1, summary.CleanedUnbackedCount);
        Assert.Equal(9, summary.RemainingCount);
    }

    [Fact]
    public void LocalDeletedUnconfirmed_FullLifecycle_ConfirmMissingUnavailable()
    {
        DateTime now = DateTime.Now;
        string archivePath = Path.Combine(_directory, "nas-confirm", "old.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        File.WriteAllText(archivePath, new string('x', 100));
        long id = InsertLocal(archivePath, now.AddHours(-5), now.AddHours(-4));
        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-4));
        _database.MarkLocalCopyDeleted(
            id,
            "容量清理（未确认）",
            RecordingDeletionReasonCode.CapacityCleanupUnconfirmedRemote);

        // 不可达：保持原状态，仍计入
        VideoRecord unavailable = _database.GetVideoById(id)!;
        LocalMissingRepair.Apply(
            _database,
            unavailable,
            RemoteFileProbe.FileProbeState.Unavailable);
        Assert.Equal(VideoArchiveStatus.LocalDeleted, _database.GetVideoById(id)!.ArchiveStatus);
        Assert.Equal(
            RecordingDeletionReasonCode.CapacityCleanupUnconfirmedRemote,
            _database.GetVideoById(id)!.DeleteReasonCode);
        Assert.Equal(1, _database.GetArchiveQueueSummary().PendingVerificationCount);

        // NAS 恢复 + 历史证据 + Exists → 升级为已确认，不再计入，可回放
        LocalMissingRepair.Apply(
            _database,
            _database.GetVideoById(id)!,
            RemoteFileProbe.FileProbeState.Exists);
        VideoRecord confirmed = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.LocalDeleted, confirmed.ArchiveStatus);
        Assert.Equal(
            RecordingDeletionReasonCode.CapacityCleanupVerified,
            confirmed.DeleteReasonCode);
        Assert.Equal(0, _database.GetArchiveQueueSummary().PendingVerificationCount);
        Assert.Equal(
            archivePath,
            PlaybackFileResolver.ResolvePlaybackPath(confirmed));

        // NAS 永久消失 → BackupLost
        long lostId = InsertLocal(archivePath + ".lost", now.AddHours(-5), now.AddHours(-4));
        _database.UpdateArchiveState(
            lostId,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-4));
        _database.MarkLocalCopyDeleted(
            lostId,
            "容量清理（未确认）",
            RecordingDeletionReasonCode.CapacityCleanupUnconfirmedRemote);
        LocalMissingRepair.Apply(
            _database,
            _database.GetVideoById(lostId)!,
            RemoteFileProbe.FileProbeState.ConfirmedMissing);
        Assert.Equal(
            VideoArchiveStatus.BackupLost,
            _database.GetVideoById(lostId)!.ArchiveStatus);
        Assert.Equal(1, _database.GetArchiveQueueSummary().LostCount);
    }

    [Fact]
    public void LocalMissingUnverified_ThreeExits()
    {
        DateTime now = DateTime.Now;
        string archivePath = Path.Combine(_directory, "nas-exit", "old.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        File.WriteAllText(archivePath, new string('x', 100));
        long confirmedExit = InsertLocal(archivePath, now.AddHours(-5), now.AddHours(-4));
        _database.UpdateArchiveState(
            confirmedExit,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-4));
        LocalMissingRepair.Apply(
            _database,
            _database.GetVideoById(confirmedExit)!,
            RemoteFileProbe.FileProbeState.Unavailable);
        Assert.Equal(
            VideoArchiveStatus.LocalMissingUnverified,
            _database.GetVideoById(confirmedExit)!.ArchiveStatus);
        LocalMissingRepair.Apply(
            _database,
            _database.GetVideoById(confirmedExit)!,
            RemoteFileProbe.FileProbeState.Exists);
        Assert.Equal(
            VideoArchiveStatus.LocalDeleted,
            _database.GetVideoById(confirmedExit)!.ArchiveStatus);
        Assert.Equal(
            RecordingDeletionReasonCode.CapacityCleanupVerified,
            _database.GetVideoById(confirmedExit)!.DeleteReasonCode);

        long lostExit = InsertLocal(@"\\nas\exit2.mp4", now.AddHours(-5), now.AddHours(-4));
        _database.UpdateArchiveState(
            lostExit,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-4));
        LocalMissingRepair.Apply(
            _database,
            _database.GetVideoById(lostExit)!,
            RemoteFileProbe.FileProbeState.Unavailable);
        LocalMissingRepair.Apply(
            _database,
            _database.GetVideoById(lostExit)!,
            RemoteFileProbe.FileProbeState.ConfirmedMissing);
        Assert.Equal(
            VideoArchiveStatus.BackupLost,
            _database.GetVideoById(lostExit)!.ArchiveStatus);

        long userExit = InsertLocal(@"\\nas\exit3.mp4", now.AddHours(-5), now.AddHours(-4));
        _database.UpdateArchiveState(
            userExit,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: now.AddHours(-4));
        LocalMissingRepair.Apply(
            _database,
            _database.GetVideoById(userExit)!,
            RemoteFileProbe.FileProbeState.Unavailable);
        _database.MarkRecordDeletedById(
            userExit,
            "用户删除",
            RecordingDeletionReasonCode.UserRequested);
        Assert.Null(_database.GetVideoById(userExit));
    }

}
