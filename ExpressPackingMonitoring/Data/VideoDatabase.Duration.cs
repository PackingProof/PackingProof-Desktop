namespace ExpressPackingMonitoring.Data;

public partial class VideoDatabase
{
    public void SaveWallClockDuration(long videoRecordId, double durationSeconds)
    {
        if (videoRecordId <= 0 || !double.IsFinite(durationSeconds) || durationSeconds < 0)
            return;

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO VideoRecordingTimings (VideoRecordId, WallClockDurationSeconds)
                VALUES (@id, @duration)
                ON CONFLICT(VideoRecordId) DO UPDATE SET
                    WallClockDurationSeconds = excluded.WallClockDurationSeconds;";
            cmd.Parameters.AddWithValue("@id", videoRecordId);
            cmd.Parameters.AddWithValue("@duration", durationSeconds);
            cmd.ExecuteNonQuery();
        }
    }

    public double? GetWallClockDuration(long videoRecordId)
    {
        if (videoRecordId <= 0)
            return null;

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT WallClockDurationSeconds FROM VideoRecordingTimings WHERE VideoRecordId = @id;";
            cmd.Parameters.AddWithValue("@id", videoRecordId);
            object? value = cmd.ExecuteScalar();
            return value == null || value == DBNull.Value ? null : Convert.ToDouble(value);
        }
    }

    public void UpdateVideoDurationByFilePath(string filePath, double durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(filePath)
            || !double.IsFinite(durationSeconds)
            || durationSeconds <= 0)
            return;

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE VideoRecords
                SET DurationSeconds = @duration
                WHERE FilePath = @filePath COLLATE NOCASE
                  AND IsDeleted = 0;";
            cmd.Parameters.AddWithValue("@duration", durationSeconds);
            cmd.Parameters.AddWithValue("@filePath", filePath);
            cmd.ExecuteNonQuery();
        }
    }
}
