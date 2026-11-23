using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ExpressPackingMonitoring.Data;

public sealed record PhotoRecord(
    long Id,
    string TrackingNumber,
    string Mode,
    string FilePath,
    DateTimeOffset CreatedAt,
    string SourceDeviceId,
    string SourceDeviceName,
    int Width,
    int Height,
    long FileSizeBytes,
    bool IsDeleted,
    string DeleteReason,
    string ArchivePath,
    string ArchiveStatus = "Disabled",
    string ArchiveError = "",
    int ArchiveAttempts = 0);

public sealed record PhotoQueryPage(
    IReadOnlyList<PhotoRecord> Items,
    int Total);

public sealed class PhotoStore
{
    private readonly string _databasePath;
    private readonly object _schemaSync = new();
    private bool _schemaReady;

    public PhotoStore(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
    }

    public PhotoRecord Add(
        string trackingNumber,
        string mode,
        string filePath,
        string sourceDeviceId,
        string sourceDeviceName,
        int width,
        int height,
        long fileSizeBytes,
        DateTimeOffset createdAt,
        string archivePath = "")
        => Add(
            trackingNumber,
            mode,
            filePath,
            sourceDeviceId,
            sourceDeviceName,
            width,
            height,
            fileSizeBytes,
            createdAt,
            archivePath,
            string.IsNullOrWhiteSpace(archivePath) ? "Disabled" : "Completed");

