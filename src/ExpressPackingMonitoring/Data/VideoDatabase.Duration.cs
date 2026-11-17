using System.Globalization;

namespace ExpressPackingMonitoring.Data;

internal sealed record VideoDurationProbeCandidate(
    long RecordId,
    string FilePath,
    DateTime StartTime);

public partial class VideoDatabase
{
    private void EnsureVideoRecordingTimingsTable()
    {
        ExecuteNonQuery(@"
            CREATE TABLE IF NOT EXISTS VideoRecordingTimings (
                VideoRecordId INTEGER PRIMARY KEY,
                WallClockDurationSeconds REAL NOT NULL DEFAULT 0,
                FOREIGN KEY (VideoRecordId) REFERENCES VideoRecords(Id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS VideoDurationProbeState (
                VideoRecordId INTEGER PRIMARY KEY,
                LastAttemptAt TEXT NOT NULL,
                FOREIGN KEY (VideoRecordId) REFERENCES VideoRecords(Id) ON DELETE CASCADE
            );");
    }

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

    internal IReadOnlyList<VideoDurationProbeCandidate> GetVideosNeedingDurationProbe(
        DateTime attemptedAfter,
        int limit)
    {
        var result = new List<VideoDurationProbeCandidate>();
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT v.Id, v.FilePath, v.StartTime
                FROM VideoRecords v
                LEFT JOIN VideoDurationProbeState p ON p.VideoRecordId = v.Id
                WHERE v.IsDeleted = 0
                  AND v.DurationSeconds <= 0
                  AND v.FilePath <> ''
                  AND (p.LastAttemptAt IS NULL OR p.LastAttemptAt < @attemptedAfter)
                ORDER BY v.StartTime DESC, v.Id DESC
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@attemptedAfter", attemptedAfter.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 500));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                DateTime.TryParse(
                    reader.GetString(2),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime startTime);
                result.Add(new VideoDurationProbeCandidate(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    startTime));
            }
        }
        return result;
    }

    internal void MarkVideoDurationProbeAttempt(long recordId, DateTime attemptedAtUtc)
    {
        if (recordId <= 0) return;
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO VideoDurationProbeState (VideoRecordId, LastAttemptAt)
                VALUES (@id, @attemptedAt)
                ON CONFLICT(VideoRecordId) DO UPDATE SET
                    LastAttemptAt = excluded.LastAttemptAt;
                """;
            cmd.Parameters.AddWithValue("@id", recordId);
            cmd.Parameters.AddWithValue("@attemptedAt", attemptedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
        }
    }

    internal bool UpdateVideoDurationAndEndTime(
        long recordId,
        double durationSeconds,
        DateTime endTime)
    {
        if (recordId <= 0 || !double.IsFinite(durationSeconds) || durationSeconds <= 0)
            return false;

        bool updated;
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE VideoRecords
                SET DurationSeconds = @duration,
                    EndTime = CASE
                        WHEN EndTime IS NULL OR EndTime = '' OR EndTime <= StartTime THEN @endTime
                        ELSE EndTime
                    END
                WHERE Id = @id AND IsDeleted = 0;
                """;
            cmd.Parameters.AddWithValue("@duration", durationSeconds);
            cmd.Parameters.AddWithValue("@endTime", endTime.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@id", recordId);
            updated = cmd.ExecuteNonQuery() > 0;
        }
        if (updated)
            NotifyVideoRecordFinalized(recordId);
        return updated;
    }
}
