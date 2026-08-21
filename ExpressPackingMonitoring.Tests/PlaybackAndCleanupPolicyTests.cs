using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using System;
using System.IO;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class PlaybackAndCleanupPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "epm-playback-policy-" + Guid.NewGuid().ToString("N"));

    public PlaybackAndCleanupPolicyTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private VideoRecord VerifiedRecord(string fileName, DateTime startTime, bool createLocal, bool createArchive)
    {
        string localPath = Path.Combine(_directory, "local-" + fileName);
        string archivePath = Path.Combine(_directory, "archive-" + fileName);
        if (createLocal) File.WriteAllText(localPath, "local");
        if (createArchive) File.WriteAllText(archivePath, "local");
        return new VideoRecord
        {
            Id = 1,
            FilePath = localPath,
            ArchivePath = archivePath,
            ArchiveStatus = VideoArchiveStatus.Verified,
            ArchiveCompletedAt = DateTime.Now,
            StartTime = startTime,
            FileSizeBytes = 5
        };
    }

    [Fact]
    public void CleanupPolicy_RequiresVerifiedArchiveAndOldCopy()
    {
        DateTime now = new(2026, 8, 11, 12, 0, 0);
        VideoRecord eligible = VerifiedRecord("a.mp4", now.AddDays(-3), createLocal: true, createArchive: true);
        Assert.True(LocalCopyCleanupPolicy.IsEligibleForCapacityCleanup(eligible, now, out _));

        VideoRecord unverified = VerifiedRecord("b.mp4", now.AddDays(-3), createLocal: true, createArchive: true);
        unverified.ArchiveStatus = VideoArchiveStatus.Pending;
        Assert.False(LocalCopyCleanupPolicy.IsEligibleForCapacityCleanup(unverified, now, out _));

        VideoRecord recent = VerifiedRecord("c.mp4", now.AddDays(-1), createLocal: true, createArchive: true);
        Assert.False(LocalCopyCleanupPolicy.IsEligibleForCapacityCleanup(recent, now, out _));

        VideoRecord today = VerifiedRecord("d.mp4", now.Date.AddHours(1), createLocal: true, createArchive: true);
        Assert.False(LocalCopyCleanupPolicy.IsEligibleForCapacityCleanup(today, now, out _));
    }

    [Fact]
    public void CleanupPolicy_RejectsMissingLocalOrArchiveMetadata()
    {
        DateTime now = new(2026, 8, 11, 12, 0, 0);
        VideoRecord missingLocal = VerifiedRecord("e.mp4", now.AddDays(-3), createLocal: false, createArchive: true);
        Assert.False(LocalCopyCleanupPolicy.IsEligibleForCapacityCleanup(missingLocal, now, out _));

        VideoRecord missingArchive = VerifiedRecord("f.mp4", now.AddDays(-3), createLocal: true, createArchive: false);
        missingArchive.ArchiveCompletedAt = null;
        Assert.False(LocalCopyCleanupPolicy.IsEligibleForCapacityCleanup(missingArchive, now, out _));
    }

    [Fact]
    public void Resolver_PrefersLocalThenArchive()
    {
        VideoRecord withLocal = VerifiedRecord("g.mp4", DateTime.Now.AddDays(-3), createLocal: true, createArchive: true);
        Assert.Equal(withLocal.FilePath, PlaybackFileResolver.ResolvePlaybackPath(withLocal));

        VideoRecord archiveOnly = VerifiedRecord("h.mp4", DateTime.Now.AddDays(-3), createLocal: false, createArchive: true);
        Assert.Equal(archiveOnly.ArchivePath, PlaybackFileResolver.ResolvePlaybackPath(archiveOnly));
    }

    [Fact]
    public void Resolver_RejectsUnverifiedAndMissingArchive()
    {
        VideoRecord pending = VerifiedRecord("i.mp4", DateTime.Now.AddDays(-3), createLocal: false, createArchive: true);
        pending.ArchiveStatus = VideoArchiveStatus.Pending;
        Assert.Equal("", PlaybackFileResolver.ResolvePlaybackPath(pending));

        VideoRecord missingArchive = VerifiedRecord("j.mp4", DateTime.Now.AddDays(-3), createLocal: false, createArchive: false);
        Assert.Equal("", PlaybackFileResolver.ResolvePlaybackPath(missingArchive));
    }

    [Fact]
    public void Resolver_MarkUnavailableInvalidatesArchiveCache()
    {
        VideoRecord archiveOnly = VerifiedRecord("k.mp4", DateTime.Now.AddDays(-3), createLocal: false, createArchive: true);
        Assert.Equal(archiveOnly.ArchivePath, PlaybackFileResolver.ResolvePlaybackPath(archiveOnly));

        PlaybackFileResolver.MarkUnavailable(archiveOnly.ArchivePath);
        Assert.Equal("", PlaybackFileResolver.ResolvePlaybackPath(archiveOnly));
    }

    [Fact]
    public void Resolver_UnarchivedLocalDeletedReturnsEmpty()
    {
        VideoRecord record = VerifiedRecord(
            "u.mp4",
            DateTime.Now.AddDays(-3),
            createLocal: false,
            createArchive: false);
        record.ArchiveStatus = VideoArchiveStatus.LocalDeleted;
        record.ArchivePath = "";
        record.ArchiveCompletedAt = null;

        Assert.Equal("", PlaybackFileResolver.ResolvePlaybackPath(record));
    }

    [Fact]
    public void RemoteProbe_ChecksFileAndSize()
    {
        string file = Path.Combine(_directory, "probe.bin");
        File.WriteAllText(file, "12345");
        Assert.True(RemoteFileProbe.TryProbeFile(file, TimeSpan.FromSeconds(2)));
        Assert.True(RemoteFileProbe.TryProbeFileWithSize(file, 5, TimeSpan.FromSeconds(2)));
        Assert.False(RemoteFileProbe.TryProbeFileWithSize(file, 4, TimeSpan.FromSeconds(2)));
        Assert.False(RemoteFileProbe.TryProbeFile(Path.Combine(_directory, "missing.bin"), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void EmergencyCleanupPolicy_RequiresEndedOldUnarchivedNonConflict()
    {
        DateTime now = new(2026, 8, 11, 12, 0, 0);
        VideoRecord eligible = VerifiedRecord("m.mp4", now.AddDays(-3), createLocal: true, createArchive: true);
        eligible.ArchiveStatus = VideoArchiveStatus.LocalOnly;
        eligible.EndTime = now.AddHours(-2);
        Assert.True(LocalCopyCleanupPolicy.IsEligibleForEmergencyCleanup(eligible, now, out _));

        VideoRecord recent = VerifiedRecord("n.mp4", now.AddDays(-3), createLocal: true, createArchive: true);
        recent.ArchiveStatus = VideoArchiveStatus.Pending;
        recent.EndTime = now.AddMinutes(-10);
        Assert.False(LocalCopyCleanupPolicy.IsEligibleForEmergencyCleanup(recent, now, out _));

        VideoRecord conflict = VerifiedRecord("o.mp4", now.AddDays(-3), createLocal: true, createArchive: true);
        conflict.ArchiveStatus = VideoArchiveStatus.Conflict;
        conflict.EndTime = now.AddHours(-2);
        Assert.False(LocalCopyCleanupPolicy.IsEligibleForEmergencyCleanup(conflict, now, out _));

        VideoRecord missing = VerifiedRecord("p.mp4", now.AddDays(-3), createLocal: false, createArchive: true);
        missing.ArchiveStatus = VideoArchiveStatus.Failed;
        missing.EndTime = now.AddHours(-2);
        Assert.False(LocalCopyCleanupPolicy.IsEligibleForEmergencyCleanup(missing, now, out _));
    }

    [Fact]
    public void EmergencyCleanupSnapshot_RejectsReplacedPathOrSize()
    {
        DateTime now = new(2026, 8, 11, 12, 0, 0);
        string oldPath = Path.Combine(_directory, "old-snapshot.mp4");
        string newPath = Path.Combine(_directory, "new-recording.mp4");
        File.WriteAllText(oldPath, "old");
        File.WriteAllText(newPath, "new");

        VideoRecord snapshot = new()
        {
            Id = 42,
            FilePath = oldPath,
            FileSizeBytes = 3,
            ArchiveStatus = VideoArchiveStatus.LocalOnly,
            EndTime = now.AddHours(-2)
        };
        VideoRecord replaced = new()
        {
            Id = 42,
            FilePath = newPath,
            FileSizeBytes = 3,
            ArchiveStatus = VideoArchiveStatus.LocalOnly,
            EndTime = now.AddHours(-2)
        };

        Assert.False(LocalCopyCleanupPolicy.IsEmergencyCleanupSnapshotCurrent(
            snapshot,
            replaced,
            now));
        Assert.True(File.Exists(newPath));

        replaced.FilePath = oldPath;
        replaced.FileSizeBytes = 99;
        Assert.False(LocalCopyCleanupPolicy.IsEmergencyCleanupSnapshotCurrent(
            snapshot,
            replaced,
            now));
        Assert.True(File.Exists(oldPath));

        replaced.FileSizeBytes = 3;
        replaced.ContentSha256 = "new-content";
        snapshot.ContentSha256 = "old-content";
        Assert.False(LocalCopyCleanupPolicy.IsEmergencyCleanupSnapshotCurrent(
            snapshot,
            replaced,
            now));
    }

    [Fact]
    public void EmergencyCleanupPolicy_ConstantsMatchPlan()
    {
        Assert.Equal(5L * 1024 * 1024 * 1024, LocalCopyCleanupPolicy.EmergencyCleanupThresholdBytes);
        Assert.Equal(TimeSpan.FromMinutes(30), LocalCopyCleanupPolicy.EmergencyDeleteGracePeriod);
    }

    [Fact]
    public void NetworkArchiveSpacePolicy_ReserveAndCooldown()
    {
        Assert.True(NetworkArchiveSpacePolicy.IsBelowReserve(100, 100));
        Assert.True(NetworkArchiveSpacePolicy.IsBelowReserve(99, 100));
        Assert.False(NetworkArchiveSpacePolicy.IsBelowReserve(101, 100));

        Assert.Equal(TimeSpan.FromMinutes(60), NetworkArchiveSpacePolicy.WarningCooldown);
        DateTime now = new(2026, 8, 11, 12, 0, 0);
        Assert.True(NetworkArchiveSpacePolicy.ShouldWarn(now.AddMinutes(-61), now));
        Assert.False(NetworkArchiveSpacePolicy.ShouldWarn(now.AddMinutes(-1), now));
    }

    [Fact]
    public void ProbeCacheWindow_Within24HoursSkipsRepeatedProbe()
    {
        DateTime now = new(2026, 8, 11, 12, 0, 0);
        Assert.Equal(TimeSpan.FromHours(24), LocalCopyCleanupPolicy.ProbeCacheWindow);

        var fresh = VerifiedRecord("q.mp4", now.AddDays(-3), createLocal: true, createArchive: true);
        fresh.ArchiveStatus = VideoArchiveStatus.Verified;
        fresh.ArchiveCompletedAt = now.AddHours(-1);
        fresh.LastArchiveProbeAt = now.AddHours(-1);
        Assert.True(LocalCopyCleanupPolicy.IsProbeFresh(fresh, now));
        Assert.False(LocalCopyCleanupPolicy.ShouldProbeBeforeLocalCleanup(fresh, now));

        fresh.LastArchiveProbeAt = now.AddHours(-25);
        Assert.False(LocalCopyCleanupPolicy.IsProbeFresh(fresh, now));
        Assert.True(LocalCopyCleanupPolicy.ShouldProbeBeforeLocalCleanup(fresh, now));

        fresh.LastArchiveProbeAt = null;
        Assert.True(LocalCopyCleanupPolicy.ShouldProbeBeforeLocalCleanup(fresh, now));
    }

    [Fact]
    public void RemoteProbe_TryProbeDirectoryDistinguishesDirectoryFromFile()
    {
        string dir = Path.Combine(_directory, "probe-dir");
        Directory.CreateDirectory(dir);

        Assert.True(RemoteFileProbe.TryProbeDirectory(dir, TimeSpan.FromSeconds(2)));
        Assert.False(RemoteFileProbe.TryProbeDirectory(
            Path.Combine(_directory, "missing-dir"),
            TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void EmergencyCleanupPolicy_ShouldTriggerWithoutArchiveOrWhenUnreachable()
    {
        Assert.True(LocalCopyCleanupPolicy.ShouldTriggerEmergencyCleanup("", false));
        Assert.True(LocalCopyCleanupPolicy.ShouldTriggerEmergencyCleanup(null, true));
        Assert.True(LocalCopyCleanupPolicy.ShouldTriggerEmergencyCleanup(@"\\nas\share", false));
        Assert.False(LocalCopyCleanupPolicy.ShouldTriggerEmergencyCleanup(@"\\nas\share", true));
    }

    [Fact]
    public void EmergencyCleanupPolicy_ReleaseTargetSubtractsNormalCleanup()
    {
        Assert.Equal(100, LocalCopyCleanupPolicy.ComputeEmergencyReleaseTarget(1000, 1000, 0));
        Assert.Equal(0, LocalCopyCleanupPolicy.ComputeEmergencyReleaseTarget(1000, 1000, 150));
        Assert.Equal(0, LocalCopyCleanupPolicy.ComputeEmergencyReleaseTarget(100, 1000, 0));
        Assert.Equal(0, LocalCopyCleanupPolicy.ComputeEmergencyReleaseTarget(1000, 1000, 1000));
    }

    [Fact]
    public void NetworkArchiveSpacePolicy_ClassifiesDiskFullErrors()
    {
        Assert.True(NetworkArchiveSpacePolicy.IsDiskFullException(
            new IOException("磁盘空间不足", 112)));
        Assert.True(NetworkArchiveSpacePolicy.IsDiskFullException(
            new IOException("磁盘空间不足", unchecked((int)0x80070070))));
        Assert.True(NetworkArchiveSpacePolicy.IsDiskFullException(
            new IOException("no space left on device")));
        Assert.True(NetworkArchiveSpacePolicy.IsDiskFullException(
            new IOException("disk full")));
        Assert.False(NetworkArchiveSpacePolicy.IsDiskFullException(
            new IOException("网络连接失败", 64)));
        Assert.False(NetworkArchiveSpacePolicy.IsDiskFullException(
            new InvalidOperationException("网络连接失败")));
    }

    [Fact]
    public void UnarchivedCleanupPolicy_TiersAndWarningCooldown()
    {
        Assert.Equal(
            TimeSpan.FromHours(6),
            LocalCopyCleanupPolicy.UnarchivedCleanupWarningCooldown);
        Assert.Equal(
            [
                VideoArchiveStatus.NasDeleted,
                VideoArchiveStatus.Failed,
                VideoArchiveStatus.Pending,
                VideoArchiveStatus.LocalOnly
            ],
            LocalCopyCleanupPolicy.UnarchivedCleanupTiers);
    }

    [Fact]
    public void CleanupPolicy_AcceptsNasDeletedLocalCopyAndRejectsRecent()
    {
        DateTime now = new(2026, 8, 11, 12, 0, 0);
        VideoRecord oldNasDeleted = VerifiedRecord(
            "nd1.mp4",
            now.AddDays(-3),
            createLocal: true,
            createArchive: false);
        oldNasDeleted.ArchiveStatus = VideoArchiveStatus.NasDeleted;
        oldNasDeleted.ArchiveCompletedAt = now.AddDays(-3);
        oldNasDeleted.ArchivePath = @"\\nas\share\old.mp4";
        Assert.True(LocalCopyCleanupPolicy.IsEligibleForCapacityCleanup(
            oldNasDeleted,
            now,
            out _));

        VideoRecord recentNasDeleted = VerifiedRecord(
            "nd2.mp4",
            now.AddHours(-1),
            createLocal: true,
            createArchive: false);
        recentNasDeleted.ArchiveStatus = VideoArchiveStatus.NasDeleted;
        Assert.False(LocalCopyCleanupPolicy.IsEligibleForCapacityCleanup(
            recentNasDeleted,
            now,
            out _));
    }

    [Fact]
    public void RemoteConfirmation_StaleAfterGraceWindow()
    {
        DateTime now = new(2026, 8, 11, 12, 0, 0);
        VideoRecord fresh = VerifiedRecord(
            "rc1.mp4",
            now.AddDays(-3),
            createLocal: true,
            createArchive: true);
        fresh.LastArchiveProbeAt = now.AddHours(-1);
        Assert.False(LocalCopyCleanupPolicy.IsRemoteConfirmationStale(fresh, now));

        fresh.LastArchiveProbeAt = now.AddHours(-25);
        Assert.True(LocalCopyCleanupPolicy.IsRemoteConfirmationStale(fresh, now));

        fresh.LastArchiveProbeAt = null;
        Assert.True(LocalCopyCleanupPolicy.IsRemoteConfirmationStale(fresh, now));
        Assert.Equal(
            TimeSpan.FromHours(24),
            LocalCopyCleanupPolicy.UnconfirmedRemoteCleanupGrace);
    }

    [Fact]
    public void RemoteProbe_TryProbeFileStateDistinguishesMissingAndUnavailable()
    {
        string file = Path.Combine(_directory, "tri-state.bin");
        File.WriteAllText(file, "12345");

        Assert.Equal(
            RemoteFileProbe.FileProbeState.Exists,
            RemoteFileProbe.TryProbeFileState(file, 5, TimeSpan.FromSeconds(2)));
        Assert.Equal(
            RemoteFileProbe.FileProbeState.Unavailable,
            RemoteFileProbe.TryProbeFileState(file, 4, TimeSpan.FromSeconds(2)));
        Assert.Equal(
            RemoteFileProbe.FileProbeState.ConfirmedMissing,
            RemoteFileProbe.TryProbeFileState(
                Path.Combine(_directory, "missing.bin"),
                5,
                TimeSpan.FromSeconds(2)));

        // 目录缺失属于“明确不存在”；非法字符触发参数错误 → 必须判定为不可判断
        string missingDirectory = Path.Combine(file, "sub", "x.mp4");
        Assert.Equal(
            RemoteFileProbe.FileProbeState.ConfirmedMissing,
            RemoteFileProbe.TryProbeFileState(
                missingDirectory,
                5,
                TimeSpan.FromSeconds(2)));
        Assert.Equal(
            RemoteFileProbe.FileProbeState.Unavailable,
            RemoteFileProbe.TryProbeFileState(
                Path.Combine(_directory, "bad|name.bin"),
                5,
                TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void RemoteProbe_TryProbeDirectoryStateDistinguishesReachableUnreachable()
    {
        string dir = Path.Combine(_directory, "probe-state-dir");
        Directory.CreateDirectory(dir);

        Assert.Equal(
            RemoteFileProbe.DirectoryProbeState.Reachable,
            RemoteFileProbe.TryProbeDirectoryState(dir, TimeSpan.FromSeconds(2)));
        Assert.Equal(
            RemoteFileProbe.DirectoryProbeState.Unreachable,
            RemoteFileProbe.TryProbeDirectoryState(
                Path.Combine(_directory, "missing-state-dir"),
                TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void FallbackCleanupPolicy_OnlyDeletesWhenConfirmedUnreachable()
    {
        Assert.True(LocalCopyCleanupPolicy.ShouldFallbackToUnarchivedCleanup(
            null,
            RemoteFileProbe.DirectoryProbeState.Busy));
        Assert.True(LocalCopyCleanupPolicy.ShouldFallbackToUnarchivedCleanup(
            "",
            RemoteFileProbe.DirectoryProbeState.Unreachable));
        Assert.True(LocalCopyCleanupPolicy.ShouldFallbackToUnarchivedCleanup(
            @"\\nas\share",
            RemoteFileProbe.DirectoryProbeState.Unreachable));
        Assert.False(LocalCopyCleanupPolicy.ShouldFallbackToUnarchivedCleanup(
            @"\\nas\share",
            RemoteFileProbe.DirectoryProbeState.Busy));
        Assert.False(LocalCopyCleanupPolicy.ShouldFallbackToUnarchivedCleanup(
            @"\\nas\share",
            RemoteFileProbe.DirectoryProbeState.Reachable));
    }
}
