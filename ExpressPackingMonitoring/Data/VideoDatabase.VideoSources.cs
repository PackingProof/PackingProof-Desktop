namespace ExpressPackingMonitoring.Data;

public partial class VideoDatabase
{
    /// <summary>
    /// 录像来源列表，按 (SourceType, SourceDeviceId) 分组，用于回放窗口与网页端的来源筛选。
    ///
    /// 设备名取该设备**最近一条记录**里的非空 SourceDeviceName。历史记录保存的是写入当时
    /// 的名字，设备改名后老记录仍留着"从机1"这类旧昵称；原实现用 MAX(SourceDeviceName)
    /// 按字典序随便挑一个，筛选下拉里就会冒出已经不存在的老名字。
    /// </summary>
    public IReadOnlyList<VideoSourceInfo> GetVideoSources()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT SourceType, SourceDeviceId, SourceDeviceName, VideoCount
                FROM (
                    SELECT SourceType,
                           SourceDeviceId,
                           SourceDeviceName,
                           COUNT(1) OVER (PARTITION BY SourceType, SourceDeviceId) AS VideoCount,
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
                result.Add(new VideoSourceInfo(
                    sourceType,
                    deviceId,
                    deviceName,
                    reader.GetInt32(3)));
            }
            return result;
        }
    }
}
