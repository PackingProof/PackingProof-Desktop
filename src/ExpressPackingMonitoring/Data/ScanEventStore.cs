using Microsoft.Data.Sqlite;
using System.Globalization;

namespace ExpressPackingMonitoring.Data;

internal sealed record ScanEventRecord
{
    public required string EventId { get; init; }
    public required string OccurredAtUtc { get; init; }
    public required string LastOccurredAtUtc { get; init; }
    public required string UpdatedAtUtc { get; init; }
    public required string Source { get; init; }
    public string RawCode { get; init; } = "";
    public string NormalizedOrder { get; init; } = "";
    public string Action { get; init; } = "";
    public string Reason { get; init; } = "";
    public string Outcome { get; init; } = "received";
    public string ComputerName { get; init; } = "";
    public string AppVersion { get; init; } = "";
    public string RuntimeSessionId { get; init; } = "";
    public string SourceSessionId { get; init; } = "";
    public string FileName { get; init; } = "";
    public string StopOrigin { get; init; } = "";
    public string Detail { get; init; } = "";
    public long? PriorRecordId { get; init; }
    public long? StartedRecordId { get; init; }
    public int RepeatCount { get; init; } = 1;
    public long DurationMs { get; init; }
    public bool WasRecording { get; init; }
    public bool ManualStop { get; init; }
    public bool ShortRecording { get; init; }
}

