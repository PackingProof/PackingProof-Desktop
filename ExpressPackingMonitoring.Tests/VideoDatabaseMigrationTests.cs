using ExpressPackingMonitoring.Data;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VideoDatabaseMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "epm-migration-tests-" + Guid.NewGuid().ToString("N"));

    public VideoDatabaseMigrationTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void FreshDatabase_HasCurrentSchemaVersion()
    {
        string dbPath = Path.Combine(_directory, "fresh.db");
        using var database = new VideoDatabase(dbPath);

        Assert.Equal(1, ReadUserVersion(dbPath));
    }

    [Fact]
    public void FreshDatabase_HasArchiveStatusIndex()
    {
        string dbPath = Path.Combine(_directory, "index.db");
        using var database = new VideoDatabase(dbPath);

        using var verify = new SqliteConnection($"Data Source={dbPath}");
        verify.Open();
        using var cmd = verify.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'idx_video_archive_status_reason';";
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void LegacyDatabase_IsMigratedToCurrentSchemaVersion()
    {
        string legacyPath = Path.Combine(_directory, "legacy.db");
        using (var connection = new SqliteConnection($"Data Source={legacyPath}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"CREATE TABLE VideoRecords (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    OrderId TEXT NOT NULL,
                    Mode TEXT NOT NULL DEFAULT '',
                    TrackingNumber TEXT DEFAULT '',
                    SourceOrderId TEXT DEFAULT '',
                    BuyerMessage TEXT DEFAULT '',
                    SellerMemo TEXT DEFAULT '',
                    ProductInfo TEXT DEFAULT '',
                    OrderInfoPushTime TEXT,
                    OrderInfoJson TEXT DEFAULT '',
                    SourceType TEXT NOT NULL DEFAULT 'pc',
                    SourceDeviceId TEXT DEFAULT '',
                    SourceDeviceName TEXT DEFAULT '',
                    SourceDeviceKind TEXT DEFAULT '',
                    SourceSessionId TEXT DEFAULT '',
                    ContentSha256 TEXT DEFAULT '',
                    BackupCompletedAt TEXT,
                    StorageState TEXT NOT NULL DEFAULT 'Local',
                    RemoteVideoRecordId INTEGER,
                    FilePath TEXT NOT NULL,
                    FileSizeBytes INTEGER DEFAULT 0,
                    StartTime TEXT NOT NULL,
                    EndTime TEXT,
                    DurationSeconds REAL DEFAULT 0,
                    StopReason TEXT DEFAULT '',
                    MkvFirstFailedAt TEXT,
                    MkvLastAttemptAt TEXT,
                    MkvFailureCount INTEGER NOT NULL DEFAULT 0,
                    MkvLastError TEXT DEFAULT '',
                    MkvLastNotifiedAt TEXT,
                    IsDeleted INTEGER DEFAULT 0,
                    DeletedAt TEXT,
                    DeleteReason TEXT DEFAULT ''
                );";
            cmd.ExecuteNonQuery();
        }

        using (var migrated = new VideoDatabase(legacyPath))
        {
        }

        Assert.Equal(1, ReadUserVersion(legacyPath));
        using var verify = new SqliteConnection($"Data Source={legacyPath}");
        verify.Open();
        using var columnsCmd = verify.CreateCommand();
        columnsCmd.CommandText = "PRAGMA table_info(VideoRecords);";
        var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = columnsCmd.ExecuteReader();
        while (reader.Read())
            columnNames.Add(reader.GetString(1));
        Assert.Contains("ArchiveStatus", columnNames);
        Assert.Contains("ArchivePath", columnNames);
        Assert.Contains("LastArchiveProbeAt", columnNames);
    }

    [Fact]
    public void NewerDatabaseVersion_IsNotDowngraded()
    {
        string newerPath = Path.Combine(_directory, "newer.db");
        using (var connection = new SqliteConnection($"Data Source={newerPath}"))
        {
            connection.Open();
            using var setVersionCmd = connection.CreateCommand();
            setVersionCmd.CommandText = "PRAGMA user_version = 99;";
            setVersionCmd.ExecuteNonQuery();
        }

        using var opened = new VideoDatabase(newerPath);

        Assert.Equal(99, ReadUserVersion(newerPath));
    }

    private static int ReadUserVersion(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
