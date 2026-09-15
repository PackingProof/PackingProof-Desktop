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
}
