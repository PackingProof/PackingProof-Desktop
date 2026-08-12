using ExpressPackingMonitoring.Data;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ArchiveBackupStatePolicyTests
{
    private static VideoRecord Record(
        string status,
        string reasonCode = "",
        bool hasCompletedAt = false,
        bool hasShaAndPath = false,
        bool deleted = false,
        bool ended = true) =>
        new()
        {
            ArchiveStatus = status,
            DeleteReasonCode = reasonCode,
            ArchiveCompletedAt = hasCompletedAt ? System.DateTime.Now : null,
            ContentSha256 = hasShaAndPath ? "h" : "",
            ArchivePath = hasShaAndPath ? @"\\nas\share\a.mp4" : "",
            IsDeleted = deleted,
            EndTime = ended ? System.DateTime.Now : System.DateTime.MinValue
        };

    [Fact]
    public void HasCompletedArchiveEvidence_OnlyHistoricalEvidence()
    {
        Assert.True(ArchiveBackupStatePolicy.HasCompletedArchiveEvidence(
            Record(VideoArchiveStatus.Verified, hasCompletedAt: true)));
        Assert.True(ArchiveBackupStatePolicy.HasCompletedArchiveEvidence(
            Record(VideoArchiveStatus.Verified, hasShaAndPath: true)));
        Assert.False(ArchiveBackupStatePolicy.HasCompletedArchiveEvidence(
            Record(VideoArchiveStatus.Pending)));
        // 历史证据不因记录已删除而消失
        Assert.True(ArchiveBackupStatePolicy.HasCompletedArchiveEvidence(
            Record(VideoArchiveStatus.Verified, hasCompletedAt: true, deleted: true)));
    }

    [Fact]
    public void IsBackupRemaining_CombinesStatusReasonAndEnded()
    {
        foreach (string status in ArchiveBackupStatePolicy.RemainingStatuses)
            Assert.True(ArchiveBackupStatePolicy.IsBackupRemaining(Record(status)));

        Assert.True(ArchiveBackupStatePolicy.IsBackupRemaining(Record(
            VideoArchiveStatus.LocalDeleted,
            reasonCode: RecordingDeletionReasonCode.CapacityCleanupUnconfirmedRemote)));
        Assert.False(ArchiveBackupStatePolicy.IsBackupRemaining(Record(
            VideoArchiveStatus.LocalDeleted,
            reasonCode: RecordingDeletionReasonCode.CapacityCleanupVerified,
            hasCompletedAt: true)));
        Assert.False(ArchiveBackupStatePolicy.IsBackupRemaining(Record(
            VideoArchiveStatus.LocalDeleted,
            reasonCode: RecordingDeletionReasonCode.ManualCleanup)));
        Assert.False(ArchiveBackupStatePolicy.IsBackupRemaining(Record(
            VideoArchiveStatus.Verified, hasCompletedAt: true)));
        Assert.False(ArchiveBackupStatePolicy.IsBackupRemaining(Record(
            VideoArchiveStatus.NasDeleted)));
        Assert.False(ArchiveBackupStatePolicy.IsBackupRemaining(Record(
            VideoArchiveStatus.Pending, deleted: true)));
        Assert.False(ArchiveBackupStatePolicy.IsBackupRemaining(Record(
            VideoArchiveStatus.Pending, ended: false)));
    }

    [Fact]
    public void IsCleanedUnbacked_OnlyActiveCleanupWithoutEvidence()
    {
        Assert.True(ArchiveBackupStatePolicy.IsCleanedUnbacked(Record(
            VideoArchiveStatus.LocalDeleted,
            reasonCode: RecordingDeletionReasonCode.ManualCleanup)));
        Assert.False(ArchiveBackupStatePolicy.IsCleanedUnbacked(Record(
            VideoArchiveStatus.LocalDeleted,
            reasonCode: RecordingDeletionReasonCode.ManualCleanup,
            hasCompletedAt: true)));
        Assert.False(ArchiveBackupStatePolicy.IsCleanedUnbacked(Record(
            VideoArchiveStatus.LocalDeleted,
            reasonCode: RecordingDeletionReasonCode.CapacityCleanupUnconfirmedRemote,
            hasCompletedAt: true)));
        Assert.False(ArchiveBackupStatePolicy.IsCleanedUnbacked(Record(
            VideoArchiveStatus.LocalDeleted,
            reasonCode: RecordingDeletionReasonCode.ManualCleanup,
            deleted: true)));
        Assert.False(ArchiveBackupStatePolicy.IsCleanedUnbacked(Record(
            VideoArchiveStatus.LocalDeleted,
            reasonCode: "UnknownReason")));
    }

    [Fact]
    public void RemainingStatuses_SingleSourceCoversAllCardCounts()
    {
        Assert.Contains(VideoArchiveStatus.LocalOnly, ArchiveBackupStatePolicy.RemainingStatuses);
        Assert.Contains(VideoArchiveStatus.Pending, ArchiveBackupStatePolicy.RemainingStatuses);
        Assert.Contains(VideoArchiveStatus.Copying, ArchiveBackupStatePolicy.RemainingStatuses);
        Assert.Contains(VideoArchiveStatus.Verifying, ArchiveBackupStatePolicy.RemainingStatuses);
        Assert.Contains(VideoArchiveStatus.Failed, ArchiveBackupStatePolicy.RemainingStatuses);
        Assert.Contains(VideoArchiveStatus.NASFull, ArchiveBackupStatePolicy.RemainingStatuses);
        Assert.Contains(VideoArchiveStatus.Conflict, ArchiveBackupStatePolicy.RemainingStatuses);
        Assert.Contains(VideoArchiveStatus.LocalMissingUnverified, ArchiveBackupStatePolicy.RemainingStatuses);
        Assert.Contains(VideoArchiveStatus.BackupLost, ArchiveBackupStatePolicy.RemainingStatuses);
        Assert.DoesNotContain(VideoArchiveStatus.Verified, ArchiveBackupStatePolicy.RemainingStatuses);
        Assert.DoesNotContain(VideoArchiveStatus.LocalDeleted, ArchiveBackupStatePolicy.RemainingStatuses);
        Assert.DoesNotContain(VideoArchiveStatus.NasDeleted, ArchiveBackupStatePolicy.RemainingStatuses);
        Assert.Equal(
            [RecordingDeletionReasonCode.ManualCleanup],
            ArchiveBackupStatePolicy.ActiveCleanupReasonCodes);
    }
}
