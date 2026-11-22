using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace ExpressPackingMonitoring.Data;

/// <summary>手动清理候选的完整统计，用于在分批执行前给出真实数量。</summary>
public sealed record ManualCleanupSelectionSummary(int Count, long Bytes);

public partial class VideoDatabase
{
    private const string HostUploadedStorageState = "Uploaded";
    private const string RemoteStorageState = "Remote";

    private const string ManualCleanupColumns = @"
        Id, OrderId, Mode, VideoCodec, VideoEncoder, FilePath, FileSizeBytes,
        StartTime, EndTime, DurationSeconds, StopReason,
        IsDeleted, DeletedAt, DeleteReason,
        TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo,
        OrderInfoPushTime, OrderInfoJson,
        SourceType, SourceDeviceId, SourceDeviceName, SourceSessionId, ContentSha256,
        StorageState, RemoteVideoRecordId, SourceDeviceKind,
        ArchivePath, ArchiveStatus, ArchiveRetryCount,
        NextRetryAt, LastArchiveAttemptAt, ArchiveCompletedAt,
        LastArchiveProbeAt,
        ArchiveError, LocalCopyDeletedAt, LocalDeleteReason, DeleteReasonCode";

    private const string ManualCleanupBaseFilter = @"
        IsDeleted = 0
        AND EndTime IS NOT NULL
        AND EndTime < @cutoff
        AND FilePath <> ''
        AND LocalCopyDeletedAt IS NULL
        AND COALESCE(StorageState, '') <> 'Remote'
        AND ArchiveStatus IN ('Verified', 'Failed', 'Pending', 'LocalOnly', 'NasDeleted')
        AND (ArchiveStatus <> 'Verified' OR ArchiveCompletedAt IS NOT NULL)";

    private const string HostUploadedBackupPredicate = @"
        (
            (ArchiveStatus = 'Verified' AND ArchiveCompletedAt IS NOT NULL)
            OR (
                StorageState = 'Uploaded'
                AND RemoteVideoRecordId IS NOT NULL
                AND RemoteVideoRecordId > 0
            )
        )";

    private const string UnarchivedPredicate =
        "NOT (" + HostUploadedBackupPredicate + ")";

    /// <summary>
    /// 手动清理候选：本地录像已定稿、未删除、结束时间早于截止时间，
    /// 且文件位于任一托管本地根目录下。调用方可按状态分档并分批取完。
    /// </summary>
    public IReadOnlyList<VideoRecord> GetManualCleanupCandidates(
        DateTime cutoff,
        IReadOnlyList<string> rootPrefixes,
        int limit = 200,
        string? archiveStatus = null) =>
        GetManualCleanupRows(
            cutoff,
            rootPrefixes,
            limit,
            string.IsNullOrWhiteSpace(archiveStatus)
                ? ""
                : "AND ArchiveStatus = @archiveStatus",
            archiveStatus);

    /// <summary>
    /// 已有可信备份的本地副本：NAS 已校验，或录像已成功上传并由备份主机校验。
    /// 后者属于录像从机的缓存，不能被误报成“尚未备份到 NAS”。
    /// </summary>
    public IReadOnlyList<VideoRecord> GetManualCleanupBackedUpCandidates(
        DateTime cutoff,
        IReadOnlyList<string> rootPrefixes,
        int limit = 200) =>
        GetManualCleanupRows(
            cutoff,
            rootPrefixes,
            limit,
            "AND " + HostUploadedBackupPredicate,
            null);

    /// <summary>手动清理预览统计：条数、字节数与尚未上传或归档条数。</summary>
    public ManualCleanupPreview GetManualCleanupPreview(
        DateTime cutoff,
        IReadOnlyList<string> rootPrefixes)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"
                SELECT COUNT(1),
                       COALESCE(SUM(FileSizeBytes), 0),
                       COALESCE(SUM(CASE WHEN {UnarchivedPredicate} THEN 1 ELSE 0 END), 0)
                FROM VideoRecords
                WHERE {ManualCleanupBaseFilter}
                  {BuildRootPrefixFilter(rootPrefixes)};";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("yyyy-MM-dd HH:mm:ss"));
            AddRootPrefixParameters(cmd, rootPrefixes);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return new ManualCleanupPreview(
                Convert.ToInt32(reader.GetInt64(0)),
                reader.GetInt64(1),
                Convert.ToInt32(reader.GetInt64(2)));
        }
    }

    /// <summary>未完成任何可信备份的候选总数，不受单批 200 条限制。</summary>
    public ManualCleanupSelectionSummary GetManualCleanupUnarchivedSummary(
        DateTime cutoff,
        IReadOnlyList<string> rootPrefixes)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"
                SELECT COUNT(1), COALESCE(SUM(FileSizeBytes), 0)
                FROM VideoRecords
                WHERE {ManualCleanupBaseFilter}
                  AND {UnarchivedPredicate}
                  {BuildRootPrefixFilter(rootPrefixes)};";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("yyyy-MM-dd HH:mm:ss"));
            AddRootPrefixParameters(cmd, rootPrefixes);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return new ManualCleanupSelectionSummary(
                Convert.ToInt32(reader.GetInt64(0)),
                reader.GetInt64(1));
        }
    }

    private IReadOnlyList<VideoRecord> GetManualCleanupRows(
        DateTime cutoff,
        IReadOnlyList<string> rootPrefixes,
        int limit,
        string extraFilter,
        string? archiveStatus)
    {
        limit = Math.Clamp(limit, 1, 500);
        lock (_lock)
        {
            var results = new List<VideoRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"
                SELECT {ManualCleanupColumns}
                FROM VideoRecords
                WHERE {ManualCleanupBaseFilter}
                  {BuildRootPrefixFilter(rootPrefixes)}
                  {extraFilter}
                ORDER BY
                    CASE ArchiveStatus
                        WHEN 'Verified' THEN 0
                        WHEN 'NasDeleted' THEN 1
                        WHEN 'Failed' THEN 2
                        WHEN 'Pending' THEN 3
                        ELSE 4
                    END,
                    EndTime ASC,
                    Id ASC
                LIMIT @limit;";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@limit", limit);
            if (!string.IsNullOrWhiteSpace(archiveStatus))
                cmd.Parameters.AddWithValue("@archiveStatus", archiveStatus);
            AddRootPrefixParameters(cmd, rootPrefixes);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(ReadVideoRecord(reader));
            return results;
        }
    }

    private static string BuildRootPrefixFilter(IReadOnlyList<string> rootPrefixes)
    {
        int count = 0;
        foreach (string prefix in rootPrefixes)
        {
            if (!string.IsNullOrWhiteSpace(prefix))
                count++;
        }
        if (count == 0)
            return "AND 0";

        var conditions = new List<string>();
        for (int index = 0; index < rootPrefixes.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(rootPrefixes[index]))
                continue;
            conditions.Add($"FilePath LIKE @root{index} ESCAPE '\\'");
        }
        return "AND (" + string.Join(" OR ", conditions) + ")";
    }

    private static void AddRootPrefixParameters(
        SqliteCommand cmd,
        IReadOnlyList<string> rootPrefixes)
    {
        for (int index = 0; index < rootPrefixes.Count; index++)
        {
            string prefix = rootPrefixes[index];
            if (string.IsNullOrWhiteSpace(prefix))
                continue;
            cmd.Parameters.AddWithValue(
                $"@root{index}",
                EscapeLikePattern(prefix) + "%");
        }
    }

    private static string EscapeLikePattern(string value) =>
        value
            .Replace(@"\", @"\\")
            .Replace("%", @"\%")
            .Replace("_", @"\_");
}
