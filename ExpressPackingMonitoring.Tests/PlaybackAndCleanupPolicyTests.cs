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
        Assert.Equal(withLocal.FilePath, VideoFileResolver.ResolvePlaybackPath(withLocal));

        VideoRecord archiveOnly = VerifiedRecord("h.mp4", DateTime.Now.AddDays(-3), createLocal: false, createArchive: true);
        Assert.Equal(archiveOnly.ArchivePath, VideoFileResolver.ResolvePlaybackPath(archiveOnly));
    }

    [Fact]
    public void Resolver_RejectsUnverifiedAndMissingArchive()
    {
        VideoRecord pending = VerifiedRecord("i.mp4", DateTime.Now.AddDays(-3), createLocal: false, createArchive: true);
        pending.ArchiveStatus = VideoArchiveStatus.Pending;
        Assert.Equal("", VideoFileResolver.ResolvePlaybackPath(pending));

        VideoRecord missingArchive = VerifiedRecord("j.mp4", DateTime.Now.AddDays(-3), createLocal: false, createArchive: false);
        Assert.Equal("", VideoFileResolver.ResolvePlaybackPath(missingArchive));
    }

    [Fact]
    public void Resolver_MarkUnavailableInvalidatesArchiveCache()
    {
        VideoRecord archiveOnly = VerifiedRecord("k.mp4", DateTime.Now.AddDays(-3), createLocal: false, createArchive: true);
        Assert.Equal(archiveOnly.ArchivePath, VideoFileResolver.ResolvePlaybackPath(archiveOnly));

        VideoFileResolver.MarkUnavailable(archiveOnly.ArchivePath);
        Assert.Equal("", VideoFileResolver.ResolvePlaybackPath(archiveOnly));
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
    public void EmergencyCleanupPolicy_ConstantsMatchPlan()
    {
        Assert.Equal(5L * 1024 * 1024 * 1024, LocalCopyCleanupPolicy.EmergencyCleanupThresholdBytes);
        Assert.Equal(TimeSpan.FromMinutes(30), LocalCopyCleanupPolicy.EmergencyDeleteGracePeriod);
    }
}
