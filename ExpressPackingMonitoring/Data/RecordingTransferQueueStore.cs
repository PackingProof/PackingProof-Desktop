using Microsoft.Data.Sqlite;
using System.IO;

namespace ExpressPackingMonitoring.Data;

internal static class RecordingTransferStates
{
    public const string Pending = "Pending";
    public const string Uploading = "Uploading";
    public const string Uploaded = "Uploaded";
    public const string Failed = "Failed";
}

internal sealed class RecordingTransferTask
{
    public long Id { get; init; }
    public long LocalVideoRecordId { get; init; }
    public string LocalFilePath { get; init; } = "";
    public string FileSha256 { get; init; } = "";
    public string SourceSessionId { get; init; } = "";
    public string TargetNodeId { get; init; } = "";
    public string TargetAddress { get; set; } = "";
    public string State { get; init; } = RecordingTransferStates.Pending;
    public long ServerOffset { get; init; }
    public int RetryCount { get; init; }
    public string LastError { get; init; } = "";
    public DateTime? NextAttemptAt { get; init; }
    public long? RemoteVideoRecordId { get; init; }
    public int VerificationVersion { get; init; }
    public string VerificationReceipt { get; init; } = "";
    public DateTime? CacheDeletedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

internal sealed record RecordingTransferSummary(
    int PendingCount,
    int UploadingCount,
    int FailedCount,
    string LastError);

internal sealed class RecordingTransferQueueStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    public RecordingTransferQueueStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("数据库路径不能为空", nameof(databasePath));
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        Initialize();
    }

    private void Initialize()
    {
        Execute(@"
            CREATE TABLE IF NOT EXISTS RecordingTransferQueue (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                LocalVideoRecordId INTEGER NOT NULL,
                LocalFilePath TEXT NOT NULL,
                FileSha256 TEXT DEFAULT '',
                SourceSessionId TEXT NOT NULL,
                TargetNodeId TEXT NOT NULL,
                TargetAddress TEXT NOT NULL,
                State TEXT NOT NULL DEFAULT 'Pending',
                ServerOffset INTEGER NOT NULL DEFAULT 0,
                RetryCount INTEGER NOT NULL DEFAULT 0,
                LastError TEXT DEFAULT '',
                NextAttemptAt TEXT,
                RemoteVideoRecordId INTEGER,
                VerificationVersion INTEGER NOT NULL DEFAULT 0,
                VerificationReceipt TEXT DEFAULT '',
                CacheDeletedAt TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );");
        EnsureColumn("NextAttemptAt", "TEXT");
        EnsureColumn("CacheDeletedAt", "TEXT");
        EnsureColumn("VerificationVersion", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("VerificationReceipt", "TEXT DEFAULT ''");
        Execute("CREATE UNIQUE INDEX IF NOT EXISTS IX_RecordingTransferQueue_LocalVideoRecordId ON RecordingTransferQueue(LocalVideoRecordId);");
        Execute("CREATE INDEX IF NOT EXISTS IX_RecordingTransferQueue_State_UpdatedAt ON RecordingTransferQueue(State, UpdatedAt);");
        Execute(@"
            CREATE INDEX IF NOT EXISTS IX_RecordingTransferQueue_CacheCleanup
            ON RecordingTransferQueue(CreatedAt, Id, LocalVideoRecordId)
            WHERE State = 'Uploaded'
              AND CacheDeletedAt IS NULL
              AND RemoteVideoRecordId IS NOT NULL
              AND RemoteVideoRecordId > 0
              AND VerificationReceipt <> '';");
    }

    public bool Enqueue(
        long localVideoRecordId,
        string localFilePath,
        string sourceSessionId,
        string targetNodeId,
        string targetAddress,
        DateTime now)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO RecordingTransferQueue (
                    LocalVideoRecordId, LocalFilePath, SourceSessionId, TargetNodeId,
                    TargetAddress, State, CreatedAt, UpdatedAt)
                VALUES (
                    @recordId, @path, @sessionId, @nodeId, @address,
                    'Pending', @now, @now);";
            cmd.Parameters.AddWithValue("@recordId", localVideoRecordId);
            cmd.Parameters.AddWithValue("@path", Path.GetFullPath(localFilePath));
            cmd.Parameters.AddWithValue("@sessionId", sourceSessionId);
            cmd.Parameters.AddWithValue("@nodeId", targetNodeId);
            cmd.Parameters.AddWithValue("@address", targetAddress.Trim().TrimEnd('/'));
            cmd.Parameters.AddWithValue("@now", Format(now));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public void RecoverInterrupted(DateTime now)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE RecordingTransferQueue
                SET State = 'Pending',
                    LastError = CASE WHEN LastError = '' THEN '程序重启，继续上传' ELSE LastError END,
                    NextAttemptAt = NULL,
                    UpdatedAt = @now
                WHERE State = 'Uploading';";
            cmd.Parameters.AddWithValue("@now", Format(now));
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<RecordingTransferTask> GetReady(DateTime now, int limit = 10)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, LocalVideoRecordId, LocalFilePath, FileSha256, SourceSessionId,
                       TargetNodeId, TargetAddress, State, ServerOffset, RetryCount,
                       LastError, NextAttemptAt, RemoteVideoRecordId, VerificationVersion,
                       VerificationReceipt, CacheDeletedAt,
                       CreatedAt, UpdatedAt
                FROM RecordingTransferQueue
                WHERE State IN ('Pending', 'Failed')
                  AND (NextAttemptAt IS NULL OR NextAttemptAt = '' OR NextAttemptAt <= @now)
                ORDER BY CreatedAt ASC, Id ASC
                LIMIT @limit;";
            cmd.Parameters.AddWithValue("@now", Format(now));
            cmd.Parameters.AddWithValue("@limit", Math.Max(1, limit));
            using var reader = cmd.ExecuteReader();
            var result = new List<RecordingTransferTask>();
            while (reader.Read())
                result.Add(Read(reader));
            return result;
        }
    }

    public void SetHash(long id, string sha256, DateTime now) =>
        Update(id, "FileSha256 = @value, UpdatedAt = @now", ("@value", sha256), ("@now", Format(now)));

    public void MarkUploading(long id, long offset, DateTime now) =>
        Update(id,
            "State = 'Uploading', ServerOffset = @offset, LastError = '', NextAttemptAt = NULL, UpdatedAt = @now",
            ("@offset", offset), ("@now", Format(now)));

    public void UpdateOffset(long id, long offset, DateTime now) =>
        Update(id, "ServerOffset = @offset, UpdatedAt = @now", ("@offset", offset), ("@now", Format(now)));

    public void UpdateTargetAddress(string nodeId, string address, DateTime now)
    {
        string normalizedNodeId = nodeId?.Trim() ?? "";
        string normalizedAddress = address?.Trim().TrimEnd('/') ?? "";
        if (normalizedNodeId.Length == 0 || normalizedAddress.Length == 0)
            return;

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE RecordingTransferQueue
                SET TargetAddress = @address,
                    UpdatedAt = @now
                WHERE TargetNodeId = @nodeId
                  AND TargetAddress <> @address;";
            cmd.Parameters.AddWithValue("@nodeId", normalizedNodeId);
            cmd.Parameters.AddWithValue("@address", normalizedAddress);
            cmd.Parameters.AddWithValue("@now", Format(now));
            cmd.ExecuteNonQuery();
        }
    }

    public void MarkFailed(long id, int retryCount, string error, DateTime nextAttemptAt, DateTime now) =>
        Update(id,
            "State = 'Failed', RetryCount = @retry, LastError = @error, NextAttemptAt = @next, UpdatedAt = @now",
            ("@retry", retryCount), ("@error", TrimError(error)), ("@next", Format(nextAttemptAt)), ("@now", Format(now)));

    public void MarkUploaded(
        long id,
        long remoteRecordId,
        int verificationVersion,
        string verificationReceipt,
        DateTime now) =>
        Update(id,
            "State = 'Uploaded', RemoteVideoRecordId = @remoteId, VerificationVersion = @version, VerificationReceipt = @receipt, LastError = '', NextAttemptAt = NULL, UpdatedAt = @now",
            ("@remoteId", remoteRecordId), ("@version", verificationVersion),
            ("@receipt", verificationReceipt ?? ""), ("@now", Format(now)));

    public void MarkUploaded(long id, long remoteRecordId, DateTime now) =>
        MarkUploaded(id, remoteRecordId, 0, "", now);

    public void MarkCacheDeleted(long id, DateTime now) =>
        Update(id, "CacheDeletedAt = @now, UpdatedAt = @now", ("@now", Format(now)));

    public IReadOnlyList<RecordingTransferTask> GetUploadedWithLocalCache()
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, LocalVideoRecordId, LocalFilePath, FileSha256, SourceSessionId,
                       TargetNodeId, TargetAddress, State, ServerOffset, RetryCount,
                       LastError, NextAttemptAt, RemoteVideoRecordId, VerificationVersion,
                       VerificationReceipt, CacheDeletedAt,
                       CreatedAt, UpdatedAt
                FROM RecordingTransferQueue
                WHERE State = 'Uploaded' AND CacheDeletedAt IS NULL
                ORDER BY CreatedAt ASC, Id ASC;";
            using var reader = cmd.ExecuteReader();
            var result = new List<RecordingTransferTask>();
            while (reader.Read())
                result.Add(Read(reader));
            return result;
        }
    }

    public IReadOnlyList<RecordingTransferTask> GetCacheCleanupCandidateBatch(
        string rootPath,
        int limit,
        int minimumVerificationVersion)
    {
        string normalizedRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string escapedRootPrefix = normalizedRoot
            .Replace("~", "~~", StringComparison.Ordinal)
            .Replace("%", "~%", StringComparison.Ordinal)
            .Replace("_", "~_", StringComparison.Ordinal)
            + "%";
        limit = Math.Clamp(limit, 1, 128);
        minimumVerificationVersion = Math.Max(0, minimumVerificationVersion);
        lock (_lock)
        {
            var candidatePaths = new List<string>(limit);
            using (var paths = _connection.CreateCommand())
            {
                paths.CommandText = @"
                    SELECT v.FilePath
                    FROM RecordingTransferQueue q
                    JOIN VideoRecords v ON v.Id = q.LocalVideoRecordId
                    JOIN LocalVideoFileInventory i ON i.FilePath = v.FilePath
                    WHERE q.State = 'Uploaded'
                      AND q.CacheDeletedAt IS NULL
                      AND q.RemoteVideoRecordId IS NOT NULL
                      AND q.RemoteVideoRecordId > 0
                      AND q.VerificationVersion >= @minimumVersion
                      AND q.VerificationReceipt <> ''
                      AND TRIM(q.VerificationReceipt) <> ''
                      AND v.IsDeleted = 0
                      AND v.SourceType = 'pc'
                      AND v.FilePath LIKE @rootPrefix ESCAPE '~'
                      AND v.StorageState = 'Uploaded'
                      AND v.RemoteVideoRecordId IS NOT NULL
                      AND v.RemoteVideoRecordId > 0
                      AND (
                          SELECT COUNT(1)
                          FROM VideoRecords fileRecord
                          WHERE fileRecord.IsDeleted = 0
                            AND fileRecord.SourceType = 'pc'
                            AND fileRecord.FilePath = v.FilePath
                      ) <= @resultLimit
                      AND NOT EXISTS (
                          SELECT 1
                          FROM VideoRecords sibling
                          LEFT JOIN RecordingTransferQueue siblingQueue
                            ON siblingQueue.LocalVideoRecordId = sibling.Id
                          WHERE sibling.IsDeleted = 0
                            AND sibling.SourceType = 'pc'
                            AND sibling.FilePath = v.FilePath
                            AND (
                                sibling.StorageState <> 'Uploaded'
                                OR sibling.RemoteVideoRecordId IS NULL
                                OR sibling.RemoteVideoRecordId <= 0
                                OR siblingQueue.State <> 'Uploaded'
                                OR siblingQueue.RemoteVideoRecordId IS NULL
                                OR siblingQueue.RemoteVideoRecordId <= 0
                                OR siblingQueue.VerificationVersion < @minimumVersion
                                OR TRIM(COALESCE(siblingQueue.VerificationReceipt, '')) = ''
                            )
                      )
                    ORDER BY q.CreatedAt ASC, q.Id ASC
                    LIMIT @scanLimit;";
                paths.Parameters.AddWithValue("@minimumVersion", minimumVerificationVersion);
                paths.Parameters.AddWithValue("@rootPrefix", escapedRootPrefix);
                paths.Parameters.AddWithValue("@resultLimit", limit);
                paths.Parameters.AddWithValue("@scanLimit", limit * 4);
                using var reader = paths.ExecuteReader();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read() && candidatePaths.Count < limit)
                {
                    string path = reader.GetString(0);
                    if (seen.Add(path))
                        candidatePaths.Add(path);
                }
            }

            if (candidatePaths.Count == 0)
                return Array.Empty<RecordingTransferTask>();

            using var cmd = _connection.CreateCommand();
            var placeholders = new List<string>(candidatePaths.Count);
            for (int index = 0; index < candidatePaths.Count; index++)
            {
                string parameterName = $"@path{index}";
                placeholders.Add(parameterName);
                cmd.Parameters.AddWithValue(parameterName, candidatePaths[index]);
            }
            cmd.CommandText = $@"
                SELECT q.Id, q.LocalVideoRecordId, q.LocalFilePath, q.FileSha256,
                       q.SourceSessionId, q.TargetNodeId, q.TargetAddress, q.State,
                       q.ServerOffset, q.RetryCount, q.LastError, q.NextAttemptAt,
                       q.RemoteVideoRecordId, q.VerificationVersion,
                       q.VerificationReceipt, q.CacheDeletedAt,
                       q.CreatedAt, q.UpdatedAt, v.FilePath
                FROM RecordingTransferQueue q
                JOIN VideoRecords v ON v.Id = q.LocalVideoRecordId
                WHERE v.IsDeleted = 0
                  AND v.SourceType = 'pc'
                  AND v.FilePath IN ({string.Join(", ", placeholders)})
                  AND q.State = 'Uploaded'
                  AND q.CacheDeletedAt IS NULL
                ORDER BY q.CreatedAt ASC, q.Id ASC;";
            using var tasksReader = cmd.ExecuteReader();
            var tasksByPath = new Dictionary<string, List<RecordingTransferTask>>(
                StringComparer.OrdinalIgnoreCase);
            while (tasksReader.Read())
            {
                string path = tasksReader.GetString(18);
                if (!tasksByPath.TryGetValue(path, out List<RecordingTransferTask>? tasks))
                {
                    tasks = [];
                    tasksByPath[path] = tasks;
                }
                tasks.Add(Read(tasksReader));
            }
            var result = new List<RecordingTransferTask>(limit);
            foreach (string path in candidatePaths)
            {
                if (!tasksByPath.TryGetValue(path, out List<RecordingTransferTask>? tasks)
                    || result.Count + tasks.Count > limit)
                {
                    continue;
                }
                result.AddRange(tasks);
            }
            return result;
        }
    }

    public bool HasUnfinishedForDifferentHost(string nodeId) =>
        ScalarInt(@"
            SELECT COUNT(1) FROM RecordingTransferQueue
            WHERE State <> 'Uploaded' AND TargetNodeId <> @nodeId;",
            ("@nodeId", nodeId ?? "")) > 0;

    public void RetryFailedNow(DateTime now)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE RecordingTransferQueue
                SET State = 'Pending',
                    NextAttemptAt = NULL,
                    UpdatedAt = @now
                WHERE State = 'Failed';";
            cmd.Parameters.AddWithValue("@now", Format(now));
            cmd.ExecuteNonQuery();
        }
    }

    public RecordingTransferSummary GetSummary()
    {
        lock (_lock)
        {
            int pending = ScalarIntCore("SELECT COUNT(1) FROM RecordingTransferQueue WHERE State = 'Pending';");
            int uploading = ScalarIntCore("SELECT COUNT(1) FROM RecordingTransferQueue WHERE State = 'Uploading';");
            int failed = ScalarIntCore("SELECT COUNT(1) FROM RecordingTransferQueue WHERE State = 'Failed';");
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT LastError FROM RecordingTransferQueue
                WHERE LastError <> ''
                ORDER BY UpdatedAt DESC, Id DESC LIMIT 1;";
            string lastError = cmd.ExecuteScalar() as string ?? "";
            return new RecordingTransferSummary(pending, uploading, failed, lastError);
        }
    }

    private void Update(long id, string assignments, params (string Name, object Value)[] values)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"UPDATE RecordingTransferQueue SET {assignments} WHERE Id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            foreach ((string name, object value) in values)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private int ScalarInt(string sql, params (string Name, object Value)[] values)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            foreach ((string name, object value) in values)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    private int ScalarIntCore(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void EnsureColumn(string columnName, string definition)
    {
        using var check = _connection.CreateCommand();
        check.CommandText = "PRAGMA table_info('RecordingTransferQueue');";
        using SqliteDataReader reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();
        Execute($"ALTER TABLE RecordingTransferQueue ADD COLUMN {columnName} {definition};");
    }

    private static RecordingTransferTask Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        LocalVideoRecordId = reader.GetInt64(1),
        LocalFilePath = reader.GetString(2),
        FileSha256 = reader.IsDBNull(3) ? "" : reader.GetString(3),
        SourceSessionId = reader.GetString(4),
        TargetNodeId = reader.GetString(5),
        TargetAddress = reader.GetString(6),
        State = reader.GetString(7),
        ServerOffset = reader.GetInt64(8),
        RetryCount = reader.GetInt32(9),
        LastError = reader.IsDBNull(10) ? "" : reader.GetString(10),
        NextAttemptAt = ParseNullable(reader, 11),
        RemoteVideoRecordId = reader.IsDBNull(12) ? null : reader.GetInt64(12),
        VerificationVersion = reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
        VerificationReceipt = reader.IsDBNull(14) ? "" : reader.GetString(14),
        CacheDeletedAt = ParseNullable(reader, 15),
        CreatedAt = DateTime.Parse(reader.GetString(16)),
        UpdatedAt = DateTime.Parse(reader.GetString(17))
    };

    private static DateTime? ParseNullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) || string.IsNullOrWhiteSpace(reader.GetString(ordinal))
            ? null
            : DateTime.Parse(reader.GetString(ordinal));

    private static string Format(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");
    private static string TrimError(string value) =>
        string.IsNullOrWhiteSpace(value) ? "上传失败" : value.Trim()[..Math.Min(value.Trim().Length, 1000)];

    public void Dispose() => _connection.Dispose();
}
