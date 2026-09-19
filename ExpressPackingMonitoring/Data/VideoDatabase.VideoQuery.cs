#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.Data;

/// <summary>
/// 录像列表的查询构造：字段清单、WHERE 拼装与分页/游标/整表三种读取入口。
/// 从规模冻结的 VideoDatabase.cs 抽出来，来源筛选的匹配规则也集中在这里。
/// </summary>
public partial class VideoDatabase
{
        private const string VideoRecordSelectColumns = @"
            SELECT Id, OrderId, Mode, VideoCodec, VideoEncoder, FilePath, FileSizeBytes,
                   StartTime, EndTime, DurationSeconds, StopReason,
                   IsDeleted, DeletedAt, DeleteReason,
                   TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo, OrderInfoPushTime, OrderInfoJson,
                   SourceType, SourceDeviceId, SourceDeviceName, SourceSessionId, ContentSha256,
                   StorageState, RemoteVideoRecordId, SourceDeviceKind,
                   ArchivePath, ArchiveStatus, ArchiveRetryCount,
                   NextRetryAt, LastArchiveAttemptAt, ArchiveCompletedAt,
                   LastArchiveProbeAt,
                   ArchiveError, LocalCopyDeletedAt, LocalDeleteReason, DeleteReasonCode ";

        private static (string WhereSql, List<(string Name, object Value)> Parameters) BuildVideoQueryWhere(
            DateTime? startDate,
            DateTime? endDate,
            string keyword,
            bool includeDeleted,
            VideoSearchMode searchMode,
            string sourceType,
            string deviceId,
            string sourceDeviceName,
            string mode = "",
            IReadOnlyList<string> deviceIds = null,
            IReadOnlyList<string> keywordDeviceIds = null)
        {
            string normalizedKeyword = keyword?.Trim() ?? "";
            string normalizedSourceType = sourceType?.Trim().ToLowerInvariant() ?? "";
            string normalizedDeviceId = deviceId?.Trim() ?? "";
            string normalizedSourceDeviceName = sourceDeviceName?.Trim() ?? "";
            List<string> normalizedDeviceIds = NormalizeDeviceIds(deviceIds);
            List<string> normalizedKeywordDeviceIds = NormalizeDeviceIds(keywordDeviceIds);
            // 记了设备号集合就说明这个选项来自多台同名设备（或改名后的同一台设备），
            // 必须按身份命中：按名字匹配会漏掉它们改名前的记录。
            if (normalizedSourceType.Length == 0 && (normalizedDeviceId.Length > 0 || normalizedDeviceIds.Count > 0))
                normalizedSourceType = "external";

            string whereSql = @"
                FROM VideoRecords
                WHERE 1 = 1";
            var parameters = new List<(string Name, object Value)>();

            if (!string.IsNullOrEmpty(mode))
            {
                if (mode is not ("shipping" or "return")) throw new ArgumentException("Invalid recording mode", nameof(mode));
                whereSql += mode == "return"
                    ? " AND Mode IN ('return', '退货')"
                    : " AND (Mode IN ('shipping', '发货', '') OR Mode IS NULL)";
            }

            if (startDate.HasValue)
                whereSql += " AND StartTime >= @startDate";

            if (endDate.HasValue)
                whereSql += " AND StartTime < @endDate";

            if (!includeDeleted)
                whereSql += " AND IsDeleted = 0";

            if (normalizedSourceType is "pc" or "external")
            {
                whereSql += " AND SourceType = @sourceType";
                parameters.Add(("sourceType", normalizedSourceType));
            }

            if (normalizedSourceType == "external" && normalizedDeviceIds.Count > 0)
            {
                var placeholders = new List<string>(normalizedDeviceIds.Count);
                for (int index = 0; index < normalizedDeviceIds.Count; index++)
                {
                    placeholders.Add($"@deviceId{index}");
                    parameters.Add(($"deviceId{index}", normalizedDeviceIds[index]));
                }

                whereSql += $" AND SourceDeviceId IN ({string.Join(", ", placeholders)})";
            }
            else if (normalizedSourceType == "external" && normalizedDeviceId.Length > 0)
            {
                whereSql += " AND SourceDeviceId = @deviceId";
                parameters.Add(("deviceId", normalizedDeviceId));
            }
            else if (normalizedSourceType == "external" && normalizedSourceDeviceName.Length > 0)
            {
                whereSql += " AND SourceDeviceName = @sourceDeviceName";
                parameters.Add(("sourceDeviceName", normalizedSourceDeviceName));
            }

            if (normalizedKeyword.Length > 0)
            {
                if (searchMode == VideoSearchMode.ExactOrderIdentifiers)
                {
                    whereSql += ExactRecordingIdentitySearch(normalizedKeyword);
                    parameters.Add(("keyword", normalizedKeyword));
                }
                else if (searchMode == VideoSearchMode.OrderIdentifierContains)
                {
                    whereSql += @" AND (
                        OrderId LIKE @keyword OR TrackingNumber LIKE @keyword OR SourceOrderId LIKE @keyword)";
                    parameters.Add(("keyword", $"%{normalizedKeyword}%"));
                }
                else
                {
                    // 关键字命中设备名时，同时按该设备的设备号命中：
                    // 记录里存的是写入当时的名字快照，设备改名后只用新昵称搜不到改名前的录像。
                    string deviceNameMatch = @"SourceDeviceName LIKE @keyword";
                    if (normalizedKeywordDeviceIds.Count > 0)
                    {
                        var namePlaceholders = new List<string>(normalizedKeywordDeviceIds.Count);
                        for (int index = 0; index < normalizedKeywordDeviceIds.Count; index++)
                        {
                            namePlaceholders.Add($"@keywordDeviceId{index}");
                            parameters.Add(($"keywordDeviceId{index}", normalizedKeywordDeviceIds[index]));
                        }

                        deviceNameMatch += $" OR SourceDeviceId IN ({string.Join(", ", namePlaceholders)})";
                    }

                    whereSql += $@" AND (
                        OrderId LIKE @keyword OR FilePath LIKE @keyword OR TrackingNumber LIKE @keyword
                        OR SourceOrderId LIKE @keyword OR BuyerMessage LIKE @keyword
                        OR SellerMemo LIKE @keyword OR ProductInfo LIKE @keyword
                        OR {deviceNameMatch})";
                    parameters.Add(("keyword", $"%{normalizedKeyword}%"));
                }
            }

            if (startDate.HasValue)
                parameters.Add(("startDate", startDate.Value.ToString("yyyy-MM-dd 00:00:00")));
            if (endDate.HasValue)
                parameters.Add(("endDate", endDate.Value.AddDays(1).ToString("yyyy-MM-dd 00:00:00")));

            return (whereSql, parameters);
        }

        /// <summary>规范化来源筛选的设备号集合：去空白、去重、忽略大小写。</summary>
        private static List<string> NormalizeDeviceIds(IReadOnlyList<string> deviceIds)
        {
            if (deviceIds == null || deviceIds.Count == 0)
                return new List<string>();

            var normalized = new List<string>(deviceIds.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string deviceId in deviceIds)
            {
                string value = deviceId?.Trim() ?? "";
                if (value.Length == 0 || !seen.Add(value))
                    continue;

                normalized.Add(value);
            }

            return normalized;
        }

        internal PagedVideoResult QueryVideosPaged(
            DateTime? startDate,
            DateTime? endDate,
            string keyword,
            int page,
            int pageSize,
            bool includeDeleted,
            VideoSearchMode searchMode,
            string sourceType = "",
            string deviceId = "",
            string sourceDeviceName = "",
            string mode = "",
            IReadOnlyList<string> deviceIds = null,
            IReadOnlyList<string> keywordDeviceIds = null)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            int offset = (page - 1) * pageSize;

            lock (_lock)
            {
                (string whereSql, List<(string Name, object Value)> parameters) =
                    BuildVideoQueryWhere(startDate, endDate, keyword, includeDeleted, searchMode, sourceType, deviceId, sourceDeviceName, mode, deviceIds, keywordDeviceIds);

                using var countCmd = _connection.CreateCommand();
                countCmd.CommandText = "SELECT COUNT(1) " + whereSql + ";";
                foreach ((string name, object value) in parameters)
                    countCmd.Parameters.AddWithValue("@" + name, value);
                int total = Convert.ToInt32(countCmd.ExecuteScalar());

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = VideoRecordSelectColumns
                     + whereSql + @"
                    ORDER BY StartTime DESC, Id DESC
                    LIMIT @limit OFFSET @offset;";
                foreach ((string name, object value) in parameters)
                    cmd.Parameters.AddWithValue("@" + name, value);
                cmd.Parameters.AddWithValue("@limit", pageSize);
                cmd.Parameters.AddWithValue("@offset", offset);

                var records = new List<VideoRecord>(pageSize);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    records.Add(ReadVideoRecord(reader));

                return new PagedVideoResult { Total = total, Records = records };
            }
        }

        /// <summary>
        /// Reads one bounded playback window without performing a COUNT query.
        /// The extra row is used only to determine whether another window exists.
        /// </summary>
        internal CursorVideoResult QueryVideosWindow(
            DateTime? startDate,
            DateTime? endDate,
            string keyword,
            int page,
            int pageSize,
            bool includeDeleted,
            VideoSearchMode searchMode,
            string mode = "")
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            int offset = (page - 1) * pageSize;

            lock (_lock)
            {
                (string whereSql, List<(string Name, object Value)> parameters) =
                    BuildVideoQueryWhere(startDate, endDate, keyword, includeDeleted, searchMode, "", "", "", mode);

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = VideoRecordSelectColumns + whereSql + @"
                    ORDER BY StartTime DESC, Id DESC
                    LIMIT @limit OFFSET @offset;";
                foreach ((string name, object value) in parameters)
                    cmd.Parameters.AddWithValue("@" + name, value);
                cmd.Parameters.AddWithValue("@limit", pageSize + 1);
                cmd.Parameters.AddWithValue("@offset", offset);

                using var countCmd = _connection.CreateCommand();
                countCmd.CommandText = "SELECT COUNT(1) " + whereSql + ";";
                foreach ((string name, object value) in parameters)
                    countCmd.Parameters.AddWithValue("@" + name, value);
                int total = Convert.ToInt32(countCmd.ExecuteScalar());

                var records = new List<VideoRecord>(pageSize + 1);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    records.Add(ReadVideoRecord(reader));

                bool hasMore = records.Count > pageSize;
                if (hasMore)
                    records.RemoveAt(records.Count - 1);
                return new CursorVideoResult { Records = records, HasMore = hasMore, Total = total };
            }
        }

        internal List<VideoRecord> QueryVideoRecords(
            DateTime? startDate,
            DateTime? endDate,
            string keyword,
            bool includeDeleted,
            VideoSearchMode searchMode,
            string sourceType = "",
            string deviceId = "",
            string sourceDeviceName = "",
            IReadOnlyList<string> deviceIds = null)
        {
            lock (_lock)
            {
                (string whereSql, List<(string Name, object Value)> parameters) =
                    BuildVideoQueryWhere(startDate, endDate, keyword, includeDeleted, searchMode, sourceType, deviceId, sourceDeviceName, deviceIds: deviceIds);

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = VideoRecordSelectColumns
                    + whereSql + @"
                    ORDER BY StartTime DESC, Id DESC;";
                foreach ((string name, object value) in parameters)
                    cmd.Parameters.AddWithValue("@" + name, value);

                var records = new List<VideoRecord>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    records.Add(ReadVideoRecord(reader));
                return records;
            }
        }

        internal List<OrderNumberExportSource> QueryOrderNumberExportSources(
            DateTime? startDate,
            DateTime? endDate,
            CancellationToken cancellationToken = default,
            IProgress<OrderNumberExportProgress> progress = null,
            string mode = "",
            string deviceId = "",
            string sourceName = "",
            string sourceType = "",
            IReadOnlyList<string> deviceIds = null)
        {
            lock (_lock)
            {
                // 导出结果要与界面上应用的筛选一致，否则导出的单号对不上列表。
                (string filterSql, IReadOnlyList<(string Name, string Value)> parameters) =
                    new OrderNumberExportFilter(startDate, endDate, mode, deviceId, sourceName, sourceType, deviceIds)
                        .BuildWhere("v");
                string whereSql = @"
                    WHERE v.IsDeleted = 0
                      AND COALESCE(NULLIF(TRIM(v.TrackingNumber), ''), NULLIF(TRIM(v.OrderId), '')) IS NOT NULL"
                    + filterSql;

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new OrderNumberExportProgress(
                    OrderNumberExportStage.Reading,
                    0,
                    0,
                    "正在读取录像记录",
                    IsIndeterminate: true));

                // 平台订单号在内存里补，不再直接 LEFT JOIN OrderInfoRecords：
                // 原 JOIN 条件是 o.TrackingNumber = <表达式> COLLATE NOCASE，而主键索引是 BINARY 排序，
                // SQLite 用不上索引，只能对每行 VideoRecords 全表扫一遍 OrderInfoRecords。
                // 现场库实测（1.17 万行录像 × 5.85 千行订单）这条查询要 55 秒，同一条查询去掉 JOIN 只要 0.02 秒；
                // 而它全程占着 _lock，界面线程和其他后台任务只要碰数据库就一起卡死 ——
                // 表现就是 Web 端一点导出，上位机界面假死两分半（日志里 UI 心跳停了 145 秒）。
                // OrderInfoRecords 是主键表，一个单号最多一行，内存映射与原来的 JOIN 结果等价。
                Dictionary<string, string> orderSourceOrderIds = ReadOrderInfoSourceOrderIds();

                using var countCmd = _connection.CreateCommand();
                countCmd.CommandText = "SELECT COUNT(1) FROM VideoRecords v " + whereSql + ";";
                foreach ((string name, string value) in parameters)
                    countCmd.Parameters.AddWithValue("@" + name, value);
                int total = Convert.ToInt32(countCmd.ExecuteScalar());
                progress?.Report(new OrderNumberExportProgress(
                    OrderNumberExportStage.Reading,
                    0,
                    total,
                    "正在读取录像记录"));

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT
                        COALESCE(NULLIF(TRIM(v.TrackingNumber), ''), TRIM(v.OrderId)) AS ExportTrackingNumber,
                        TRIM(v.SourceOrderId) AS ExportSourceOrderId,
                        v.Mode,
                        v.StartTime,
                        v.SourceType,
                        v.SourceDeviceName,
                        v.SourceDeviceId
                    FROM VideoRecords v
                    "
                    + whereSql;
                foreach ((string name, string value) in parameters)
                    cmd.Parameters.AddWithValue("@" + name, value);
                cmd.CommandText += " ORDER BY v.StartTime DESC, v.Id DESC;";

                var results = new List<OrderNumberExportSource>(total);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string trackingNumber = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    string exportSourceOrderId = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    if (exportSourceOrderId.Length == 0
                        && orderSourceOrderIds.TryGetValue(trackingNumber, out string fallbackSourceOrderId))
                    {
                        exportSourceOrderId = fallbackSourceOrderId;
                    }

                    results.Add(new OrderNumberExportSource(
                        trackingNumber,
                        exportSourceOrderId,
                        reader.IsDBNull(2) ? "" : reader.GetString(2),
                        DateTime.Parse(reader.GetString(3)),
                        reader.IsDBNull(4) ? "" : reader.GetString(4),
                        reader.IsDBNull(5) ? "" : reader.GetString(5),
                        reader.IsDBNull(6) ? "" : reader.GetString(6)));
                    if (results.Count == total || results.Count % 100 == 0)
                    {
                        progress?.Report(new OrderNumberExportProgress(
                            OrderNumberExportStage.Reading,
                            results.Count,
                            total,
                            "正在读取录像记录"));
                    }
                }
                return results;
            }
        }

        /// <summary>
        /// 读取「快递单号 → 平台订单号」映射，供导出在内存里补空白的 SourceOrderId。
        /// OrderInfoRecords 以单号为主键，一个单号最多一行；这里用序数忽略大小写比较，
        /// 与原来 JOIN 上的 COLLATE NOCASE 语义一致。
        /// </summary>
        private Dictionary<string, string> ReadOrderInfoSourceOrderIds()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT TRIM(TrackingNumber), TRIM(SourceOrderId)
                FROM OrderInfoRecords
                WHERE TRIM(TrackingNumber) <> '' AND TRIM(SourceOrderId) <> '';";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string trackingNumber = reader.GetString(0);
                if (!map.ContainsKey(trackingNumber))
                    map[trackingNumber] = reader.GetString(1);
            }
            return map;
        }
}
