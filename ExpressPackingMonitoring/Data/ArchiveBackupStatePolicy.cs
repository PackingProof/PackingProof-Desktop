using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpressPackingMonitoring.Data;

/// <summary>
/// 录像备份“是否待备份”的统一判定层。
/// <see cref="HasCompletedArchiveEvidence"/> 仅表示历史上存在完成归档证据
/// （结果标记 ArchiveCompletedAt，或 ContentSha256 + ArchivePath），
/// 不代表当前远端副本仍存在或内容一致；
/// 当前可信副本 = 历史证据 + 当前三态探测 Exists（存在且大小一致）。
/// </summary>
public static class ArchiveBackupStatePolicy
{
    /// <summary>计入待备份的归档状态集合（单一事实来源，SQL 与卡片共用）。</summary>
    public static readonly string[] RemainingStatuses =
    [
        VideoArchiveStatus.LocalOnly,
        VideoArchiveStatus.Pending,
        VideoArchiveStatus.Copying,
        VideoArchiveStatus.Verifying,
        VideoArchiveStatus.Failed,
        VideoArchiveStatus.NASFull,
        VideoArchiveStatus.Conflict,
        VideoArchiveStatus.LocalMissingUnverified,
        VideoArchiveStatus.BackupLost
    ];

    /// <summary>明确“用户/策略主动清理”的原因码集合；新增主动清理原因只改这里。</summary>
    public static readonly string[] ActiveCleanupReasonCodes =
    [
        RecordingDeletionReasonCode.ManualCleanup
    ];

    /// <summary>
    /// 是否具备历史完成归档证据。仅表示“历史上归档成功过”，
    /// 绝不代表当前 NAS 副本仍存在或内容一致。
    /// </summary>
    public static bool HasCompletedArchiveEvidence(VideoRecord? record) =>
        record != null
        && (record.ArchiveCompletedAt != null
            || (!string.IsNullOrWhiteSpace(record.ContentSha256)
                && !string.IsNullOrWhiteSpace(record.ArchivePath)));

    /// <summary>LocalDeleted 且原因码为“容量清理时远端未确认”的记录。</summary>
    public static bool IsLocalDeletedUnconfirmed(VideoRecord? record) =>
        record != null
        && record.ArchiveStatus == VideoArchiveStatus.LocalDeleted
        && string.Equals(
            record.DeleteReasonCode,
            RecordingDeletionReasonCode.CapacityCleanupUnconfirmedRemote,
            StringComparison.Ordinal);

    /// <summary>
    /// “已清理且从未备份”：LocalDeleted + 主动清理原因 + 无历史完成证据。
    /// 不计入 RemainingCount，但卡片必须显示“已清理/无待备份”而非“已同步”。
    /// </summary>
    public static bool IsCleanedUnbacked(VideoRecord? record) =>
        record != null
        && !record.IsDeleted
        && record.ArchiveStatus == VideoArchiveStatus.LocalDeleted
        && ActiveCleanupReasonCodes.Contains(
            record.DeleteReasonCode,
            StringComparer.Ordinal)
        && !HasCompletedArchiveEvidence(record);

    /// <summary>当前是否仍属于“未完成可信备份”，应计入 RemainingCount。</summary>
    public static bool IsBackupRemaining(VideoRecord? record) =>
        record != null
        && !record.IsDeleted
        && record.EndTime != DateTime.MinValue
        && (RemainingStatuses.Contains(
                record.ArchiveStatus,
                StringComparer.Ordinal)
            || IsLocalDeletedUnconfirmed(record));

    /// <summary>生成计入状态的 SQL IN 子句（参数化前的常量片段，值均来自策略常量）。</summary>
    internal static string BuildRemainingStatusInClause() =>
        BuildInClause(RemainingStatuses);

    /// <summary>生成主动清理原因码的 SQL IN 子句。</summary>
    internal static string BuildActiveCleanupReasonInClause() =>
        BuildInClause(ActiveCleanupReasonCodes);

    private static string BuildInClause(IEnumerable<string> values)
    {
        var list = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => "'" + value.Replace("'", "''") + "'")
            .ToList();
        return list.Count == 0
            ? "('')"
            : "(" + string.Join(", ", list) + ")";
    }
}
