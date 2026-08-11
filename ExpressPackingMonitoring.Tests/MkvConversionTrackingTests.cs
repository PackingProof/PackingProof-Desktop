using ExpressPackingMonitoring.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class MkvConversionTrackingTests
{
    private static readonly DateTime Now = new(2026, 7, 28, 12, 0, 0);

    [Fact]
    public void AutomaticRetry_RetriesAggressivelyDuringFirstTwentyFourHours()
    {
        var state = CreateState(Now.AddHours(-23), Now.AddMinutes(-1));

        Assert.Equal(
            MkvAutomaticRetryDecision.Retry,
            MkvConversionRetryPolicy.GetAutomaticRetryDecision(state, Now));
    }

    [Fact]
    public void AutomaticRetry_DefersToDailyRetryAfterFirstTwentyFourHours()
    {
        var recentAttempt = CreateState(Now.AddHours(-25), Now.AddHours(-2));
        var oldAttempt = CreateState(Now.AddHours(-25), Now.AddHours(-24));

        Assert.Equal(
            MkvAutomaticRetryDecision.Deferred,
            MkvConversionRetryPolicy.GetAutomaticRetryDecision(recentAttempt, Now));
        Assert.Equal(
            MkvAutomaticRetryDecision.Retry,
            MkvConversionRetryPolicy.GetAutomaticRetryDecision(oldAttempt, Now));
    }

    [Fact]
    public void AutomaticRetry_SuppressesFailuresOlderThanSevenDays()
    {
        var state = CreateState(Now.AddDays(-7).AddSeconds(-1), Now.AddDays(-1));

        Assert.Equal(
            MkvAutomaticRetryDecision.Suppressed,
            MkvConversionRetryPolicy.GetAutomaticRetryDecision(state, Now));
        Assert.False(MkvConversionRetryPolicy.ShouldNotify(state, Now));
    }

    [Fact]
    public void Notification_IsDailyButNewFailureCanNotifyImmediately()
    {
        var newFailure = CreateState(Now, Now);
        var notifiedToday = CreateState(Now.AddHours(-2), Now.AddHours(-1), Now.AddMinutes(-30));
        var notifiedYesterday = CreateState(Now.AddDays(-1), Now.AddHours(-1), Now.AddDays(-1));

        Assert.True(MkvConversionRetryPolicy.ShouldNotify(newFailure, Now));
        Assert.False(MkvConversionRetryPolicy.ShouldNotify(notifiedToday, Now));
        Assert.True(MkvConversionRetryPolicy.ShouldNotify(notifiedYesterday, Now));
    }

    [Fact]
    public void Database_PersistsFailureStateClaimsNotificationAndClearsOnSuccess()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "recording.mkv");
            string databasePath = Path.Combine(directory, "videos.db");
            using var database = new VideoDatabase(databasePath);
            database.InsertVideoRecord("ORDER-1", "发货", "", "", path, Now.AddMinutes(-1));
            database.InsertVideoRecord("ORDER-2", "发货", "", "", path, Now.AddMinutes(-1));

            database.RecordMkvConversionFailure(path, Now, "first");
            database.RecordMkvConversionFailure(path, Now.AddMinutes(1), "second");

            MkvConversionFailureState state =
                Assert.IsType<MkvConversionFailureState>(database.GetMkvConversionFailureState(path));
            Assert.Equal(Now, state.FirstFailedAt);
            Assert.Equal(Now.AddMinutes(1), state.LastAttemptAt);
            Assert.Equal(2, state.FailureCount);
            Assert.Equal("second", state.LastError);
            Assert.Equal(2, CountRowsWithFailureCount(databasePath, 2));

            Assert.Equal(1, database.ClaimMkvFailureNotifications([path], Now.AddMinutes(2)));
            Assert.Equal(0, database.ClaimMkvFailureNotifications([path], Now.AddHours(1)));
            Assert.Equal(1, database.ClaimMkvFailureNotifications([path], Now.AddDays(1)));

            database.ClearMkvConversionFailure(path);
            MkvConversionFailureState cleared =
                Assert.IsType<MkvConversionFailureState>(database.GetMkvConversionFailureState(path));
            Assert.Null(cleared.FirstFailedAt);
            Assert.Null(cleared.LastAttemptAt);
            Assert.Equal(0, cleared.FailureCount);
            Assert.Equal("", cleared.LastError);
            Assert.Null(cleared.LastNotifiedAt);

            database.RecordMkvConversionFailure(path, Now.AddDays(2), "third");
            string mp4Path = Path.ChangeExtension(path, ".mp4");
            database.UpdateVideoFilePath(path, mp4Path);
            MkvConversionFailureState converted =
                Assert.IsType<MkvConversionFailureState>(database.GetMkvConversionFailureState(mp4Path));
            Assert.Null(converted.FirstFailedAt);
            Assert.Equal(0, converted.FailureCount);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void DatabaseUpgrade_AddsMkvTrackingColumnsAndPreservesExistingPath()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "videos.db");
            string videoPath = Path.Combine(directory, "legacy.mkv");
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE VideoRecords (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        OrderId TEXT NOT NULL,
                        Mode TEXT NOT NULL DEFAULT '',
                        VideoCodec TEXT DEFAULT '',
                        VideoEncoder TEXT DEFAULT '',
                        TrackingNumber TEXT DEFAULT '',
                        SourceOrderId TEXT DEFAULT '',
                        BuyerMessage TEXT DEFAULT '',
                        SellerMemo TEXT DEFAULT '',
                        ProductInfo TEXT DEFAULT '',
                        OrderInfoPushTime TEXT,
                        OrderInfoJson TEXT DEFAULT '',
                        FilePath TEXT NOT NULL,
                        FileSizeBytes INTEGER DEFAULT 0,
                        StartTime TEXT NOT NULL,
                        EndTime TEXT,
                        DurationSeconds REAL DEFAULT 0,
                        StopReason TEXT DEFAULT '',
                        IsDeleted INTEGER DEFAULT 0,
                        DeletedAt TEXT,
                        DeleteReason TEXT DEFAULT ''
                    );
                    INSERT INTO VideoRecords (OrderId, FilePath, StartTime)
                    VALUES ('LEGACY', @filePath, @startTime);";
                command.Parameters.AddWithValue("@filePath", videoPath);
                command.Parameters.AddWithValue("@startTime", Now.ToString("yyyy-MM-dd HH:mm:ss"));
                command.ExecuteNonQuery();
            }

            using (var database = new VideoDatabase(databasePath))
            {
                Assert.Contains(videoPath, database.QueryMkvFilePaths());
                Assert.Null(database.GetMkvConversionFailureState(videoPath)?.FirstFailedAt);
            }

            using var reopened = new SqliteConnection($"Data Source={databasePath}");
            reopened.Open();
            using var schema = reopened.CreateCommand();
            schema.CommandText = "PRAGMA table_info(VideoRecords);";
            using SqliteDataReader reader = schema.ExecuteReader();
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
                columns.Add(reader.GetString(1));

            Assert.Contains("MkvFirstFailedAt", columns);
            Assert.Contains("MkvLastAttemptAt", columns);
            Assert.Contains("MkvFailureCount", columns);
            Assert.Contains("MkvLastError", columns);
            Assert.Contains("MkvLastNotifiedAt", columns);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    private static MkvConversionFailureState CreateState(
        DateTime firstFailedAt,
        DateTime lastAttemptAt,
        DateTime? lastNotifiedAt = null)
    {
        return new MkvConversionFailureState
        {
            FilePath = "recording.mkv",
            FirstFailedAt = firstFailedAt,
            LastAttemptAt = lastAttemptAt,
            FailureCount = 1,
            LastNotifiedAt = lastNotifiedAt
        };
    }

    private static int CountRowsWithFailureCount(string databasePath, int failureCount)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM VideoRecords WHERE MkvFailureCount = @failureCount;";
        command.Parameters.AddWithValue("@failureCount", failureCount);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"MkvConversionTrackingTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        SqliteTestPool.ClearPoolFor(directory);
        Directory.Delete(directory, recursive: true);
    }
}
