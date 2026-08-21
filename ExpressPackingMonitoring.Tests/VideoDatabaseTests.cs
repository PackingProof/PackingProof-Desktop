using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VideoDatabaseTests
{
    [Fact]
    public void MobileBackupDailyCounts_AreGroupedByStableDeviceAndIgnoreDeletedVideos()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            string firstPath = Path.Combine(tempDirectory, "first.mp4");
            string secondPath = Path.Combine(tempDirectory, "second.mp4");
            string deletedPath = Path.Combine(tempDirectory, "deleted.mp4");
            database.InsertMobileBackupRecord("A", firstPath, 1, DateTime.Now, 3, "phone-a", "手机1", "session-a1", "sha-a1");
            database.InsertMobileBackupRecord("B", secondPath, 1, DateTime.Now, 3, "phone-a", "手机1", "session-a2", "sha-a2");
            database.InsertMobileBackupRecord("C", deletedPath, 1, DateTime.Now, 3, "phone-b", "手机2", "session-b1", "sha-b1");
            database.MarkVideoDeleted(deletedPath, "测试");

            MobileBackupDailyCount count = Assert.Single(
                database.GetMobileBackupDailyCounts(DateTime.Today));
            Assert.Equal("phone-a", count.DeviceId);
            Assert.Equal("手机1", count.DeviceName);
            Assert.Equal(2, count.VideoCount);
            Assert.Equal("mobile", count.DeviceKind);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void MobileBackupOverview_CountsCompletedBackupsAndKeepsDeletedHistory()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            string firstPath = Path.Combine(tempDirectory, "first.mp4");
            string deletedPath = Path.Combine(tempDirectory, "deleted.mp4");
            database.InsertMobileBackupRecord("A", firstPath, 1, DateTime.Now, 3, "phone-a", "手机1", "session-a", "sha-a");
            database.InsertMobileBackupRecord("B", deletedPath, 1, DateTime.Now, 3, "phone-b", "手机2", "session-b", "sha-b");
            database.MarkVideoDeleted(deletedPath, "容量清理");
            database.InsertVideoRecord("PC", "发货", "", "", Path.Combine(tempDirectory, "pc.mp4"), DateTime.Now);

            MobileBackupOverview overview = database.GetMobileBackupOverview(DateTime.Today);

            Assert.Equal(2, overview.TodayCount);
            Assert.Equal(2, overview.TotalCount);
            Assert.Equal(2, overview.DeviceCounts.Count);
            Assert.Contains(overview.DeviceCounts, item =>
                item.DeviceId == "phone-a" && item.DeviceName == "手机1" && item.VideoCount == 1);
            Assert.Contains(overview.DeviceCounts, item =>
                item.DeviceId == "phone-b" && item.DeviceName == "手机2" && item.VideoCount == 1);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void VideoSourceFilter_AppliesToPagedQueryAndReturnsDistinctSources()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            database.InsertVideoRecord("PC", "发货", "", "", Path.Combine(tempDirectory, "pc.mp4"), DateTime.Now);
            database.InsertMobileBackupRecord("A", Path.Combine(tempDirectory, "a.mp4"), 1, DateTime.Now, 3, "phone-a", "手机1", "session-a", "sha-a");
            database.InsertMobileBackupRecord("B", Path.Combine(tempDirectory, "b.mp4"), 1, DateTime.Now, 3, "phone-b", "手机2", "session-b", "sha-b");
            database.InsertMobileBackupRecord("C", Path.Combine(tempDirectory, "c.mp4"), 1, DateTime.Now, 3, "legacy-phone-b", "手机2", "session-c", "sha-c");

            PagedVideoResult computer = database.QueryVideosPaged(
                null, null, null, 1, 20, sourceType: "pc");
            PagedVideoResult phone = database.QueryVideosPaged(
                null, null, null, 1, 20, sourceType: "external", deviceId: "phone-b");
            PagedVideoResult phoneName = database.QueryVideosPaged(
                null, null, null, 1, 20, sourceType: "external", sourceDeviceName: "手机2");
            IReadOnlyList<VideoSourceInfo> sources = database.GetVideoSources();

            Assert.Equal("PC", Assert.Single(computer.Records).OrderId);
            Assert.Equal("phone-b", Assert.Single(phone.Records).SourceDeviceId);
            Assert.Equal(2, phoneName.Total);
            Assert.Equal(4, sources.Count);
            Assert.Contains(sources, source => source.SourceType == "pc");
            Assert.Contains(sources, source => source.DeviceId == "phone-a" && source.DeviceName == "手机1");
            Assert.Contains(sources, source => source.DeviceId == "phone-b" && source.DeviceName == "手机2");
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void QueryVideoRecords_ReturnsAllMatchingInDescendingOrderAndRespectsDeletedFlag()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            DateTime baseTime = DateTime.Now.AddHours(-3);
            string firstPath = Path.Combine(tempDirectory, "first.mp4");
            string secondPath = Path.Combine(tempDirectory, "second.mp4");
            string deletedPath = Path.Combine(tempDirectory, "deleted.mp4");
            File.WriteAllBytes(firstPath, new byte[] { 1 });
            File.WriteAllBytes(secondPath, new byte[] { 2 });
            File.WriteAllBytes(deletedPath, new byte[] { 3 });
            database.InsertVideoRecord("ORDER-A", "发货", "", "", firstPath, baseTime.AddMinutes(2));
            database.InsertVideoRecord("ORDER-B", "发货", "", "", secondPath, baseTime.AddMinutes(1));
            database.InsertVideoRecord("ORDER-C", "发货", "", "", deletedPath, baseTime.AddMinutes(3));
            database.MarkVideoDeleted(deletedPath, "容量清理");

            List<VideoRecord> all = database.QueryVideoRecords(
                null, null, "", includeDeleted: true, VideoSearchMode.ExactOrderIdentifiers);
            Assert.Equal(new[] { "ORDER-C", "ORDER-A", "ORDER-B" }, all.Select(r => r.OrderId));

            List<VideoRecord> active = database.QueryVideoRecords(
                null, null, "", includeDeleted: false, VideoSearchMode.ExactOrderIdentifiers);
            Assert.Equal(new[] { "ORDER-A", "ORDER-B" }, active.Select(r => r.OrderId));
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void QueryVideoRecords_AppliesKeywordAndDateFilterAndMatchesPagedTotal()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            string firstPath = Path.Combine(tempDirectory, "first.mp4");
            string secondPath = Path.Combine(tempDirectory, "second.mp4");
            File.WriteAllBytes(firstPath, new byte[] { 1 });
            File.WriteAllBytes(secondPath, new byte[] { 2 });
            database.InsertVideoRecord("ORDER-KEY-1", "发货", "", "", firstPath, DateTime.Today.AddHours(8));
            database.InsertVideoRecord("ORDER-OLD", "发货", "", "", secondPath, DateTime.Today.AddDays(-3).AddHours(8));

            List<VideoRecord> byKeyword = database.QueryVideoRecords(
                null, null, "KEY", includeDeleted: false, VideoSearchMode.OrderIdentifierContains);
            Assert.Equal(new[] { "ORDER-KEY-1" }, byKeyword.Select(r => r.OrderId));

            List<VideoRecord> byDate = database.QueryVideoRecords(
                DateTime.Today.AddDays(-2), DateTime.Today, "", includeDeleted: false, VideoSearchMode.ExactOrderIdentifiers);
            Assert.Equal(new[] { "ORDER-KEY-1" }, byDate.Select(r => r.OrderId));

            PagedVideoResult paged = database.QueryVideosPaged(
                null, null, "", 1, 20, includeDeleted: false, VideoSearchMode.ExactOrderIdentifiers);
            List<VideoRecord> all = database.QueryVideoRecords(
                null, null, "", includeDeleted: false, VideoSearchMode.ExactOrderIdentifiers);
            Assert.Equal(paged.Total, all.Count);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void MobileHistory_CountsDeviceDuplicatesAndReturnsDeletedStatuses()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            string firstPath = Path.Combine(tempDirectory, "first.mp4");
            string secondPath = Path.Combine(tempDirectory, "second.mp4");
            File.WriteAllBytes(firstPath, new byte[] { 1 });
            File.WriteAllBytes(secondPath, new byte[] { 2 });
            long firstId = database.InsertMobileBackupRecord("TRACK-1", firstPath, 1, DateTime.Now, 3, "phone-a", "Phone", "session-1", "sha-1");
            long secondId = database.InsertMobileBackupRecord("TRACK-2", secondPath, 1, DateTime.Now, 3, "phone-b", "Phone", "session-2", "sha-2");
            database.MarkVideoDeleted(secondPath, "容量清理");

            Assert.Equal(1, database.CountVideosForDevice(null, null, null, "phone-a"));
            IReadOnlyDictionary<long, VideoRecord> statuses = database.QueryVideoStatuses(new[] { firstId, secondId, 999999L });
            Assert.False(statuses[firstId].IsDeleted);
            Assert.True(statuses[secondId].IsDeleted);
            Assert.Equal("容量清理", statuses[secondId].DeleteReason);
            Assert.DoesNotContain(999999L, statuses.Keys);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void OrderIdExistsRecent_ChecksThirtyDaysAndIgnoresDeletedOrExcludedRecords()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            AddCompleted(database, "RECENT", "发货", Path.Combine(tempDirectory, "recent.mp4"), DateTime.Now.AddDays(-29));
            AddCompleted(database, "OLD", "发货", Path.Combine(tempDirectory, "old.mp4"), DateTime.Now.AddDays(-31));
            string deletedPath = Path.Combine(tempDirectory, "deleted.mp4");
            AddCompleted(database, "DELETED", "发货", deletedPath, DateTime.Now.AddDays(-1));
            database.MarkVideoDeleted(deletedPath, "测试");
            long excludedId = database.InsertVideoRecord("EXCLUDED", "发货", "", "", Path.Combine(tempDirectory, "excluded.mp4"), DateTime.Now);

            Assert.True(database.OrderIdExistsRecent("RECENT"));
            Assert.False(database.OrderIdExistsRecent("OLD"));
            Assert.False(database.OrderIdExistsRecent("DELETED"));
            Assert.False(database.OrderIdExistsRecent("EXCLUDED", excludedId));
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void GetRecentOrderInfos_UsesDatabaseAsNinetyDaySourceOfTruth()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(tempDirectory, "videos.db");
            using (var database = new VideoDatabase(databasePath))
            {
                database.UpsertOrderInfos(new[]
                {
                    new OrderInfo { TrackingNumber = " recent ", IsPrintedRefund = true, RefundStatus = "SUCCESS", PushTime = DateTime.Now.AddDays(-89) },
                    new OrderInfo { TrackingNumber = "OLD", IsPrintedRefund = true, PushTime = DateTime.Now.AddDays(-91) }
                });
            }

            using var reopened = new VideoDatabase(databasePath);
            List<OrderInfo> records = reopened.GetRecentOrderInfos();

            OrderInfo recent = Assert.Single(records);
            Assert.Equal(" recent ", recent.TrackingNumber);
            Assert.True(recent.IsPrintedRefund);
            Assert.Equal("SUCCESS", recent.RefundStatus);
            Assert.DoesNotContain(records, item => item.TrackingNumber == "OLD");
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void UpsertOrderInfos_DoesNotLetOlderSnapshotOverwriteNewerRefundState()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            database.UpsertOrderInfos(new[]
            {
                new OrderInfo { TrackingNumber = "TRACK-1", IsPrintedRefund = true, RefundStatus = "SUCCESS", PushTime = DateTime.Now }
            });
            database.UpsertOrderInfos(new[]
            {
                new OrderInfo { TrackingNumber = "TRACK-1", IsPrintedRefund = false, RefundStatus = "NO_REFUND", PushTime = DateTime.Now.AddDays(-1) }
            });

            OrderInfo record = Assert.Single(database.GetRecentOrderInfos());
            Assert.True(record.IsPrintedRefund);
            Assert.Equal("SUCCESS", record.RefundStatus);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void VideoRecords_DerivesFileNameFromPathAndDoesNotPersistRedundantColumn()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(tempDirectory, "videos.db");
            string videoPath = Path.Combine(tempDirectory, "订单-ABC.mp4");
            using (var database = new VideoDatabase(databasePath))
            {
                long id = database.InsertVideoRecord("ORDER", "发货", "", "", videoPath, DateTime.Now);
                VideoRecord record = database.GetVideoById(id);
                Assert.Equal("订单-ABC.mp4", record.FileName);
                Assert.Contains(database.QueryVideos(null, null, "订单-ABC"), item => item.Id == id);
            }

            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info('VideoRecords');";
            using var reader = command.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read()) columns.Add(reader.GetString(1));
            Assert.DoesNotContain("FileName", columns);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void GetRecentCompletedVideos_ReturnsLatestTwentyValidRecordsForDate()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"ExpressPackingMonitoringTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string databasePath = Path.Combine(tempDirectory, "videos.db");
            DateTime date = new(2026, 7, 11);
            using (var database = new VideoDatabase(databasePath))
            {
                AddCompleted(database, "YESTERDAY", "发货", Path.Combine(tempDirectory, "yesterday.mp4"), date.AddDays(-1).AddHours(23));

                for (int index = 0; index < 22; index++)
                {
                    AddCompleted(
                        database,
                        $"TODAY-{index:00}",
                        index % 2 == 0 ? "发货" : "退货",
                        Path.Combine(tempDirectory, $"today-{index:00}.mp4"),
                        date.AddHours(8).AddMinutes(index));
                }

                string deletedPath = Path.Combine(tempDirectory, "deleted.mp4");
                AddCompleted(database, "DELETED", "发货", deletedPath, date.AddHours(22));
                database.MarkVideoDeleted(deletedPath, "测试清理");
                database.InsertVideoRecord("INCOMPLETE", "退货", "", "", Path.Combine(tempDirectory, "incomplete.mp4"), date.AddHours(23));

                List<VideoRecord> records = database.GetRecentCompletedVideos(date, 20);

                Assert.Equal(20, records.Count);
                Assert.Equal("TODAY-21", records[0].OrderId);
                Assert.Equal("TODAY-02", records[^1].OrderId);
                Assert.Equal("退货", records[0].Mode);
                Assert.DoesNotContain(records, record => record.OrderId is "YESTERDAY" or "DELETED" or "INCOMPLETE");
                Assert.True(records.SequenceEqual(records.OrderByDescending(record => record.StartTime)));
            }
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(tempDirectory);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void MainWindowQueries_CanFilterToPcRecordingsOnly()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            DateTime date = new(2026, 7, 19);
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            AddCompleted(
                database,
                "PC-LOCAL",
                "发货",
                Path.Combine(tempDirectory, "pc.mp4"),
                date.AddHours(9));
            database.InsertMobileBackupRecord(
                "PHONE-REMOTE",
                Path.Combine(tempDirectory, "phone.mp4"),
                2048,
                date.AddHours(10),
                30,
                "phone-1",
                "打包手机",
                "session-1",
                new string('a', 64));

            List<VideoRecord> allRecent = database.GetRecentCompletedVideos(date, 20);
            List<VideoRecord> pcRecent = database.GetRecentCompletedVideos(date, 20, "pc");
            List<DailyStat> allStats = database.GetAggregatedStats(date, date);
            List<DailyStat> pcStats = database.GetAggregatedStats(date, date, "day", "pc");

            Assert.Equal(2, allRecent.Count);
            Assert.Single(pcRecent);
            Assert.Equal("PC-LOCAL", pcRecent[0].OrderId);
            Assert.Equal(2, Assert.Single(allStats).TotalPieces);
            Assert.Equal(1, Assert.Single(pcStats).TotalPieces);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void GetAggregatedStats_CountsSharedFileSizeOnlyOnce()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            DateTime date = new(2026, 7, 20);
            string sharedPath = Path.Combine(tempDirectory, "shared.mp4");
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));

            long firstId = database.InsertVideoRecord("ORDER-1", "发货", "", "", sharedPath, date.AddHours(9));
            database.UpdateVideoRecordOnStop(firstId, date.AddHours(9).AddMinutes(1), 60, 1024, "手动");
            long secondId = database.InsertVideoRecord("ORDER-2", "发货", "", "", sharedPath, date.AddHours(10));
            database.UpdateVideoRecordOnStop(secondId, date.AddHours(10).AddMinutes(2), 120, 2048, "手动");

            DailyStat stat = Assert.Single(database.GetAggregatedStats(date, date));

            Assert.Equal(2, stat.TotalPieces);
            Assert.Equal(2, stat.ShippingPieces);
            Assert.Equal(0, stat.ReturnPieces);
            Assert.Equal(180, stat.TotalDurationSec);
            Assert.Equal(180, stat.ShippingDurationSec);
            Assert.Equal(0, stat.ReturnDurationSec);
            Assert.Equal(1024, stat.TotalBytes);
            Assert.Equal(1024, stat.ShippingBytes);
            Assert.Equal(0, stat.ReturnBytes);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void GetAggregatedStats_GroupsCompletedVideosByYear()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(tempDirectory, "videos.db"));
            AddCompleted(database, "ORDER-2025-A", "发货", Path.Combine(tempDirectory, "2025-a.mp4"), new DateTime(2025, 2, 1));
            AddCompleted(database, "ORDER-2025-B", "退货", Path.Combine(tempDirectory, "2025-b.mp4"), new DateTime(2025, 11, 1));
            AddCompleted(database, "ORDER-2026", "发货", Path.Combine(tempDirectory, "2026.mp4"), new DateTime(2026, 1, 1));

            List<DailyStat> stats = database.GetAggregatedStats(
                new DateTime(2025, 1, 1),
                new DateTime(2026, 12, 31),
                "year");

            Assert.Collection(
                stats,
                item =>
                {
                    Assert.Equal("2025", item.Date);
                    Assert.Equal(2, item.TotalPieces);
                    Assert.Equal(1, item.ShippingPieces);
                    Assert.Equal(1, item.ReturnPieces);
                },
                item =>
                {
                    Assert.Equal("2026", item.Date);
                    Assert.Equal(1, item.TotalPieces);
                    Assert.Equal(1, item.ShippingPieces);
                    Assert.Equal(0, item.ReturnPieces);
                });
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void QueryVideosPaged_ReturnsOnlyRequestedPageFromTenThousandRecords()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(tempDirectory, "videos.db");
            using (var database = new VideoDatabase(databasePath)) { }
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO VideoRecords (OrderId, FilePath, StartTime)
                    VALUES (@orderId, @filePath, @startTime);";
                var orderId = command.Parameters.Add("@orderId", SqliteType.Text);
                var filePath = command.Parameters.Add("@filePath", SqliteType.Text);
                var startTime = command.Parameters.Add("@startTime", SqliteType.Text);
                DateTime baseTime = new(2026, 1, 1);
                for (int index = 0; index < 10000; index++)
                {
                    orderId.Value = $"ORDER-{index:00000}";
                    filePath.Value = Path.Combine(tempDirectory, $"video-{index:00000}.mkv");
                    startTime.Value = baseTime.AddSeconds(index).ToString("yyyy-MM-dd HH:mm:ss");
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }

            using var reopened = new VideoDatabase(databasePath);
            PagedVideoResult page = reopened.QueryVideosPaged(null, null, null, 200, 50);
            PagedVideoResult searched = reopened.QueryVideosPaged(null, null, "ORDER-09999", 1, 50);

            Assert.Equal(10000, page.Total);
            Assert.Equal(50, page.Records.Count);
            Assert.Equal("ORDER-00049", page.Records[0].OrderId);
            Assert.Equal("ORDER-00000", page.Records[^1].OrderId);
            Assert.Equal("ORDER-09999", Assert.Single(searched.Records).OrderId);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void QueryVideosWindow_FiltersDeletedAndReportsMoreWithoutCounting()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(tempDirectory, "videos.db");
            using var database = new VideoDatabase(databasePath);
            DateTime start = new(2026, 1, 1);
            for (int index = 0; index < 6; index++)
            {
                long id = database.InsertVideoRecord($"ORDER-{index}", "发货", "", "", $"video-{index}.mp4", start.AddMinutes(index));
                database.UpdateVideoRecordOnStop(id, start.AddMinutes(index + 1), 60, 1024, "手动");
                if (index == 1)
                    database.MarkVideoDeleted("video-1.mp4", "测试清理");
            }

            CursorVideoResult first = database.QueryVideosWindow(null, null, "", 1, 2, false, VideoSearchMode.ExactOrderIdentifiers);
            CursorVideoResult second = database.QueryVideosWindow(null, null, "", 2, 2, false, VideoSearchMode.ExactOrderIdentifiers);
            CursorVideoResult third = database.QueryVideosWindow(null, null, "", 3, 2, false, VideoSearchMode.ExactOrderIdentifiers);

            Assert.DoesNotContain(first.Records, record => record.IsDeleted);
            Assert.DoesNotContain(second.Records, record => record.IsDeleted);
            Assert.Equal(2, first.Records.Count);
            Assert.True(first.HasMore);
            Assert.True(second.HasMore);
            Assert.Single(third.Records);
            Assert.False(third.HasMore);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public void QueryVideosPaged_ExactSearchMatchesOnlyOrderIdentifiers()
    {
        string tempDirectory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(tempDirectory, "videos.db");
            using var database = new VideoDatabase(databasePath);
            DateTime start = new(2026, 7, 21, 9, 0, 0);

            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO VideoRecords
                        (OrderId, TrackingNumber, SourceOrderId, BuyerMessage, FilePath, StartTime)
                    VALUES
                        ('ORDER-EXACT', 'TRACK-EXACT', 'SOURCE-EXACT', '', 'first.mp4', @start),
                        ('ORDER-OTHER', 'TRACK-OTHER', 'SOURCE-OTHER', 'TRACK-EXACT', 'second.mp4', @start);";
                command.Parameters.AddWithValue("@start", start.ToString("yyyy-MM-dd HH:mm:ss"));
                command.ExecuteNonQuery();
            }

            PagedVideoResult trackingResult = database.QueryVideosPaged(
                null, null, "TRACK-EXACT", 1, 50, true, VideoSearchMode.ExactOrderIdentifiers);
            PagedVideoResult orderResult = database.QueryVideosPaged(
                null, null, "ORDER-EXACT", 1, 50, true, VideoSearchMode.ExactOrderIdentifiers);
            PagedVideoResult sourceResult = database.QueryVideosPaged(
                null, null, "SOURCE-EXACT", 1, 50, true, VideoSearchMode.ExactOrderIdentifiers);
            PagedVideoResult partialResult = database.QueryVideosPaged(
                null, null, "EXACT", 1, 50, true, VideoSearchMode.ExactOrderIdentifiers);
            PagedVideoResult partialIdentifierResult = database.QueryVideosPaged(
                null, null, "EXACT", 1, 50, true, VideoSearchMode.OrderIdentifierContains);

            Assert.Equal("ORDER-EXACT", Assert.Single(trackingResult.Records).OrderId);
            Assert.Equal("ORDER-EXACT", Assert.Single(orderResult.Records).OrderId);
            Assert.Equal("ORDER-EXACT", Assert.Single(sourceResult.Records).OrderId);
            Assert.Empty(partialResult.Records);
            Assert.Equal("ORDER-EXACT", Assert.Single(partialIdentifierResult.Records).OrderId);
        }
        finally
        {
            DeleteTempDirectory(tempDirectory);
        }
    }

    private static void AddCompleted(VideoDatabase database, string orderId, string mode, string path, DateTime startTime)
    {
        long id = database.InsertVideoRecord(orderId, mode, "", "", path, startTime);
        database.UpdateVideoRecordOnStop(id, startTime.AddMinutes(1), 60, 1024, "手动");
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ExpressPackingMonitoringTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        SqliteTestPool.ClearPoolFor(path);
        Directory.Delete(path, recursive: true);
    }
}