internal sealed class ScanEventStore : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    internal ScanEventStore(string databasePath)
    {
        string fullPath = Path.GetFullPath(databasePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        _connection = new SqliteConnection($"Data Source={fullPath};Pooling=False");
        _connection.Open();
        ExecuteNonQuery("PRAGMA busy_timeout=5000;");
        ExecuteNonQuery("PRAGMA journal_mode=WAL;");
        ExecuteNonQuery("PRAGMA synchronous=NORMAL;");
        EnsureSchema();
    }

    internal void UpsertBatch(IReadOnlyList<ScanEventRecord> records)
    {
        if (records.Count == 0)
            return;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteTransaction transaction = _connection.BeginTransaction();
            foreach (ScanEventRecord record in records)
                UpsertCore(record, transaction);
            transaction.Commit();
        }
    }

    internal IReadOnlyList<ScanEventRecord> Query(
        int limit = 5000,
        string? normalizedOrder = null,
        DateTimeOffset? occurredAfterUtc = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                SELECT EventId, OccurredAtUtc, LastOccurredAtUtc, UpdatedAtUtc,
                       Source, RawCode, NormalizedOrder, Action, Reason, Outcome,
                       ComputerName, AppVersion, RuntimeSessionId, SourceSessionId,
                       FileName, StopOrigin, Detail, PriorRecordId, StartedRecordId,
                       RepeatCount, DurationMs, WasRecording, ManualStop, ShortRecording
                FROM ScanEvents
                WHERE (@order = '' OR NormalizedOrder = @order)
                  AND (@after = '' OR OccurredAtUtc >= @after)
                ORDER BY OccurredAtUtc DESC, Id DESC
                LIMIT @limit;
                """;
            command.Parameters.AddWithValue("@order", normalizedOrder?.Trim() ?? "");
            command.Parameters.AddWithValue("@after", occurredAfterUtc?.UtcDateTime.ToString(
                "O",
                CultureInfo.InvariantCulture) ?? "");
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 100_000));

            using SqliteDataReader reader = command.ExecuteReader();
            var results = new List<ScanEventRecord>();
            while (reader.Read())
            {
                results.Add(new ScanEventRecord
                {
                    EventId = reader.GetString(0),
                    OccurredAtUtc = reader.GetString(1),
                    LastOccurredAtUtc = reader.GetString(2),
                    UpdatedAtUtc = reader.GetString(3),
                    Source = reader.GetString(4),
                    RawCode = reader.GetString(5),
                    NormalizedOrder = reader.GetString(6),
                    Action = reader.GetString(7),
                    Reason = reader.GetString(8),
                    Outcome = reader.GetString(9),
                    ComputerName = reader.GetString(10),
                    AppVersion = reader.GetString(11),
                    RuntimeSessionId = reader.GetString(12),
                    SourceSessionId = reader.GetString(13),
                    FileName = reader.GetString(14),
                    StopOrigin = reader.GetString(15),
                    Detail = reader.GetString(16),
                    PriorRecordId = reader.IsDBNull(17) ? null : reader.GetInt64(17),
                    StartedRecordId = reader.IsDBNull(18) ? null : reader.GetInt64(18),
                    RepeatCount = reader.GetInt32(19),
                    DurationMs = reader.GetInt64(20),
                    WasRecording = reader.GetInt64(21) != 0,
                    ManualStop = reader.GetInt64(22) != 0,
                    ShortRecording = reader.GetInt64(23) != 0
                });
            }
            return results;
        }
    }

    internal static bool CreateFeedbackSnapshot(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
            return false;

        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(destinationPath))
            File.Delete(destinationPath);

        using var connection = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        string escapedPath = destinationPath.Replace("'", "''");
        command.CommandText = $"VACUUM INTO '{escapedPath}';";
        command.ExecuteNonQuery();
        return File.Exists(destinationPath);
    }

    private void EnsureSchema()
    {
        ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS ScanEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EventId TEXT NOT NULL UNIQUE,
                OccurredAtUtc TEXT NOT NULL,
                LastOccurredAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                Source TEXT NOT NULL,
                RawCode TEXT NOT NULL DEFAULT '',
                NormalizedOrder TEXT NOT NULL DEFAULT '',
                Action TEXT NOT NULL DEFAULT '',
                Reason TEXT NOT NULL DEFAULT '',
                Outcome TEXT NOT NULL DEFAULT 'received',
                ComputerName TEXT NOT NULL DEFAULT '',
                AppVersion TEXT NOT NULL DEFAULT '',
                RuntimeSessionId TEXT NOT NULL DEFAULT '',
                SourceSessionId TEXT NOT NULL DEFAULT '',
                FileName TEXT NOT NULL DEFAULT '',
                StopOrigin TEXT NOT NULL DEFAULT '',
                Detail TEXT NOT NULL DEFAULT '',
                PriorRecordId INTEGER,
                StartedRecordId INTEGER,
                RepeatCount INTEGER NOT NULL DEFAULT 1,
                DurationMs INTEGER NOT NULL DEFAULT 0,
                WasRecording INTEGER NOT NULL DEFAULT 0,
                ManualStop INTEGER NOT NULL DEFAULT 0,
                ShortRecording INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS IX_ScanEvents_OccurredAt
                ON ScanEvents(OccurredAtUtc DESC);
            CREATE INDEX IF NOT EXISTS IX_ScanEvents_NormalizedOrder
                ON ScanEvents(NormalizedOrder, OccurredAtUtc DESC);
            CREATE INDEX IF NOT EXISTS IX_ScanEvents_StartedRecord
                ON ScanEvents(StartedRecordId);
            """);
    }

    private void UpsertCore(ScanEventRecord record, SqliteTransaction transaction)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ScanEvents (
                EventId, OccurredAtUtc, LastOccurredAtUtc, UpdatedAtUtc,
                Source, RawCode, NormalizedOrder, Action, Reason, Outcome,
                ComputerName, AppVersion, RuntimeSessionId, SourceSessionId,
                FileName, StopOrigin, Detail, PriorRecordId, StartedRecordId,
                RepeatCount, DurationMs, WasRecording, ManualStop, ShortRecording
            ) VALUES (
                @eventId, @occurredAt, @lastOccurredAt, @updatedAt,
                @source, @rawCode, @normalizedOrder, @action, @reason, @outcome,
                @computerName, @appVersion, @runtimeSessionId, @sourceSessionId,
                @fileName, @stopOrigin, @detail, @priorRecordId, @startedRecordId,
                @repeatCount, @durationMs, @wasRecording, @manualStop, @shortRecording
            )
            ON CONFLICT(EventId) DO UPDATE SET
                LastOccurredAtUtc = excluded.LastOccurredAtUtc,
                UpdatedAtUtc = excluded.UpdatedAtUtc,
                Source = excluded.Source,
                RawCode = excluded.RawCode,
                NormalizedOrder = excluded.NormalizedOrder,
                Action = excluded.Action,
                Reason = excluded.Reason,
                Outcome = excluded.Outcome,
                ComputerName = excluded.ComputerName,
                AppVersion = excluded.AppVersion,
                RuntimeSessionId = excluded.RuntimeSessionId,
                SourceSessionId = excluded.SourceSessionId,
                FileName = excluded.FileName,
                StopOrigin = excluded.StopOrigin,
                Detail = excluded.Detail,
                PriorRecordId = excluded.PriorRecordId,
                StartedRecordId = excluded.StartedRecordId,
                RepeatCount = excluded.RepeatCount,
                DurationMs = excluded.DurationMs,
                WasRecording = excluded.WasRecording,
                ManualStop = excluded.ManualStop,
                ShortRecording = excluded.ShortRecording;
            """;
        command.Parameters.AddWithValue("@eventId", record.EventId);
        command.Parameters.AddWithValue("@occurredAt", record.OccurredAtUtc);
        command.Parameters.AddWithValue("@lastOccurredAt", record.LastOccurredAtUtc);
        command.Parameters.AddWithValue("@updatedAt", record.UpdatedAtUtc);
        command.Parameters.AddWithValue("@source", record.Source);
        command.Parameters.AddWithValue("@rawCode", record.RawCode);
        command.Parameters.AddWithValue("@normalizedOrder", record.NormalizedOrder);
        command.Parameters.AddWithValue("@action", record.Action);
        command.Parameters.AddWithValue("@reason", record.Reason);
        command.Parameters.AddWithValue("@outcome", record.Outcome);
        command.Parameters.AddWithValue("@computerName", record.ComputerName);
        command.Parameters.AddWithValue("@appVersion", record.AppVersion);
        command.Parameters.AddWithValue("@runtimeSessionId", record.RuntimeSessionId);
        command.Parameters.AddWithValue("@sourceSessionId", record.SourceSessionId);
        command.Parameters.AddWithValue("@fileName", record.FileName);
        command.Parameters.AddWithValue("@stopOrigin", record.StopOrigin);
        command.Parameters.AddWithValue("@detail", record.Detail);
        command.Parameters.AddWithValue("@priorRecordId", (object?)record.PriorRecordId ?? DBNull.Value);
        command.Parameters.AddWithValue("@startedRecordId", (object?)record.StartedRecordId ?? DBNull.Value);
        command.Parameters.AddWithValue("@repeatCount", record.RepeatCount);
        command.Parameters.AddWithValue("@durationMs", record.DurationMs);
        command.Parameters.AddWithValue("@wasRecording", record.WasRecording ? 1 : 0);
        command.Parameters.AddWithValue("@manualStop", record.ManualStop ? 1 : 0);
        command.Parameters.AddWithValue("@shortRecording", record.ShortRecording ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private void ExecuteNonQuery(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _connection.Dispose();
        }
    }
}
