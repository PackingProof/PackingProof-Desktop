using System;
using System.Collections.Generic;
using System.Linq;
using ExpressPackingMonitoring.Services;

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
        VideoArchiveStatus.BackupLost,
        VideoArchiveStatus.SharedFileMigrationPending
    ];

    internal static readonly string[] PendingStatuses =
        [VideoArchiveStatus.Pending];
    internal static readonly string[] UploadingStatuses =
        [VideoArchiveStatus.Copying, VideoArchiveStatus.Verifying];
    internal static readonly string[] FailedStatuses =
        [VideoArchiveStatus.Failed];
    internal static readonly string[] NasFullStatuses =
        [VideoArchiveStatus.NASFull];
    internal static readonly string[] LocalOnlyStatuses =
        [VideoArchiveStatus.LocalOnly];
    internal static readonly string[] ConflictStatuses =
        [VideoArchiveStatus.Conflict];
    internal static readonly string[] PendingVerificationStatuses =
        [VideoArchiveStatus.LocalMissingUnverified, VideoArchiveStatus.SharedFileMigrationPending];
    internal static readonly string[] LostStatuses =
        [VideoArchiveStatus.BackupLost];

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

    /// <summary>
    /// 当前是否存在可信远端副本（统一判定，唯一允许进入“LocalDeleted 已确认语义”的门槛）：
    /// 历史完成归档证据 + 当前三态探测为 Exists（存在且大小一致）。
    /// </summary>
    internal static bool HasCurrentTrustedRemoteCopy(
        VideoRecord? record,
        RemoteFileProbe.FileProbeState probeState) =>
        HasCompletedArchiveEvidence(record)
        && probeState == RemoteFileProbe.FileProbeState.Exists;

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

    /// <summary>“无历史完成证据”的 SQL 条件，与 C# 判定共用同一语义。</summary>
    internal static string BuildNoCompletedEvidenceSql() =>
        "NOT (ArchiveCompletedAt IS NOT NULL "
        + "OR (ContentSha256 <> '' AND ArchivePath <> ''))";

    /// <summary>LocalDeleted + 未确认远端原因码的 SQL 条件。</summary>
    internal static string BuildLocalDeletedUnconfirmedSql() =>
        "ArchiveStatus = 'LocalDeleted' AND DeleteReasonCode = '"
        + RecordingDeletionReasonCode.CapacityCleanupUnconfirmedRemote
        + "'";

    /// <summary>
    /// 生成归档队列统计的 SELECT 计数表达式；所有状态集合都来自策略常量，禁止手写状态。
    /// </summary>
    internal static string BuildArchiveQueueSummarySelectSql() =>
        "SUM(CASE WHEN ArchiveStatus IN " + BuildInClause(PendingStatuses) + " THEN 1 ELSE 0 END), "
        + "SUM(CASE WHEN ArchiveStatus IN " + BuildInClause(UploadingStatuses) + " THEN 1 ELSE 0 END), "
        + "SUM(CASE WHEN ArchiveStatus IN " + BuildInClause(FailedStatuses) + " THEN 1 ELSE 0 END), "
        + "SUM(CASE WHEN ArchiveStatus IN " + BuildInClause(NasFullStatuses) + " THEN 1 ELSE 0 END), "
        + "SUM(CASE WHEN ArchiveStatus IN " + BuildInClause(LocalOnlyStatuses) + " THEN 1 ELSE 0 END), "
        + "SUM(CASE WHEN ArchiveStatus IN " + BuildInClause(ConflictStatuses) + " THEN 1 ELSE 0 END), "
        + "SUM(CASE WHEN ArchiveStatus IN " + BuildInClause(PendingVerificationStatuses) + " THEN 1 "
        + "     WHEN " + BuildLocalDeletedUnconfirmedSql() + " THEN 1 ELSE 0 END), "
        + "SUM(CASE WHEN ArchiveStatus IN " + BuildInClause(LostStatuses) + " THEN 1 ELSE 0 END), "
        + "SUM(CASE WHEN ArchiveStatus = 'LocalDeleted' AND DeleteReasonCode IN "
        + BuildInClause(ActiveCleanupReasonCodes) + " AND "
        + BuildNoCompletedEvidenceSql() + " THEN 1 ELSE 0 END)";

    /// <summary>
    /// 生成统计行的过滤条件：只统计策略认定“计入待备份”或“已清理且从未备份”的记录，
    /// 状态集合只能来自 RemainingStatuses / ActiveCleanupReasonCodes。
    /// </summary>
    internal static string BuildSummaryRowFilterSql() =>
        "(ArchiveStatus IN " + BuildRemainingStatusInClause() + ") OR "
        + BuildLocalDeletedUnconfirmedSql() + " OR "
        + "(ArchiveStatus = 'LocalDeleted' AND DeleteReasonCode IN "
        + BuildInClause(ActiveCleanupReasonCodes) + " AND "
        + BuildNoCompletedEvidenceSql() + ")";

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