    public PhotoRecord Add(
        string trackingNumber,
        string mode,
        string filePath,
        string sourceDeviceId,
        string sourceDeviceName,
        int width,
        int height,
        long fileSizeBytes,
        DateTimeOffset createdAt,
        string archivePath,
        string archiveStatus)
    {
        EnsureInitialized();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PhotoRecords
              (TrackingNumber, Mode, FilePath, CreatedAt, SourceDeviceId,
               SourceDeviceName, Width, Height, FileSizeBytes, IsDeleted,
               DeleteReason, ArchivePath, ArchiveStatus)
            VALUES
              ($tracking, $mode, $path, $created, $sourceId,
               $sourceName, $width, $height, $size, 0,
               '', $archivePath, $archiveStatus);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$tracking", trackingNumber?.Trim() ?? "");
        command.Parameters.AddWithValue("$mode", mode?.Trim() ?? "");
        command.Parameters.AddWithValue("$path", Path.GetFullPath(filePath));
        command.Parameters.AddWithValue(
            "$created",
            createdAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$sourceId", sourceDeviceId?.Trim() ?? "");
        command.Parameters.AddWithValue("$sourceName", sourceDeviceName?.Trim() ?? "");
        command.Parameters.AddWithValue("$width", Math.Max(0, width));
        command.Parameters.AddWithValue("$height", Math.Max(0, height));
        command.Parameters.AddWithValue("$size", Math.Max(0, fileSizeBytes));
        command.Parameters.AddWithValue("$archivePath", archivePath?.Trim() ?? "");
        command.Parameters.AddWithValue(
            "$archiveStatus",
            string.IsNullOrWhiteSpace(archiveStatus) ? "Disabled" : archiveStatus.Trim());
        long id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return Get(id, includeDeleted: true)
            ?? throw new InvalidOperationException("照片记录写入后无法读取");
    }

    public PhotoRecord? Get(long id, bool includeDeleted = false)
    {
        EnsureInitialized();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = includeDeleted
            ? "SELECT * FROM PhotoRecords WHERE Id = $id;"
            : "SELECT * FROM PhotoRecords WHERE Id = $id AND IsDeleted = 0;";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public PhotoQueryPage QueryPage(
        string keyword,
        int offset,
        int limit,
        DateTimeOffset? startInclusive = null,
        DateTimeOffset? endExclusive = null,
        string mode = "",
        string sourceId = "",
        string sourceName = "",
        IReadOnlyList<string>? sourceIds = null)
    {
        EnsureInitialized();
        string normalizedKeyword = keyword?.Trim() ?? "";
        string normalizedMode = RecordingModeFilter.Normalize(mode);
        string normalizedSourceId = sourceId?.Trim() ?? "";
        string normalizedSourceName = sourceName?.Trim() ?? "";
        List<string> normalizedSourceIds = (sourceIds ?? [])
            .Select(value => value?.Trim() ?? "")
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var where = new List<string> { "IsDeleted = 0" };
        using SqliteConnection connection = Open();
        using SqliteCommand countCommand = connection.CreateCommand();
        using SqliteCommand pageCommand = connection.CreateCommand();
        if (normalizedKeyword.Length > 0)
        {
            where.Add("TrackingNumber LIKE $pattern");
            AddParameter(countCommand, pageCommand, "$pattern", $"%{normalizedKeyword}%");
        }
        if (startInclusive.HasValue)
        {
            where.Add("CreatedAt >= $start");
            string value = startInclusive.Value.UtcDateTime.ToString(
                "O",
                CultureInfo.InvariantCulture);
            AddParameter(countCommand, pageCommand, "$start", value);
        }
        if (endExclusive.HasValue)
        {
            where.Add("CreatedAt < $end");
            string value = endExclusive.Value.UtcDateTime.ToString(
                "O",
                CultureInfo.InvariantCulture);
            AddParameter(countCommand, pageCommand, "$end", value);
        }
        if (normalizedMode == RecordingModeFilter.Return)
        {
            where.Add("Mode IN ('return', '退货')");
        }
        else if (normalizedMode == RecordingModeFilter.Shipping)
        {
            where.Add("(Mode IN ('shipping', '发货', '') OR Mode IS NULL)");
        }
        if (normalizedSourceIds.Count > 0)
        {
            var placeholders = new List<string>(normalizedSourceIds.Count);
            for (int index = 0; index < normalizedSourceIds.Count; index++)
            {
                string name = $"$sourceId{index}";
                placeholders.Add(name);
                AddParameter(
                    countCommand,
                    pageCommand,
                    name,
                    normalizedSourceIds[index]);
            }
            where.Add($"SourceDeviceId IN ({string.Join(", ", placeholders)})");
        }
        else if (normalizedSourceId.Length > 0)
        {
            where.Add("SourceDeviceId = $sourceId");
            AddParameter(countCommand, pageCommand, "$sourceId", normalizedSourceId);
        }
        else if (normalizedSourceName.Length > 0)
        {
            where.Add("SourceDeviceName = $sourceName");
            AddParameter(countCommand, pageCommand, "$sourceName", normalizedSourceName);
        }

        string whereSql = string.Join(" AND ", where);
        countCommand.CommandText = $"""
            SELECT COUNT(1)
            FROM PhotoRecords
            WHERE {whereSql};
            """;
        int total = Convert.ToInt32(
            countCommand.ExecuteScalar(),
            CultureInfo.InvariantCulture);

        pageCommand.CommandText = $"""
            SELECT *
            FROM PhotoRecords
            WHERE {whereSql}
            ORDER BY CreatedAt DESC, Id DESC
            LIMIT $limit OFFSET $offset;
            """;
        pageCommand.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        pageCommand.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        var items = new List<PhotoRecord>();
        using SqliteDataReader reader = pageCommand.ExecuteReader();
        while (reader.Read())
            items.Add(Read(reader));
        return new PhotoQueryPage(items, total);
    }

    private static void AddParameter(
        SqliteCommand first,
        SqliteCommand second,
        string name,
        object value)
    {
        first.Parameters.AddWithValue(name, value);
        second.Parameters.AddWithValue(name, value);
    }

    public bool MarkDeleted(long id, string reason)
    {
        EnsureInitialized();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PhotoRecords
            SET IsDeleted = 1, DeleteReason = $reason
            WHERE Id = $id AND IsDeleted = 0;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$reason", reason?.Trim() ?? "UserRequested");
        return command.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<PhotoRecord> QueryPendingArchives(int limit = 50)
    {
        EnsureInitialized();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM PhotoRecords
            WHERE IsDeleted = 0 AND ArchiveStatus = 'Pending'
            ORDER BY CreatedAt, Id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        var result = new List<PhotoRecord>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(Read(reader));
        return result;
    }

    public bool HasPendingArchives()
    {
        EnsureInitialized();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM PhotoRecords
                WHERE IsDeleted = 0 AND ArchiveStatus = 'Pending');
            """;
        return Convert.ToInt64(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture) != 0;
    }

    public bool MarkArchivePending(long id)
    {
        EnsureInitialized();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PhotoRecords
            SET ArchiveStatus = 'Pending', ArchiveError = ''
            WHERE Id = $id AND IsDeleted = 0
              AND ArchiveStatus IN ('Pending', 'Failed');
            """;
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() == 1;
    }

    public void UpdateArchiveState(
        long id,
        string status,
        string archivePath,
        string error,
        int attempts)
    {
        EnsureInitialized();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PhotoRecords
            SET ArchiveStatus = $status,
                ArchivePath = $path,
                ArchiveError = $error,
                ArchiveAttempts = $attempts
            WHERE Id = $id AND IsDeleted = 0;
            """;
        command.Parameters.AddWithValue(
            "$status",
            string.IsNullOrWhiteSpace(status) ? "Failed" : status.Trim());
        command.Parameters.AddWithValue("$path", archivePath?.Trim() ?? "");
        command.Parameters.AddWithValue("$error", error?.Trim() ?? "");
        command.Parameters.AddWithValue("$attempts", Math.Max(0, attempts));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS PhotoRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TrackingNumber TEXT NOT NULL DEFAULT '',
                Mode TEXT NOT NULL DEFAULT '',
                FilePath TEXT NOT NULL UNIQUE,
                CreatedAt TEXT NOT NULL,
                SourceDeviceId TEXT NOT NULL DEFAULT '',
                SourceDeviceName TEXT NOT NULL DEFAULT '',
                Width INTEGER NOT NULL DEFAULT 0,
                Height INTEGER NOT NULL DEFAULT 0,
                FileSizeBytes INTEGER NOT NULL DEFAULT 0,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                DeleteReason TEXT NOT NULL DEFAULT '',
                ArchivePath TEXT NOT NULL DEFAULT '',
                ArchiveStatus TEXT NOT NULL DEFAULT 'Disabled',
                ArchiveError TEXT NOT NULL DEFAULT '',
                ArchiveAttempts INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS IX_PhotoRecords_CreatedAt
              ON PhotoRecords(CreatedAt DESC, Id DESC);
            CREATE INDEX IF NOT EXISTS IX_PhotoRecords_TrackingNumber
              ON PhotoRecords(TrackingNumber);
            """;
        command.ExecuteNonQuery();
        EnsureArchiveColumns(connection);
    }

    private static void EnsureArchiveColumns(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(PhotoRecords);";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
                columns.Add(reader.GetString(1));
        }
        if (!columns.Contains("ArchiveStatus"))
            Execute(connection, "ALTER TABLE PhotoRecords ADD COLUMN ArchiveStatus TEXT NOT NULL DEFAULT 'Disabled';");
        if (!columns.Contains("ArchiveError"))
            Execute(connection, "ALTER TABLE PhotoRecords ADD COLUMN ArchiveError TEXT NOT NULL DEFAULT '';");
        if (!columns.Contains("ArchiveAttempts"))
            Execute(connection, "ALTER TABLE PhotoRecords ADD COLUMN ArchiveAttempts INTEGER NOT NULL DEFAULT 0;");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void EnsureInitialized()
    {
        if (_schemaReady)
            return;
        lock (_schemaSync)
        {
            if (_schemaReady)
                return;
            string? directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            EnsureSchema();
            _schemaReady = true;
        }
    }

    private static PhotoRecord Read(SqliteDataReader reader) =>
        new(
            Id: reader.GetInt64(0),
            TrackingNumber: reader.GetString(1),
            Mode: reader.GetString(2),
            FilePath: reader.GetString(3),
            CreatedAt: DateTimeOffset.Parse(
                reader.GetString(4),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            SourceDeviceId: reader.GetString(5),
            SourceDeviceName: reader.GetString(6),
            Width: reader.GetInt32(7),
            Height: reader.GetInt32(8),
            FileSizeBytes: reader.GetInt64(9),
            IsDeleted: reader.GetInt64(10) != 0,
            DeleteReason: reader.GetString(11),
            ArchivePath: reader.GetString(12),
            ArchiveStatus: reader.GetString(13),
            ArchiveError: reader.GetString(14),
            ArchiveAttempts: reader.GetInt32(15));
}
