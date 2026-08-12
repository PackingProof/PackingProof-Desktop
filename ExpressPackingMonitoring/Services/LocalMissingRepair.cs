using ExpressPackingMonitoring.Data;
using System;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 本地文件缺失的统一判定与修复：不按 ArchiveStatus 硬编码，
/// 而是按“历史完成归档证据 + 当前三态探测”决定 LocalDeleted（确认）/
/// LocalMissingUnverified（待核实）/ BackupLost（备份丢失）。
/// 手动清理、自动 GC、归档 Worker 与对账共用本服务。
/// </summary>
internal static class LocalMissingRepair
{
    internal enum Disposition
    {
        KeepCurrent,
        LocalDeletedConfirmed,
        LocalMissingUnverified,
        BackupLost
    }

    private const string ConfirmedLocalMissingReason =
        "本地文件已缺失，NAS 归档已确认";
    private const string PendingVerificationReason =
        "本地副本缺失，等待确认 NAS 归档";
    private const string BackupLostReason =
        "本地与 NAS 均无可信副本（外部删除或缺失）";
    private const string ConflictBackupLostReason =
        "归档冲突且本地副本丢失";

    internal static Disposition Resolve(
        VideoRecord record,
        RemoteFileProbe.FileProbeState probe)
    {
        if (record == null || record.IsDeleted)
            return Disposition.KeepCurrent;

        // ManualCleanup 是用户主动清理：不推论 NAS 存在、不因缺失转异常
        if (record.ArchiveStatus == VideoArchiveStatus.LocalDeleted
            && string.Equals(
                record.DeleteReasonCode,
                RecordingDeletionReasonCode.ManualCleanup,
                StringComparison.Ordinal))
        {
            return Disposition.KeepCurrent;
        }

        if (record.ArchiveStatus == VideoArchiveStatus.NasDeleted)
        {
            // 有本程序策略删除证据 → 正常策略终态；否则视为外部丢失
            return string.Equals(
                    record.DeleteReasonCode,
                    RecordingDeletionReasonCode.NasCapacityCleanup,
                    StringComparison.Ordinal)
                ? Disposition.KeepCurrent
                : Disposition.BackupLost;
        }

        if (record.ArchiveStatus == VideoArchiveStatus.Conflict)
            return Disposition.BackupLost;

        if (record.ArchiveStatus == VideoArchiveStatus.LocalDeleted)
        {
            return probe switch
            {
                RemoteFileProbe.FileProbeState.Exists
                    when ArchiveBackupStatePolicy.HasCurrentTrustedRemoteCopy(
                        record,
                        RemoteFileProbe.FileProbeState.Exists) =>
                    Disposition.LocalDeletedConfirmed,
                RemoteFileProbe.FileProbeState.ConfirmedMissing =>
                    Disposition.BackupLost,
                _ => Disposition.KeepCurrent
            };
        }

        // LocalOnly / Pending / Copying / Verifying / Failed / NASFull / Verified / LocalMissingUnverified
        return probe switch
        {
            RemoteFileProbe.FileProbeState.ConfirmedMissing =>
                Disposition.BackupLost,
            RemoteFileProbe.FileProbeState.Exists
                when ArchiveBackupStatePolicy.HasCurrentTrustedRemoteCopy(
                    record,
                    RemoteFileProbe.FileProbeState.Exists) =>
                Disposition.LocalDeletedConfirmed,
            _ => Disposition.LocalMissingUnverified
        };
    }

    /// <summary>执行判定结果对应的数据库迁移；返回是否发生了状态变化。</summary>
    internal static bool Apply(
        VideoDatabase database,
        VideoRecord record,
        RemoteFileProbe.FileProbeState probe)
    {
        if (database == null || record == null)
            return false;

        switch (Resolve(record, probe))
        {
            case Disposition.LocalDeletedConfirmed:
                if (record.ArchiveStatus == VideoArchiveStatus.LocalDeleted)
                {
                    database.MarkLocalCleanupConfirmed(record.Id, DateTime.Now);
                }
                else
                {
                    database.MarkLocalCopyDeleted(
                        record.Id,
                        ConfirmedLocalMissingReason,
                        RecordingDeletionReasonCode.CapacityCleanupVerified);
                }
                database.UpdateLastArchiveProbeAt(record.Id, DateTime.Now);
                return true;
            case Disposition.LocalMissingUnverified:
                database.MarkLocalMissingUnverified(
                    record.Id,
                    record.ArchivePath,
                    PendingVerificationReason,
                    "");
                return true;
            case Disposition.BackupLost:
                bool conflict = record.ArchiveStatus == VideoArchiveStatus.Conflict;
                database.MarkBackupLost(
                    record.Id,
                    record.ArchivePath,
                    conflict ? ConflictBackupLostReason : BackupLostReason,
                    conflict
                        ? RecordingDeletionReasonCode.ConflictLocalMissing
                        : RecordingDeletionReasonCode.BackupLost);
                return true;
            default:
                return false;
        }
    }
}
