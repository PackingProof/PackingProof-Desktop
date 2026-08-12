using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class LocalMissingRepairTests
{
    private static VideoRecord Record(
        string status,
        string reasonCode = "",
        bool hasCompletedAt = false,
        bool hasShaAndPath = false) =>
        new()
        {
            ArchiveStatus = status,
            DeleteReasonCode = reasonCode,
            ArchiveCompletedAt = hasCompletedAt ? System.DateTime.Now : null,
            ContentSha256 = hasShaAndPath ? "h" : "",
            ArchivePath = hasShaAndPath ? @"\\nas\share\a.mp4" : @"\\nas\share\a.mp4",
            FileSizeBytes = 100
        };

    private static void AssertDisposition(
        VideoRecord record,
        RemoteFileProbe.FileProbeState probe,
        LocalMissingRepair.Disposition expected) =>
        Assert.Equal(expected, LocalMissingRepair.Resolve(record, probe));

    [Fact]
    public void VerifiedMissing_Unavailable_GoesToPendingVerification()
    {
        AssertDisposition(
            Record(VideoArchiveStatus.Verified, hasCompletedAt: true),
            RemoteFileProbe.FileProbeState.Unavailable,
            LocalMissingRepair.Disposition.LocalMissingUnverified);
    }

    [Fact]
    public void CopyingOrVerifyingMissing_ExistsWithoutEvidence_StaysPendingVerification()
    {
        AssertDisposition(
            Record(VideoArchiveStatus.Copying),
            RemoteFileProbe.FileProbeState.Exists,
            LocalMissingRepair.Disposition.LocalMissingUnverified);
        AssertDisposition(
            Record(VideoArchiveStatus.Verifying),
            RemoteFileProbe.FileProbeState.Exists,
            LocalMissingRepair.Disposition.LocalMissingUnverified);
    }

    [Fact]
    public void NasFullMissing_JudgedByEvidenceAndProbe()
    {
        AssertDisposition(
            Record(VideoArchiveStatus.NASFull),
            RemoteFileProbe.FileProbeState.ConfirmedMissing,
            LocalMissingRepair.Disposition.BackupLost);
        AssertDisposition(
            Record(VideoArchiveStatus.NASFull),
            RemoteFileProbe.FileProbeState.Unavailable,
            LocalMissingRepair.Disposition.LocalMissingUnverified);
    }

    [Fact]
    public void ConflictMissing_AlwaysBackupLostWithConflictReason()
    {
        AssertDisposition(
            Record(VideoArchiveStatus.Conflict),
            RemoteFileProbe.FileProbeState.ConfirmedMissing,
            LocalMissingRepair.Disposition.BackupLost);
        AssertDisposition(
            Record(VideoArchiveStatus.Conflict),
            RemoteFileProbe.FileProbeState.Unavailable,
            LocalMissingRepair.Disposition.BackupLost);
    }

    [Fact]
    public void VerifiedMissing_ExistsWithEvidence_ConfirmsLocalDeleted()
    {
        AssertDisposition(
            Record(VideoArchiveStatus.Verified, hasCompletedAt: true),
            RemoteFileProbe.FileProbeState.Exists,
            LocalMissingRepair.Disposition.LocalDeletedConfirmed);
    }

    [Fact]
    public void UnarchivedMissing_ConfirmedMissing_IsBackupLost()
    {
        foreach (string status in new[]
                 {
                     VideoArchiveStatus.LocalOnly,
                     VideoArchiveStatus.Pending,
                     VideoArchiveStatus.Failed
                 })
        {
            AssertDisposition(
                Record(status),
                RemoteFileProbe.FileProbeState.ConfirmedMissing,
                LocalMissingRepair.Disposition.BackupLost);
        }
        AssertDisposition(
            Record(VideoArchiveStatus.Pending),
            RemoteFileProbe.FileProbeState.Exists,
            LocalMissingRepair.Disposition.LocalMissingUnverified);
    }

    [Fact]
    public void ManualCleanupLocalDeleted_NeverTransitions()
    {
        AssertDisposition(
            Record(
                VideoArchiveStatus.LocalDeleted,
                reasonCode: RecordingDeletionReasonCode.ManualCleanup),
            RemoteFileProbe.FileProbeState.ConfirmedMissing,
            LocalMissingRepair.Disposition.KeepCurrent);
        AssertDisposition(
            Record(
                VideoArchiveStatus.LocalDeleted,
                reasonCode: RecordingDeletionReasonCode.ManualCleanup),
            RemoteFileProbe.FileProbeState.Unavailable,
            LocalMissingRepair.Disposition.KeepCurrent);
    }

    [Fact]
    public void NasDeletedMissing_SplitsByPolicyEvidence()
    {
        AssertDisposition(
            Record(
                VideoArchiveStatus.NasDeleted,
                reasonCode: RecordingDeletionReasonCode.NasCapacityCleanup),
            RemoteFileProbe.FileProbeState.ConfirmedMissing,
            LocalMissingRepair.Disposition.KeepCurrent);
        AssertDisposition(
            Record(VideoArchiveStatus.NasDeleted),
            RemoteFileProbe.FileProbeState.ConfirmedMissing,
            LocalMissingRepair.Disposition.BackupLost);
    }

    [Fact]
    public void LocalDeletedUnconfirmed_ExistsWithEvidence_ConfirmsReasonCode()
    {
        AssertDisposition(
            Record(
                VideoArchiveStatus.LocalDeleted,
                reasonCode: RecordingDeletionReasonCode.CapacityCleanupUnconfirmedRemote,
                hasCompletedAt: true),
            RemoteFileProbe.FileProbeState.Exists,
            LocalMissingRepair.Disposition.LocalDeletedConfirmed);
        AssertDisposition(
            Record(
                VideoArchiveStatus.LocalDeleted,
                reasonCode: RecordingDeletionReasonCode.CapacityCleanupUnconfirmedRemote,
                hasCompletedAt: true),
            RemoteFileProbe.FileProbeState.Unavailable,
            LocalMissingRepair.Disposition.KeepCurrent);
        AssertDisposition(
            Record(
                VideoArchiveStatus.LocalDeleted,
                reasonCode: RecordingDeletionReasonCode.CapacityCleanupUnconfirmedRemote,
                hasCompletedAt: true),
            RemoteFileProbe.FileProbeState.ConfirmedMissing,
            LocalMissingRepair.Disposition.BackupLost);
    }
}
