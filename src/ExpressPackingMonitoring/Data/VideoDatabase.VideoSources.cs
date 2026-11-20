namespace ExpressPackingMonitoring.Data;

public partial class VideoDatabase
{
    /// <summary>
    /// 录像来源列表，按 (SourceType, SourceDeviceId) 分组，用于回放窗口与网页端的来源筛选。
    ///
    /// 设备身份是设备号，名字只是显示属性（昵称随时会改，记录里的 SourceDeviceName 也
    /// 不再逐条写入）。这里的 DeviceName 只在设备登记表还没记住这台设备时兜底，
    /// 取该设备最近一条带名字的记录（老库升级时用来补齐"设备号 -> 昵称"映射）；
    /// LastRecordUtc 是这台设备最后一次留下录像的时间，用于补齐映射时排序。
    /// </summary>
    public IReadOnlyList<VideoSourceInfo> GetVideoSources()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT SourceType, SourceDeviceId, SourceDeviceName, VideoCount, LastRecordAt, PreferredName
                FROM (
                    SELECT SourceType,
                           SourceDeviceId,
                           SourceDeviceName,
                           COUNT(1) OVER (PARTITION BY SourceType, SourceDeviceId) AS VideoCount,
                           MAX(COALESCE(NULLIF(TRIM(COALESCE(BackupCompletedAt, '')), ''), StartTime))
                               OVER (PARTITION BY SourceType, SourceDeviceId) AS LastRecordAt,
                           MAX(CASE
                               WHEN TRIM(COALESCE(SourceDeviceName, '')) <> ''
                                AND SourceDeviceName NOT LIKE '从机%'
                               THEN SourceDeviceName END)
                               OVER (PARTITION BY SourceType, SourceDeviceId) AS PreferredName,
                           ROW_NUMBER() OVER (
                               PARTITION BY SourceType, SourceDeviceId
                               ORDER BY (TRIM(COALESCE(SourceDeviceName, '')) = '') ASC,
                                        Id DESC) AS RowIndex
                    FROM VideoRecords
                    WHERE IsDeleted = 0
                )
                WHERE RowIndex = 1
                ORDER BY SourceType, SourceDeviceName, SourceDeviceId;";
            using var reader = cmd.ExecuteReader();
            var result = new List<VideoSourceInfo>();
            while (reader.Read())
            {
                string sourceType = reader.IsDBNull(0) ? "pc" : reader.GetString(0);
                string deviceId = reader.IsDBNull(1) ? "" : reader.GetString(1);
                string deviceName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                DateTime lastRecordUtc = reader.IsDBNull(4)
                    || !DateTime.TryParse(reader.GetString(4), out DateTime parsed)
                        ? default
                        : DateTime.SpecifyKind(parsed, DateTimeKind.Local).ToUniversalTime();
                string preferredName = reader.IsDBNull(5) ? "" : reader.GetString(5);
                result.Add(new VideoSourceInfo(
                    sourceType,
                    deviceId,
                    deviceName,
                    reader.GetInt32(3),
                    lastRecordUtc,
                    preferredName));
            }
            return result;
        }
    }

    /// <summary>
    /// 统计某台外部设备仍可回放的录像数。清理设备登记前只认数据库的未删除记录，
    /// 不根据文件是否存在猜测归属；录像文件即使被外部移走，也要先走现有的缺失修复/删除流程。
    /// </summary>
    public int CountActiveExternalVideosByDevice(string? deviceId)
    {
        string normalizedDeviceId = deviceId?.Trim() ?? "";
        if (normalizedDeviceId.Length == 0)
            return 0;

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(1)
                FROM VideoRecords
                WHERE IsDeleted = 0
                  AND SourceType = 'external'
                  AND SourceDeviceId = @deviceId COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("@deviceId", normalizedDeviceId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>
    /// 只查询登记设备中仍有未删除录像的设备号。IN 列表最多只有设备登记表的硬上限，
    /// 可命中外部会话索引；不要在周期对账中反复对整张录像表做窗口聚合。
    /// </summary>
    public IReadOnlyList<string> GetActiveExternalDeviceIds(
        IEnumerable<string>? deviceIds)
    {
        string[] normalizedDeviceIds = (deviceIds ?? [])
            .Select(item => item?.Trim() ?? "")
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(512)
            .ToArray();
        if (normalizedDeviceIds.Length == 0)
            return [];

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            string[] parameterNames = new string[normalizedDeviceIds.Length];
            for (int index = 0; index < normalizedDeviceIds.Length; index++)
            {
                parameterNames[index] = $"@device{index}";
                cmd.Parameters.AddWithValue(
                    parameterNames[index],
                    normalizedDeviceIds[index]);
            }
            cmd.CommandText = $@"
                SELECT DISTINCT SourceDeviceId
                FROM VideoRecords
                WHERE IsDeleted = 0
                  AND SourceType = 'external'
                  AND SourceDeviceId IN ({string.Join(", ", parameterNames)});";
            using var reader = cmd.ExecuteReader();
            var result = new List<string>();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                    result.Add(reader.GetString(0));
            }
            return result;
        }
    }
}
