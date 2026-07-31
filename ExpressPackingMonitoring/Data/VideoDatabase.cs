using ExpressPackingMonitoring.Services;
#nullable disable
using ExpressPackingMonitoring.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ExpressPackingMonitoring.Data
{
    /// <summary>
    /// 视频录制记录
    /// </summary>
    public class VideoRecord
    {
        public long Id { get; set; }
        public string OrderId { get; set; } = "";
        public string Mode { get; set; } = "";       // 发货/退货
        public string TrackingNumber { get; set; } = "";
        public string SourceOrderId { get; set; } = "";
        public string BuyerMessage { get; set; } = "";
        public string SellerMemo { get; set; } = "";
        public string ProductInfo { get; set; } = "";
        public DateTime? OrderInfoPushTime { get; set; }
        public string OrderInfoJson { get; set; } = "";
        public string SourceType { get; set; } = "pc";
        public string SourceDeviceId { get; set; } = "";
        public string SourceDeviceName { get; set; } = "";
        public string SourceSessionId { get; set; } = "";
        public string SourceDeviceKind { get; set; } = "";
        public string ContentSha256 { get; set; } = "";
        public string StorageState { get; set; } = "Local";
        public long? RemoteVideoRecordId { get; set; }
        public string VideoCodec { get; set; } = "";
        public string VideoEncoder { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string FileName => Path.GetFileName(FilePath ?? "");
        public long FileSizeBytes { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public double DurationSeconds { get; set; }
        public string StopReason { get; set; } = "";  // 手动/静止超时/时长超时/程序退出
        public bool IsDeleted { get; set; }
        public DateTime? DeletedAt { get; set; }
        public string DeleteReason { get; set; } = ""; // 磁盘清理/手动删除
    }

    /// <summary>
    /// 每日统计记录（从数据库聚合）
    /// </summary>
    public class DailyStat
    {
        public string Date { get; set; } = "";
        public int TotalPieces { get; set; }
        public double TotalDurationSec { get; set; }
        public long TotalBytes { get; set; } // 新增
    }

    /// <summary>
    /// 删除日志记录
    /// </summary>
    public class DeleteLogEntry
    {
        public long Id { get; set; }
        public string FilePath { get; set; } = "";
        public string OrderId { get; set; } = "";
        public long FileSizeBytes { get; set; }
        public DateTime DeletedAt { get; set; }
        public string Reason { get; set; } = "";
    }

    public class PagedVideoResult
    {
        public int Total { get; set; }
        public List<VideoRecord> Records { get; set; } = new();
    }

    internal enum VideoSearchMode
    {
        BroadContains,
        ExactOrderIdentifiers,
        OrderIdentifierContains
    }

    public class CursorVideoResult
    {
        public List<VideoRecord> Records { get; set; } = new();
        public bool HasMore { get; set; }
    }

    public class StorageVideoFile
    {
        public string FilePath { get; set; } = "";
        public long FileSizeBytes { get; set; }
        public DateTime StartTime { get; set; }
    }

    /// <summary>
    /// 本地 SQLite 视频数据库，统一管理录制记录、统计数据和删除日志。
    /// 替代原来的 daily_stats.json 和文件系统扫描。
    /// </summary>
    public class VideoDatabase : IDisposable
    {
        public static readonly TimeSpan OrderInfoRetention = TimeSpan.FromDays(90);
        public static readonly TimeSpan DuplicateOrderLookback = TimeSpan.FromDays(30);
        public const int MaxOrderInfoRecords = 50000;

        private readonly string _dbPath;
        private SqliteConnection _connection;
        private readonly object _lock = new object();

        public VideoDatabase(string dbPath)
        {
            _dbPath = dbPath;
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            bool databaseExisted = File.Exists(_dbPath);
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();

            // 启用 WAL 模式提高并发性能
            ExecuteNonQuery("PRAGMA journal_mode=WAL;");
            ExecuteNonQuery("PRAGMA synchronous=NORMAL;");

            // 视频录制表
            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS VideoRecords (
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
                );");

            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS OrderInfoRecords (
                    TrackingNumber TEXT PRIMARY KEY,
                    SourceOrderId TEXT DEFAULT '',
                    BuyerMessage TEXT DEFAULT '',
                    SellerMemo TEXT DEFAULT '',
                    ProductInfo TEXT DEFAULT '',
                    PushTime TEXT,
                    OrderInfoJson TEXT DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );");

            // 录制工位本地上传队列。任务独立于 VideoRecords，避免网络重试影响录像主记录。
            ExecuteNonQuery(@"
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
                    CacheDeletedAt TEXT,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS IX_RecordingTransferQueue_State_UpdatedAt ON RecordingTransferQueue(State, UpdatedAt);");
            ExecuteNonQuery("CREATE UNIQUE INDEX IF NOT EXISTS IX_RecordingTransferQueue_LocalVideoRecordId ON RecordingTransferQueue(LocalVideoRecordId);");

            // 本地录像文件清单。录制工位用它做高频容量统计，避免反复递归扫描缓存目录。
            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS LocalVideoFileInventory (
                    FilePath TEXT PRIMARY KEY COLLATE NOCASE,
                    FileSizeBytes INTEGER NOT NULL DEFAULT 0,
                    UpdatedAt TEXT NOT NULL
                );");

            // 删除日志表
            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS DeleteLogs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FilePath TEXT NOT NULL,
                    OrderId TEXT DEFAULT '',
                    FileSizeBytes INTEGER DEFAULT 0,
                    DeletedAt TEXT NOT NULL,
                    Reason TEXT DEFAULT ''
                );");

            // 索引
            BackupBeforeSchemaMigrationIfNeeded(databaseExisted);
            DropRedundantFileNameColumn();

            EnsureColumnExists("VideoRecords", "VideoCodec", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "VideoEncoder", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "TrackingNumber", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "SourceOrderId", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "BuyerMessage", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "SellerMemo", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "ProductInfo", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "OrderInfoPushTime", "TEXT");
            EnsureColumnExists("VideoRecords", "OrderInfoJson", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "SourceType", "TEXT NOT NULL DEFAULT 'pc'");
            EnsureColumnExists("VideoRecords", "SourceDeviceId", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "SourceDeviceName", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "SourceDeviceKind", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "SourceSessionId", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "ContentSha256", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "BackupCompletedAt", "TEXT");
            EnsureColumnExists("VideoRecords", "StorageState", "TEXT NOT NULL DEFAULT 'Local'");
            EnsureColumnExists("VideoRecords", "RemoteVideoRecordId", "INTEGER");
            EnsureColumnExists("VideoRecords", "MkvFirstFailedAt", "TEXT");
            EnsureColumnExists("VideoRecords", "MkvLastAttemptAt", "TEXT");
            EnsureColumnExists("VideoRecords", "MkvFailureCount", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumnExists("VideoRecords", "MkvLastError", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "MkvLastNotifiedAt", "TEXT");
            EnsureColumnExists("RecordingTransferQueue", "NextAttemptAt", "TEXT");
            EnsureColumnExists("RecordingTransferQueue", "CacheDeletedAt", "TEXT");
            ExecuteNonQuery(@"
                UPDATE VideoRecords
                SET BackupCompletedAt = COALESCE(EndTime, StartTime)
                WHERE SourceType = 'external'
                  AND (BackupCompletedAt IS NULL OR BackupCompletedAt = '');");

            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_orderid ON VideoRecords(OrderId);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_starttime ON VideoRecords(StartTime);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_filepath ON VideoRecords(FilePath);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_isdeleted ON VideoRecords(IsDeleted);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_active_starttime ON VideoRecords(IsDeleted, StartTime DESC);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_mobile_history ON VideoRecords(IsDeleted, StartTime DESC, Id DESC);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_tracking ON VideoRecords(TrackingNumber);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_source_order ON VideoRecords(SourceOrderId);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_orderinfo_source_order ON OrderInfoRecords(SourceOrderId);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_orderinfo_push_time ON OrderInfoRecords(PushTime DESC);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_content_sha256 ON VideoRecords(ContentSha256);");
            ExecuteNonQuery("CREATE UNIQUE INDEX IF NOT EXISTS idx_video_external_session ON VideoRecords(SourceDeviceId, SourceSessionId) WHERE SourceType = 'external' AND SourceDeviceId <> '' AND SourceSessionId <> '';");
            CleanupExpiredOrderInfos();
        }

        /// <summary>
        /// 录制开始时插入记录，返回记录 ID
        /// </summary>
        public long InsertVideoRecord(
            string orderId,
            string mode,
            string videoCodec,
            string videoEncoder,
            string filePath,
            DateTime startTime,
            OrderInfo orderInfo = null,
            string sourceDeviceId = "",
            string sourceDeviceName = "")
        {
            string orderInfoJson = SerializeOrderInfo(orderInfo);
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO VideoRecords (
                        OrderId, Mode, TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo,
                        OrderInfoPushTime, OrderInfoJson, SourceType, SourceDeviceId, SourceDeviceName,
                        VideoCodec, VideoEncoder, FilePath, StartTime)
                    VALUES (
                        @orderId, @mode, @trackingNumber, @sourceOrderId, @buyerMessage, @sellerMemo, @productInfo,
                        @orderInfoPushTime, @orderInfoJson, 'pc', @sourceDeviceId, @sourceDeviceName,
                        @videoCodec, @videoEncoder, @filePath, @startTime);
                    SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@orderId", orderId ?? "");
                cmd.Parameters.AddWithValue("@mode", mode ?? "");
                cmd.Parameters.AddWithValue("@trackingNumber", FirstNotEmpty(orderInfo?.TrackingNumber, orderId));
                cmd.Parameters.AddWithValue("@sourceOrderId", orderInfo?.OrderId ?? "");
                cmd.Parameters.AddWithValue("@buyerMessage", orderInfo?.BuyerMessage ?? "");
                cmd.Parameters.AddWithValue("@sellerMemo", orderInfo?.SellerMemo ?? "");
                cmd.Parameters.AddWithValue("@productInfo", orderInfo?.ProductInfo ?? "");
                cmd.Parameters.AddWithValue("@orderInfoPushTime", orderInfo == null ? DBNull.Value : orderInfo.PushTime.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@orderInfoJson", orderInfoJson);
                cmd.Parameters.AddWithValue("@sourceDeviceId", sourceDeviceId?.Trim() ?? "");
                cmd.Parameters.AddWithValue("@sourceDeviceName", sourceDeviceName?.Trim() ?? "");
                cmd.Parameters.AddWithValue("@videoCodec", videoCodec ?? "");
                cmd.Parameters.AddWithValue("@videoEncoder", videoEncoder ?? "");
                cmd.Parameters.AddWithValue("@filePath", filePath ?? "");
                cmd.Parameters.AddWithValue("@startTime", startTime.ToString("yyyy-MM-dd HH:mm:ss"));
                return (long)cmd.ExecuteScalar();
            }
        }

        public long InsertMobileBackupRecord(
            string trackingNumber,
            string filePath,
            long fileSizeBytes,
            DateTime startTime,
            double durationSeconds,
            string sourceDeviceId,
            string sourceDeviceName,
            string sourceSessionId,
            string contentSha256,
            OrderInfo orderInfo = null,
            string sourceDeviceKind = "mobile",
            string mode = "发货")
        {
            string normalizedTracking = trackingNumber?.Trim().ToUpperInvariant() ?? "";
            string orderId = string.IsNullOrEmpty(normalizedTracking) ? "未识别面单" : normalizedTracking;
            string orderInfoJson = SerializeOrderInfo(orderInfo);
            DateTime endTime = startTime.AddSeconds(Math.Max(0, durationSeconds));

            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO VideoRecords (
                        OrderId, Mode, TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo,
                        OrderInfoPushTime, OrderInfoJson, SourceType, SourceDeviceId, SourceDeviceName,
                        SourceDeviceKind, SourceSessionId, ContentSha256, FilePath, FileSizeBytes, StartTime, EndTime,
                        DurationSeconds, StopReason, BackupCompletedAt)
                    VALUES (
                        @orderId, @mode, @trackingNumber, @sourceOrderId, @buyerMessage, @sellerMemo, @productInfo,
                        @orderInfoPushTime, @orderInfoJson, 'external', @sourceDeviceId, @sourceDeviceName,
                        @sourceDeviceKind, @sourceSessionId, @contentSha256, @filePath, @fileSizeBytes, @startTime, @endTime,
                        @durationSeconds, @stopReason, @backupCompletedAt);
                    SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@orderId", orderId);
                cmd.Parameters.AddWithValue("@mode", NormalizeRecordingMode(mode));
                cmd.Parameters.AddWithValue("@trackingNumber", normalizedTracking);
                cmd.Parameters.AddWithValue("@sourceOrderId", orderInfo?.OrderId ?? "");
                cmd.Parameters.AddWithValue("@buyerMessage", orderInfo?.BuyerMessage ?? "");
                cmd.Parameters.AddWithValue("@sellerMemo", orderInfo?.SellerMemo ?? "");
                cmd.Parameters.AddWithValue("@productInfo", orderInfo?.ProductInfo ?? "");
                cmd.Parameters.AddWithValue("@orderInfoPushTime", orderInfo == null ? DBNull.Value : orderInfo.PushTime.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@orderInfoJson", orderInfoJson);
                cmd.Parameters.AddWithValue("@sourceDeviceId", sourceDeviceId?.Trim() ?? "");
                cmd.Parameters.AddWithValue("@sourceDeviceName", sourceDeviceName?.Trim() ?? "");
                cmd.Parameters.AddWithValue("@sourceDeviceKind",
                    string.Equals(sourceDeviceKind, "pc", StringComparison.OrdinalIgnoreCase) ? "pc" : "mobile");
                cmd.Parameters.AddWithValue("@sourceSessionId", sourceSessionId?.Trim() ?? "");
                cmd.Parameters.AddWithValue("@contentSha256", contentSha256?.Trim().ToLowerInvariant() ?? "");
                cmd.Parameters.AddWithValue("@filePath", filePath ?? "");
                cmd.Parameters.AddWithValue("@fileSizeBytes", fileSizeBytes);
                cmd.Parameters.AddWithValue("@startTime", startTime.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@endTime", endTime.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@durationSeconds", Math.Max(0, durationSeconds));
                cmd.Parameters.AddWithValue(
                    "@stopReason",
                    string.Equals(sourceDeviceKind, "pc", StringComparison.OrdinalIgnoreCase)
                        ? "电脑工位上传"
                        : "APP 备份");
                cmd.Parameters.AddWithValue("@backupCompletedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                return (long)cmd.ExecuteScalar();
            }
        }

        internal static string NormalizeRecordingMode(string mode)
        {
            string normalized = mode?.Trim() ?? "";
            return normalized.Equals("return", StringComparison.OrdinalIgnoreCase)
                   || normalized.Equals("退货", StringComparison.Ordinal)
                ? "退货"
                : "发货";
        }

        public IReadOnlyList<MobileBackupDailyCount> GetMobileBackupDailyCounts(DateTime day)
        {
            DateTime start = day.Date;
            DateTime end = start.AddDays(1);
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT SourceDeviceId, MAX(SourceDeviceName), COUNT(1), MAX(SourceDeviceKind)
                    FROM VideoRecords
                    WHERE IsDeleted = 0
                      AND SourceType = 'external'
                      AND SourceDeviceId <> ''
                      AND BackupCompletedAt >= @start
                      AND BackupCompletedAt < @end
                    GROUP BY SourceDeviceId
                    ORDER BY MAX(SourceDeviceName), SourceDeviceId;";
                cmd.Parameters.AddWithValue("@start", start.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@end", end.ToString("yyyy-MM-dd HH:mm:ss"));
                using var reader = cmd.ExecuteReader();
                var result = new List<MobileBackupDailyCount>();
                while (reader.Read())
                {
                    result.Add(new MobileBackupDailyCount(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? "" : reader.GetString(1),
                        reader.GetInt32(2),
                        reader.IsDBNull(3) ? "" : reader.GetString(3)));
                }
                return result;
            }
        }

        public MobileBackupOverview GetMobileBackupOverview(DateTime day)
        {
            DateTime start = day.Date;
            DateTime end = start.AddDays(1);
            lock (_lock)
            {
                var deviceCounts = new List<MobileBackupDailyCount>();
                using (var dailyCommand = _connection.CreateCommand())
                {
                    dailyCommand.CommandText = @"
                        SELECT SourceDeviceId, MAX(SourceDeviceName), COUNT(1), MAX(SourceDeviceKind)
                        FROM VideoRecords
                        WHERE SourceType = 'external'
                          AND SourceDeviceId <> ''
                          AND BackupCompletedAt IS NOT NULL
                          AND BackupCompletedAt <> ''
                          AND BackupCompletedAt >= @start
                          AND BackupCompletedAt < @end
                        GROUP BY SourceDeviceId
                        ORDER BY MAX(SourceDeviceName), SourceDeviceId;";
                    dailyCommand.Parameters.AddWithValue("@start", start.ToString("yyyy-MM-dd HH:mm:ss"));
                    dailyCommand.Parameters.AddWithValue("@end", end.ToString("yyyy-MM-dd HH:mm:ss"));
                    using var reader = dailyCommand.ExecuteReader();
                    while (reader.Read())
                    {
                        deviceCounts.Add(new MobileBackupDailyCount(
                            reader.GetString(0),
                            reader.IsDBNull(1) ? "" : reader.GetString(1),
                            reader.GetInt32(2),
                            reader.IsDBNull(3) ? "" : reader.GetString(3)));
                    }
                }

                using var totalCommand = _connection.CreateCommand();
                totalCommand.CommandText = @"
                    SELECT COUNT(1)
                    FROM VideoRecords
                    WHERE SourceType = 'external'
                      AND BackupCompletedAt IS NOT NULL
                      AND BackupCompletedAt <> '';";
                int totalCount = Convert.ToInt32(totalCommand.ExecuteScalar());
                return new MobileBackupOverview(
                    deviceCounts,
                    deviceCounts.Sum(item => item.VideoCount),
                    totalCount);
            }
        }

        public VideoRecord GetVideoBySourceSession(string sourceDeviceId, string sourceSessionId)
        {
            if (string.IsNullOrWhiteSpace(sourceDeviceId) || string.IsNullOrWhiteSpace(sourceSessionId))
                return null;

            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, OrderId, Mode, VideoCodec, VideoEncoder, FilePath, FileSizeBytes,
                           StartTime, EndTime, DurationSeconds, StopReason,
                           IsDeleted, DeletedAt, DeleteReason,
                           TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo, OrderInfoPushTime, OrderInfoJson,
                           SourceType, SourceDeviceId, SourceDeviceName, SourceSessionId, ContentSha256,
                           StorageState, RemoteVideoRecordId, SourceDeviceKind
                    FROM VideoRecords
                    WHERE SourceType = 'external' AND SourceDeviceId = @sourceDeviceId AND SourceSessionId = @sourceSessionId
                    LIMIT 1;";
                cmd.Parameters.AddWithValue("@sourceDeviceId", sourceDeviceId.Trim());
                cmd.Parameters.AddWithValue("@sourceSessionId", sourceSessionId.Trim());
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadVideoRecord(reader) : null;
            }
        }

        public VideoRecord GetVideoByContentSha256(string contentSha256)
        {
            if (string.IsNullOrWhiteSpace(contentSha256)) return null;
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, OrderId, Mode, VideoCodec, VideoEncoder, FilePath, FileSizeBytes,
                           StartTime, EndTime, DurationSeconds, StopReason,
                           IsDeleted, DeletedAt, DeleteReason,
                           TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo, OrderInfoPushTime, OrderInfoJson,
                           SourceType, SourceDeviceId, SourceDeviceName, SourceSessionId, ContentSha256,
                           StorageState, RemoteVideoRecordId, SourceDeviceKind
                    FROM VideoRecords
                    WHERE ContentSha256 = @contentSha256 AND IsDeleted = 0
                    ORDER BY Id LIMIT 1;";
                cmd.Parameters.AddWithValue("@contentSha256", contentSha256.Trim().ToLowerInvariant());
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadVideoRecord(reader) : null;
            }
        }

        public void UpsertOrderInfos(IEnumerable<OrderInfo> items)
        {
            if (items == null) return;
            lock (_lock)
            {
                using var transaction = _connection.BeginTransaction();
                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item?.TrackingNumber)) continue;
                    string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    using var cmd = _connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        INSERT INTO OrderInfoRecords (
                            TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo,
                            PushTime, OrderInfoJson, CreatedAt, UpdatedAt)
                        VALUES (
                            @trackingNumber, @sourceOrderId, @buyerMessage, @sellerMemo, @productInfo,
                            @pushTime, @orderInfoJson, @now, @now)
                        ON CONFLICT(TrackingNumber) DO UPDATE SET
                            SourceOrderId = excluded.SourceOrderId,
                            BuyerMessage = excluded.BuyerMessage,
                            SellerMemo = excluded.SellerMemo,
                            ProductInfo = excluded.ProductInfo,
                            PushTime = excluded.PushTime,
                            OrderInfoJson = excluded.OrderInfoJson,
                            UpdatedAt = excluded.UpdatedAt
                        WHERE excluded.PushTime >= OrderInfoRecords.PushTime;";
                    AddOrderInfoParameters(cmd, item);
                    cmd.Parameters.AddWithValue("@now", now);
                    cmd.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public List<OrderInfo> GetRecentOrderInfos()
        {
            lock (_lock)
            {
                var results = new List<OrderInfo>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT OrderInfoJson
                    FROM OrderInfoRecords
                    WHERE PushTime >= @since
                    ORDER BY PushTime DESC
                    LIMIT @limit;";
                cmd.Parameters.AddWithValue("@since", DateTime.Now.Subtract(OrderInfoRetention).ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@limit", MaxOrderInfoRecords);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0)) continue;
                    try
                    {
                        var item = JsonSerializer.Deserialize<OrderInfo>(reader.GetString(0));
                        if (item != null && !string.IsNullOrWhiteSpace(item.TrackingNumber))
                            results.Add(item);
                    }
                    catch (JsonException)
                    {
                        // 单条历史数据损坏不应阻止其余订单缓存恢复。
                    }
                }
                return results;
            }
        }

        public void CleanupExpiredOrderInfos()
        {
            lock (_lock)
            {
                using var transaction = _connection.BeginTransaction();
                using (var expired = _connection.CreateCommand())
                {
                    expired.Transaction = transaction;
                    expired.CommandText = "DELETE FROM OrderInfoRecords WHERE PushTime < @cutoff OR PushTime IS NULL;";
                    expired.Parameters.AddWithValue("@cutoff", DateTime.Now.Subtract(OrderInfoRetention).ToString("yyyy-MM-dd HH:mm:ss"));
                    expired.ExecuteNonQuery();
                }
                using (var overflow = _connection.CreateCommand())
                {
                    overflow.Transaction = transaction;
                    overflow.CommandText = @"
                        DELETE FROM OrderInfoRecords
                        WHERE TrackingNumber NOT IN (
                            SELECT TrackingNumber FROM OrderInfoRecords
                            ORDER BY PushTime DESC LIMIT @limit
                        );";
                    overflow.Parameters.AddWithValue("@limit", MaxOrderInfoRecords);
                    overflow.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public void UpdateRecentVideoOrderInfos(IEnumerable<OrderInfo> items)
        {
            if (items == null) return;
            lock (_lock)
            {
                using var transaction = _connection.BeginTransaction();
                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item?.TrackingNumber)) continue;
                    using var cmd = _connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = @"
                        UPDATE VideoRecords SET
                            TrackingNumber = @trackingNumber,
                            SourceOrderId = @sourceOrderId,
                            BuyerMessage = @buyerMessage,
                            SellerMemo = @sellerMemo,
                            ProductInfo = @productInfo,
                            OrderInfoPushTime = @pushTime,
                            OrderInfoJson = @orderInfoJson
                        WHERE IsDeleted = 0
                          AND (StartTime >= @since OR SourceType = 'external')
                          AND (OrderId = @trackingNumber OR TrackingNumber = @trackingNumber)
                          AND (
                              BuyerMessage = '' OR SellerMemo = '' OR ProductInfo = ''
                              OR SourceOrderId = '' OR OrderInfoJson = ''
                          );";
                    AddOrderInfoParameters(cmd, item);
                    cmd.Parameters.AddWithValue("@since", DateTime.Now.AddHours(-72).ToString("yyyy-MM-dd HH:mm:ss"));
                    cmd.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        /// <summary>
        /// 录制结束时更新记录
        /// </summary>
        public void UpdateVideoRecordOnStop(long recordId, DateTime endTime, double durationSeconds, long fileSizeBytes, string stopReason, string videoCodec = null, string videoEncoder = null)
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE VideoRecords SET 
                        EndTime = @endTime, 
                        DurationSeconds = @duration, 
                        FileSizeBytes = @fileSize,
                        StopReason = @stopReason,
                        VideoCodec = COALESCE(@videoCodec, VideoCodec),
                        VideoEncoder = COALESCE(@videoEncoder, VideoEncoder)
                    WHERE Id = @id;";
                cmd.Parameters.AddWithValue("@id", recordId);
                cmd.Parameters.AddWithValue("@endTime", endTime.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@duration", durationSeconds);
                cmd.Parameters.AddWithValue("@fileSize", fileSizeBytes);
                cmd.Parameters.AddWithValue("@stopReason", stopReason ?? "");
                cmd.Parameters.AddWithValue("@videoCodec", (object)videoCodec ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@videoEncoder", (object)videoEncoder ?? DBNull.Value);
                cmd.ExecuteNonQuery();
                string filePath = GetVideoFilePath(recordId);
                if (!string.IsNullOrWhiteSpace(filePath) && fileSizeBytes > 0)
                    UpsertLocalVideoFileCore(filePath, fileSizeBytes);
            }
        }

        public void UpdateVideoFileSize(long recordId, long fileSizeBytes)
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "UPDATE VideoRecords SET FileSizeBytes = @fileSize WHERE Id = @id;";
                cmd.Parameters.AddWithValue("@id", recordId);
                cmd.Parameters.AddWithValue("@fileSize", fileSizeBytes);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 更新视频记录的文件路径（MKV 转 MP4 后调用）
        /// </summary>
        public void UpdateVideoFilePath(string oldPath, string newPath)
        {
            long fileSizeBytes = 0;
            try
            {
                if (File.Exists(newPath))
                    fileSizeBytes = new FileInfo(newPath).Length;
            }
            catch
            {
            }

            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE VideoRecords
                    SET FilePath = @newPath,
                        FileSizeBytes = CASE WHEN @fileSize > 0 THEN @fileSize ELSE FileSizeBytes END,
                        MkvFirstFailedAt = NULL,
                        MkvLastAttemptAt = NULL,
                        MkvFailureCount = 0,
                        MkvLastError = '',
                        MkvLastNotifiedAt = NULL
                    WHERE FilePath = @oldPath;";
                cmd.Parameters.AddWithValue("@oldPath", oldPath);
                cmd.Parameters.AddWithValue("@newPath", newPath);
                cmd.Parameters.AddWithValue("@fileSize", fileSizeBytes);
                cmd.ExecuteNonQuery();
                RemoveLocalVideoFileCore(oldPath);
                if (fileSizeBytes > 0)
                    UpsertLocalVideoFileCore(newPath, fileSizeBytes);
            }
        }

        public List<StorageVideoFile> GetLocalVideoFileInventory()
        {
            lock (_lock)
            {
                var files = new List<StorageVideoFile>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT FilePath, FileSizeBytes, UpdatedAt
                    FROM LocalVideoFileInventory
                    ORDER BY FilePath;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    files.Add(new StorageVideoFile
                    {
                        FilePath = reader.GetString(0),
                        FileSizeBytes = reader.GetInt64(1),
                        StartTime = DateTime.TryParse(reader.GetString(2), out DateTime updatedAt)
                            ? updatedAt
                            : DateTime.MinValue
                    });
                }
                return files;
            }
        }

        public void ReplaceLocalVideoFileInventory(
            string rootPath,
            IEnumerable<StorageVideoFile> files)
        {
            string normalizedRoot = Path.GetFullPath(rootPath);
            StorageVideoFile[] normalizedFiles = files
                .Where(file => file != null
                    && !string.IsNullOrWhiteSpace(file.FilePath)
                    && file.FileSizeBytes >= 0)
                .Select(file => new StorageVideoFile
                {
                    FilePath = Path.GetFullPath(file.FilePath),
                    FileSizeBytes = file.FileSizeBytes,
                    StartTime = file.StartTime
                })
                .Where(file => IsPathInside(file.FilePath, normalizedRoot))
                .GroupBy(file => file.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

            lock (_lock)
            {
                using var transaction = _connection.BeginTransaction();
                using (var select = _connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = "SELECT FilePath FROM LocalVideoFileInventory;";
                    using var reader = select.ExecuteReader();
                    var stalePaths = new List<string>();
                    while (reader.Read())
                    {
                        string existingPath = reader.GetString(0);
                        if (IsPathInside(existingPath, normalizedRoot))
                            stalePaths.Add(existingPath);
                    }
                    reader.Close();
                    foreach (string stalePath in stalePaths)
                        RemoveLocalVideoFileCore(stalePath, transaction);
                }

                foreach (StorageVideoFile file in normalizedFiles)
                    UpsertLocalVideoFileCore(file.FilePath, file.FileSizeBytes, transaction);
                transaction.Commit();
            }
        }

        public bool IsLocalVideoFileFullyVerifiedForCacheDeletion(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;
            string normalizedPath = Path.GetFullPath(filePath);
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT COUNT(1),
                           SUM(CASE WHEN
                               v.StorageState <> 'Uploaded'
                               OR v.RemoteVideoRecordId IS NULL
                               OR v.RemoteVideoRecordId <= 0
                               OR q.State <> 'Uploaded'
                               OR q.RemoteVideoRecordId IS NULL
                               OR q.RemoteVideoRecordId <= 0
                           THEN 1 ELSE 0 END)
                    FROM VideoRecords v
                    LEFT JOIN RecordingTransferQueue q
                      ON q.LocalVideoRecordId = v.Id
                    WHERE v.IsDeleted = 0
                      AND v.SourceType = 'pc'
                      AND v.FilePath = @filePath;";
                cmd.Parameters.AddWithValue("@filePath", normalizedPath);
                using var reader = cmd.ExecuteReader();
                return reader.Read()
                    && reader.GetInt64(0) > 0
                    && !reader.IsDBNull(1)
                    && reader.GetInt64(1) == 0;
            }
        }

        public MkvConversionFailureState GetMkvConversionFailureState(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            (string mkvPath, string mp4Path) = GetMkvAndMp4Paths(filePath);
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT MIN(FilePath),
                           MIN(MkvFirstFailedAt),
                           MAX(MkvLastAttemptAt),
                           MAX(MkvFailureCount),
                           MAX(MkvLastError),
                           MAX(MkvLastNotifiedAt)
                    FROM VideoRecords
                    WHERE IsDeleted = 0
                      AND (FilePath = @mkvPath OR FilePath = @mp4Path);";
                cmd.Parameters.AddWithValue("@mkvPath", mkvPath);
                cmd.Parameters.AddWithValue("@mp4Path", mp4Path);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0))
                    return null;

                return new MkvConversionFailureState
                {
                    FilePath = reader.GetString(0),
                    FirstFailedAt = ReadNullableDateTime(reader, 1),
                    LastAttemptAt = ReadNullableDateTime(reader, 2),
                    FailureCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    LastError = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    LastNotifiedAt = ReadNullableDateTime(reader, 5)
                };
            }
        }

        public void RecordMkvConversionFailure(string filePath, DateTime attemptedAt, string error)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            (string mkvPath, string mp4Path) = GetMkvAndMp4Paths(filePath);
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE VideoRecords
                    SET MkvFirstFailedAt = COALESCE(MkvFirstFailedAt, @attemptedAt),
                        MkvLastAttemptAt = @attemptedAt,
                        MkvFailureCount = COALESCE(MkvFailureCount, 0) + 1,
                        MkvLastError = @error
                    WHERE IsDeleted = 0
                      AND (FilePath = @mkvPath OR FilePath = @mp4Path);";
                cmd.Parameters.AddWithValue("@mkvPath", mkvPath);
                cmd.Parameters.AddWithValue("@mp4Path", mp4Path);
                cmd.Parameters.AddWithValue("@attemptedAt", ToDatabaseTimestamp(attemptedAt));
                cmd.Parameters.AddWithValue("@error", error ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        public void ClearMkvConversionFailure(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            (string mkvPath, string mp4Path) = GetMkvAndMp4Paths(filePath);
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE VideoRecords
                    SET MkvFirstFailedAt = NULL,
                        MkvLastAttemptAt = NULL,
                        MkvFailureCount = 0,
                        MkvLastError = '',
                        MkvLastNotifiedAt = NULL
                    WHERE FilePath = @mkvPath OR FilePath = @mp4Path;";
                cmd.Parameters.AddWithValue("@mkvPath", mkvPath);
                cmd.Parameters.AddWithValue("@mp4Path", mp4Path);
                cmd.ExecuteNonQuery();
            }
        }

        public int ClaimMkvFailureNotifications(IEnumerable<string> filePaths, DateTime now)
        {
            if (filePaths == null)
                return 0;

            int claimed = 0;
            lock (_lock)
            {
                foreach (string filePath in filePaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    MkvConversionFailureState state = GetMkvConversionFailureState(filePath);
                    if (!MkvConversionRetryPolicy.ShouldNotify(state, now))
                        continue;

                    (string mkvPath, string mp4Path) = GetMkvAndMp4Paths(filePath);
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = @"
                        UPDATE VideoRecords
                        SET MkvLastNotifiedAt = @notifiedAt
                        WHERE IsDeleted = 0
                          AND (FilePath = @mkvPath OR FilePath = @mp4Path)
                          AND (
                              MkvLastNotifiedAt IS NULL
                              OR substr(MkvLastNotifiedAt, 1, 10) < @today
                          );";
                    cmd.Parameters.AddWithValue("@mkvPath", mkvPath);
                    cmd.Parameters.AddWithValue("@mp4Path", mp4Path);
                    cmd.Parameters.AddWithValue("@notifiedAt", ToDatabaseTimestamp(now));
                    cmd.Parameters.AddWithValue("@today", now.ToString("yyyy-MM-dd"));
                    if (cmd.ExecuteNonQuery() > 0)
                        claimed++;
                }
            }

            return claimed;
        }

        private static (string MkvPath, string Mp4Path) GetMkvAndMp4Paths(string filePath)
        {
            string extension = Path.GetExtension(filePath);
            string mkvPath = extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
                ? filePath
                : Path.ChangeExtension(filePath, ".mkv");
            string mp4Path = extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                ? filePath
                : Path.ChangeExtension(filePath, ".mp4");
            return (mkvPath, mp4Path);
        }

        private static DateTime? ReadNullableDateTime(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal))
                return null;

            return DateTime.TryParse(reader.GetString(ordinal), out DateTime value)
                ? value
                : null;
        }

        private static string ToDatabaseTimestamp(DateTime value) =>
            value.ToString("yyyy-MM-dd HH:mm:ss.fff");

        /// <summary>
        /// 查询所有未删除且文件路径以 .mkv 结尾的记录
        /// </summary>
        public List<string> QueryMkvFilePaths()
        {
            lock (_lock)
            {
                var paths = new List<string>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT FilePath FROM VideoRecords WHERE IsDeleted = 0 AND FilePath LIKE '%.mkv';";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    paths.Add(reader.GetString(0));
                }
                return paths;
            }
        }

        /// <summary>
        /// 查询所有未删除的视频文件路径，用于恢复异常的 MKV/WAV/MP4 残留状态。
        /// </summary>
        public List<string> QueryActiveVideoFilePaths()
        {
            lock (_lock)
            {
                var paths = new List<string>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT FilePath FROM VideoRecords WHERE IsDeleted = 0;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    paths.Add(reader.GetString(0));
                }
                return paths;
            }
        }

        /// <summary>
        /// 标记视频为已删除并写入删除日志
        /// </summary>
        public void MarkVideoDeleted(string filePath, string reason)
        {
            lock (_lock)
            {
                using var transaction = _connection.BeginTransaction();
                try
                {
                    // 更新视频记录
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            UPDATE VideoRecords SET 
                                IsDeleted = 1, DeletedAt = @deletedAt, DeleteReason = @reason
                            WHERE FilePath = @filePath AND IsDeleted = 0;";
                        cmd.Parameters.AddWithValue("@filePath", filePath);
                        cmd.Parameters.AddWithValue("@deletedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        cmd.Parameters.AddWithValue("@reason", reason ?? "");
                        cmd.ExecuteNonQuery();
                    }

                    // 写入删除日志
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = @"
                            INSERT INTO DeleteLogs (FilePath, OrderId, FileSizeBytes, DeletedAt, Reason)
                            SELECT FilePath, OrderId, FileSizeBytes, @deletedAt, @reason
                            FROM VideoRecords WHERE FilePath = @filePath
                            LIMIT 1;";
                        cmd.Parameters.AddWithValue("@filePath", filePath);
                        cmd.Parameters.AddWithValue("@deletedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        cmd.Parameters.AddWithValue("@reason", reason ?? "");
                        cmd.ExecuteNonQuery();
                    }

                    RemoveLocalVideoFileCore(filePath, transaction);
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                }
            }
        }

        /// <summary>
        /// 检查最近30天内是否已存在指定单号的未删除录像记录（含录制中的记录）
        /// </summary>
        /// <param name="orderId">要检查的单号</param>
        /// <param name="excludeRecordId">排除的记录ID（通常是当前刚插入的记录），0表示不排除</param>
        public bool OrderIdExistsRecent(string orderId, long excludeRecordId = 0)
        {
            if (string.IsNullOrWhiteSpace(orderId)) return false;
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT COUNT(1) FROM VideoRecords
                    WHERE OrderId = @orderId
                      AND StartTime >= @since
                      AND IsDeleted = 0
                      AND Id <> @excludeId;";
                cmd.Parameters.AddWithValue("@orderId", orderId);
                cmd.Parameters.AddWithValue("@since", DateTime.Now.Subtract(DuplicateOrderLookback).ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@excludeId", excludeRecordId);
                return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
            }
        }

        /// <summary>
        /// 获取指定日期最近完成且未删除的扫码录像记录。
        /// </summary>
        public List<VideoRecord> GetRecentCompletedVideos(DateTime date, int limit = 10, string sourceType = null)
        {
            if (limit <= 0) return new List<VideoRecord>();

            lock (_lock)
            {
                var results = new List<VideoRecord>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, OrderId, Mode, StartTime
                    FROM VideoRecords
                    WHERE IsDeleted = 0
                      AND EndTime IS NOT NULL
                      AND StartTime >= @startTime
                      AND StartTime < @endTime
                      AND (@sourceType = '' OR SourceType = @sourceType)
                    ORDER BY StartTime DESC, Id DESC
                    LIMIT @limit;";
                cmd.Parameters.AddWithValue("@startTime", date.Date.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@endTime", date.Date.AddDays(1).ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@limit", limit);
                cmd.Parameters.AddWithValue("@sourceType", sourceType?.Trim() ?? "");

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new VideoRecord
                    {
                        Id = reader.GetInt64(0),
                        OrderId = reader.GetString(1),
                        Mode = reader.GetString(2),
                        StartTime = DateTime.Parse(reader.GetString(3))
                    });
                }

                return results;
            }
        }

        /// <summary>
        /// 查询视频列表（支持日期范围 + 关键词过滤，包含已删除记录）
        /// </summary>
        public VideoRecord GetVideoById(long id)
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, OrderId, Mode, VideoCodec, VideoEncoder, FilePath, FileSizeBytes,
                           StartTime, EndTime, DurationSeconds, StopReason,
                           IsDeleted, DeletedAt, DeleteReason,
                           TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo, OrderInfoPushTime, OrderInfoJson,
                           SourceType, SourceDeviceId, SourceDeviceName, SourceSessionId, ContentSha256,
                           StorageState, RemoteVideoRecordId, SourceDeviceKind
                    FROM VideoRecords WHERE Id = @id AND IsDeleted = 0;";
                cmd.Parameters.AddWithValue("@id", id);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return new VideoRecord
                    {
                        Id = reader.GetInt64(0),
                        OrderId = reader.GetString(1),
                        Mode = reader.GetString(2),
                        VideoCodec = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        VideoEncoder = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        FilePath = reader.GetString(5),
                        FileSizeBytes = reader.GetInt64(6),
                        StartTime = DateTime.Parse(reader.GetString(7)),
                        EndTime = reader.IsDBNull(8) ? DateTime.MinValue : DateTime.Parse(reader.GetString(8)),
                        DurationSeconds = reader.GetDouble(9),
                        StopReason = reader.IsDBNull(10) ? "" : reader.GetString(10),
                        IsDeleted = reader.GetInt64(11) == 1,
                        DeletedAt = reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12)),
                        DeleteReason = reader.IsDBNull(13) ? "" : reader.GetString(13),
                        TrackingNumber = reader.IsDBNull(14) ? "" : reader.GetString(14),
                        SourceOrderId = reader.IsDBNull(15) ? "" : reader.GetString(15),
                        BuyerMessage = reader.IsDBNull(16) ? "" : reader.GetString(16),
                        SellerMemo = reader.IsDBNull(17) ? "" : reader.GetString(17),
                        ProductInfo = reader.IsDBNull(18) ? "" : reader.GetString(18),
                        OrderInfoPushTime = reader.IsDBNull(19) ? null : DateTime.Parse(reader.GetString(19)),
                        OrderInfoJson = reader.IsDBNull(20) ? "" : reader.GetString(20),
                        SourceType = reader.IsDBNull(21) ? "pc" : reader.GetString(21),
                        SourceDeviceId = reader.IsDBNull(22) ? "" : reader.GetString(22),
                        SourceDeviceName = reader.IsDBNull(23) ? "" : reader.GetString(23),
                        SourceSessionId = reader.IsDBNull(24) ? "" : reader.GetString(24),
                        ContentSha256 = reader.IsDBNull(25) ? "" : reader.GetString(25),
                        StorageState = reader.IsDBNull(26) ? "Local" : reader.GetString(26),
                        RemoteVideoRecordId = reader.IsDBNull(27) ? null : reader.GetInt64(27),
                        SourceDeviceKind = reader.IsDBNull(28) ? "" : reader.GetString(28)
                    };
                }
                return null;
            }
        }

        /// <summary>
        /// 查询视频列表（支持日期范围 + 关键词过滤，包含已删除记录）
        /// </summary>
        public List<VideoRecord> QueryVideos(DateTime? startDate, DateTime? endDate, string keyword = null)
        {
            lock (_lock)
            {
                var results = new List<VideoRecord>();
                using var cmd = _connection.CreateCommand();

                string sql = @"
                      SELECT Id, OrderId, Mode, VideoCodec, VideoEncoder, FilePath, FileSizeBytes,
                          StartTime, EndTime, DurationSeconds, StopReason,
                           IsDeleted, DeletedAt, DeleteReason,
                           TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo, OrderInfoPushTime, OrderInfoJson,
                           SourceType, SourceDeviceId, SourceDeviceName, SourceSessionId, ContentSha256,
                           StorageState, RemoteVideoRecordId, SourceDeviceKind
                    FROM VideoRecords 
                    WHERE 1 = 1";

                if (startDate.HasValue)
                    sql += " AND StartTime >= @startDate";

                if (endDate.HasValue)
                    sql += " AND StartTime < @endDate";

                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    sql += @" AND (
                        OrderId LIKE @keyword OR FilePath LIKE @keyword OR TrackingNumber LIKE @keyword
                        OR SourceOrderId LIKE @keyword OR BuyerMessage LIKE @keyword
                        OR SellerMemo LIKE @keyword OR ProductInfo LIKE @keyword
                        OR SourceDeviceName LIKE @keyword)";
                    cmd.Parameters.AddWithValue("@keyword", $"%{keyword}%");
                }

                sql += " ORDER BY StartTime DESC;";
                cmd.CommandText = sql;
                if (startDate.HasValue)
                    cmd.Parameters.AddWithValue("@startDate", startDate.Value.ToString("yyyy-MM-dd 00:00:00"));
                if (endDate.HasValue)
                    cmd.Parameters.AddWithValue("@endDate", endDate.Value.AddDays(1).ToString("yyyy-MM-dd 00:00:00"));

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new VideoRecord
                    {
                        Id = reader.GetInt64(0),
                        OrderId = reader.GetString(1),
                        Mode = reader.GetString(2),
                        VideoCodec = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        VideoEncoder = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        FilePath = reader.GetString(5),
                        FileSizeBytes = reader.GetInt64(6),
                        StartTime = DateTime.Parse(reader.GetString(7)),
                        EndTime = reader.IsDBNull(8) ? DateTime.MinValue : DateTime.Parse(reader.GetString(8)),
                        DurationSeconds = reader.GetDouble(9),
                        StopReason = reader.IsDBNull(10) ? "" : reader.GetString(10),
                        IsDeleted = reader.GetInt64(11) == 1,
                        DeletedAt = reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12)),
                        DeleteReason = reader.IsDBNull(13) ? "" : reader.GetString(13),
                        TrackingNumber = reader.IsDBNull(14) ? "" : reader.GetString(14),
                        SourceOrderId = reader.IsDBNull(15) ? "" : reader.GetString(15),
                        BuyerMessage = reader.IsDBNull(16) ? "" : reader.GetString(16),
                        SellerMemo = reader.IsDBNull(17) ? "" : reader.GetString(17),
                        ProductInfo = reader.IsDBNull(18) ? "" : reader.GetString(18),
                        OrderInfoPushTime = reader.IsDBNull(19) ? null : DateTime.Parse(reader.GetString(19)),
                        OrderInfoJson = reader.IsDBNull(20) ? "" : reader.GetString(20),
                        SourceType = reader.IsDBNull(21) ? "pc" : reader.GetString(21),
                        SourceDeviceId = reader.IsDBNull(22) ? "" : reader.GetString(22),
                        SourceDeviceName = reader.IsDBNull(23) ? "" : reader.GetString(23),
                        SourceSessionId = reader.IsDBNull(24) ? "" : reader.GetString(24),
                        ContentSha256 = reader.IsDBNull(25) ? "" : reader.GetString(25),
                        StorageState = reader.IsDBNull(26) ? "Local" : reader.GetString(26),
                        RemoteVideoRecordId = reader.IsDBNull(27) ? null : reader.GetInt64(27),
                        SourceDeviceKind = reader.IsDBNull(28) ? "" : reader.GetString(28)
                    });
                }
                return results;
            }
        }

        private static VideoRecord ReadVideoRecord(SqliteDataReader reader)
        {
            return new VideoRecord
            {
                Id = reader.GetInt64(0),
                OrderId = reader.GetString(1),
                Mode = reader.GetString(2),
                VideoCodec = reader.IsDBNull(3) ? "" : reader.GetString(3),
                VideoEncoder = reader.IsDBNull(4) ? "" : reader.GetString(4),
                FilePath = reader.GetString(5),
                FileSizeBytes = reader.GetInt64(6),
                StartTime = DateTime.Parse(reader.GetString(7)),
                EndTime = reader.IsDBNull(8) ? DateTime.MinValue : DateTime.Parse(reader.GetString(8)),
                DurationSeconds = reader.GetDouble(9),
                StopReason = reader.IsDBNull(10) ? "" : reader.GetString(10),
                IsDeleted = reader.GetInt64(11) == 1,
                DeletedAt = reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12)),
                DeleteReason = reader.IsDBNull(13) ? "" : reader.GetString(13),
                TrackingNumber = reader.IsDBNull(14) ? "" : reader.GetString(14),
                SourceOrderId = reader.IsDBNull(15) ? "" : reader.GetString(15),
                BuyerMessage = reader.IsDBNull(16) ? "" : reader.GetString(16),
                SellerMemo = reader.IsDBNull(17) ? "" : reader.GetString(17),
                ProductInfo = reader.IsDBNull(18) ? "" : reader.GetString(18),
                OrderInfoPushTime = reader.IsDBNull(19) ? null : DateTime.Parse(reader.GetString(19)),
                OrderInfoJson = reader.IsDBNull(20) ? "" : reader.GetString(20),
                SourceType = reader.IsDBNull(21) ? "pc" : reader.GetString(21),
                SourceDeviceId = reader.IsDBNull(22) ? "" : reader.GetString(22),
                SourceDeviceName = reader.IsDBNull(23) ? "" : reader.GetString(23),
                SourceSessionId = reader.IsDBNull(24) ? "" : reader.GetString(24),
                ContentSha256 = reader.IsDBNull(25) ? "" : reader.GetString(25),
                StorageState = reader.IsDBNull(26) ? "Local" : reader.GetString(26),
                RemoteVideoRecordId = reader.IsDBNull(27) ? null : reader.GetInt64(27),
                SourceDeviceKind = reader.IsDBNull(28) ? "" : reader.GetString(28)
            };
        }

        public PagedVideoResult QueryVideosPaged(
            DateTime? startDate,
            DateTime? endDate,
            string keyword,
            int page,
            int pageSize,
            bool includeDeleted = false,
            string sourceType = "",
            string deviceId = "",
            string sourceDeviceName = "")
        {
            return QueryVideosPaged(
                startDate,
                endDate,
                keyword,
                page,
                pageSize,
                includeDeleted,
                VideoSearchMode.BroadContains,
                sourceType,
                deviceId,
                sourceDeviceName);
        }

        public List<VideoRecord> GetCompletedPcVideosForTransfer(
            DateTime? recordedAfter = null,
            int limit = 500)
        {
            if (limit <= 0) return new List<VideoRecord>();
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, OrderId, Mode, VideoCodec, VideoEncoder, FilePath, FileSizeBytes,
                           StartTime, EndTime, DurationSeconds, StopReason,
                           IsDeleted, DeletedAt, DeleteReason,
                           TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo,
                           OrderInfoPushTime, OrderInfoJson, SourceType, SourceDeviceId,
                           SourceDeviceName, SourceSessionId, ContentSha256,
                           StorageState, RemoteVideoRecordId, SourceDeviceKind
                    FROM VideoRecords
                    WHERE SourceType = 'pc'
                      AND IsDeleted = 0
                      AND EndTime IS NOT NULL
                      AND FilePath <> ''
                      AND lower(FilePath) LIKE '%.mp4'
                      AND (@recordedAfter = '' OR StartTime >= @recordedAfter)
                    ORDER BY StartTime ASC, Id ASC
                    LIMIT @limit;";
                cmd.Parameters.AddWithValue("@limit", limit);
                cmd.Parameters.AddWithValue(
                    "@recordedAfter",
                    recordedAfter?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "");
                using var reader = cmd.ExecuteReader();
                var result = new List<VideoRecord>();
                while (reader.Read())
                    result.Add(ReadVideoRecord(reader));
                return result;
            }
        }

        public void MarkVideoUploaded(long localRecordId, long remoteRecordId)
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE VideoRecords
                    SET StorageState = 'Uploaded',
                        RemoteVideoRecordId = @remoteRecordId
                    WHERE Id = @id AND IsDeleted = 0;";
                cmd.Parameters.AddWithValue("@id", localRecordId);
                cmd.Parameters.AddWithValue("@remoteRecordId", remoteRecordId);
                cmd.ExecuteNonQuery();
            }
        }

        public void MarkVideoCacheDeleted(long localRecordId, long remoteRecordId)
        {
            lock (_lock)
            {
                string filePath = GetVideoFilePath(localRecordId);
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE VideoRecords
                    SET StorageState = 'Remote',
                        RemoteVideoRecordId = @remoteRecordId
                    WHERE Id = @id AND IsDeleted = 0;";
                cmd.Parameters.AddWithValue("@id", localRecordId);
                cmd.Parameters.AddWithValue("@remoteRecordId", remoteRecordId);
                cmd.ExecuteNonQuery();
                if (!string.IsNullOrWhiteSpace(filePath))
                    RemoveLocalVideoFileCore(filePath);
            }
        }

        internal PagedVideoResult QueryVideosPaged(
            DateTime? startDate,
            DateTime? endDate,
            string keyword,
            int page,
            int pageSize,
            bool includeDeleted,
            VideoSearchMode searchMode,
            string sourceType = "",
            string deviceId = "",
            string sourceDeviceName = "")
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            int offset = (page - 1) * pageSize;
            string normalizedKeyword = keyword?.Trim() ?? "";
            string normalizedSourceType = sourceType?.Trim().ToLowerInvariant() ?? "";
            string normalizedDeviceId = deviceId?.Trim() ?? "";
            string normalizedSourceDeviceName = sourceDeviceName?.Trim() ?? "";
            if (normalizedSourceType.Length == 0 && normalizedDeviceId.Length > 0)
                normalizedSourceType = "external";

            lock (_lock)
            {
                using var countCmd = _connection.CreateCommand();
                string whereSql = @"
                    FROM VideoRecords
                    WHERE 1 = 1";

                if (startDate.HasValue)
                    whereSql += " AND StartTime >= @startDate";

                if (endDate.HasValue)
                    whereSql += " AND StartTime < @endDate";

                if (!includeDeleted)
                    whereSql += " AND IsDeleted = 0";

                if (normalizedSourceType is "pc" or "external")
                {
                    whereSql += " AND SourceType = @sourceType";
                    countCmd.Parameters.AddWithValue("@sourceType", normalizedSourceType);
                }

                if (normalizedSourceType == "external" && normalizedDeviceId.Length > 0)
                {
                    whereSql += " AND SourceDeviceId = @deviceId";
                    countCmd.Parameters.AddWithValue("@deviceId", normalizedDeviceId);
                }
                else if (normalizedSourceType == "external" && normalizedSourceDeviceName.Length > 0)
                {
                    whereSql += " AND SourceDeviceName = @sourceDeviceName";
                    countCmd.Parameters.AddWithValue("@sourceDeviceName", normalizedSourceDeviceName);
                }

                if (normalizedKeyword.Length > 0)
                {
                    if (searchMode == VideoSearchMode.ExactOrderIdentifiers)
                    {
                        whereSql += @" AND (
                            OrderId = @keyword OR TrackingNumber = @keyword OR SourceOrderId = @keyword)";
                        countCmd.Parameters.AddWithValue("@keyword", normalizedKeyword);
                    }
                    else if (searchMode == VideoSearchMode.OrderIdentifierContains)
                    {
                        whereSql += @" AND (
                            OrderId LIKE @keyword OR TrackingNumber LIKE @keyword OR SourceOrderId LIKE @keyword)";
                        countCmd.Parameters.AddWithValue("@keyword", $"%{normalizedKeyword}%");
                    }
                    else
                    {
                        whereSql += @" AND (
                            OrderId LIKE @keyword OR FilePath LIKE @keyword OR TrackingNumber LIKE @keyword
                            OR SourceOrderId LIKE @keyword OR BuyerMessage LIKE @keyword
                            OR SellerMemo LIKE @keyword OR ProductInfo LIKE @keyword
                            OR SourceDeviceName LIKE @keyword)";
                        countCmd.Parameters.AddWithValue("@keyword", $"%{normalizedKeyword}%");
                    }
                }

                countCmd.CommandText = "SELECT COUNT(1) " + whereSql + ";";
                if (startDate.HasValue)
                    countCmd.Parameters.AddWithValue("@startDate", startDate.Value.ToString("yyyy-MM-dd 00:00:00"));
                if (endDate.HasValue)
                    countCmd.Parameters.AddWithValue("@endDate", endDate.Value.AddDays(1).ToString("yyyy-MM-dd 00:00:00"));
                int total = Convert.ToInt32(countCmd.ExecuteScalar());

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, OrderId, Mode, VideoCodec, VideoEncoder, FilePath, FileSizeBytes,
                           StartTime, EndTime, DurationSeconds, StopReason,
                           IsDeleted, DeletedAt, DeleteReason,
                           TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo, OrderInfoPushTime, OrderInfoJson,
                           SourceType, SourceDeviceId, SourceDeviceName, SourceSessionId, ContentSha256,
                           StorageState, RemoteVideoRecordId, SourceDeviceKind "
                    + whereSql + @"
                    ORDER BY StartTime DESC, Id DESC
                    LIMIT @limit OFFSET @offset;";
                if (startDate.HasValue)
                    cmd.Parameters.AddWithValue("@startDate", startDate.Value.ToString("yyyy-MM-dd 00:00:00"));
                if (endDate.HasValue)
                    cmd.Parameters.AddWithValue("@endDate", endDate.Value.AddDays(1).ToString("yyyy-MM-dd 00:00:00"));
                cmd.Parameters.AddWithValue("@limit", pageSize);
                cmd.Parameters.AddWithValue("@offset", offset);
                if (normalizedKeyword.Length > 0)
                {
                    string keywordParameter = searchMode == VideoSearchMode.ExactOrderIdentifiers
                        ? normalizedKeyword
                        : $"%{normalizedKeyword}%";
                    cmd.Parameters.AddWithValue("@keyword", keywordParameter);
                }
                if (normalizedSourceType is "pc" or "external")
                    cmd.Parameters.AddWithValue("@sourceType", normalizedSourceType);
                if (normalizedSourceType == "external" && normalizedDeviceId.Length > 0)
                    cmd.Parameters.AddWithValue("@deviceId", normalizedDeviceId);
                else if (normalizedSourceType == "external" && normalizedSourceDeviceName.Length > 0)
                    cmd.Parameters.AddWithValue("@sourceDeviceName", normalizedSourceDeviceName);

                var records = new List<VideoRecord>(pageSize);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    records.Add(ReadVideoRecord(reader));

                return new PagedVideoResult { Total = total, Records = records };
            }
        }

        public IReadOnlyList<VideoSourceInfo> GetVideoSources()
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT SourceType, SourceDeviceId, MAX(SourceDeviceName), COUNT(1)
                    FROM VideoRecords
                    WHERE IsDeleted = 0
                    GROUP BY SourceType, SourceDeviceId
                    ORDER BY SourceType, MAX(SourceDeviceName), SourceDeviceId;";
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

        public int CountVideosForDevice(DateTime? startDate, DateTime? endDate, string keyword, string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return 0;

            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                string whereSql = @"
                    FROM VideoRecords
                    WHERE IsDeleted = 0 AND SourceDeviceId = @deviceId";
                cmd.Parameters.AddWithValue("@deviceId", deviceId);
                if (startDate.HasValue)
                {
                    whereSql += " AND StartTime >= @startDate";
                    cmd.Parameters.AddWithValue("@startDate", startDate.Value.ToString("yyyy-MM-dd 00:00:00"));
                }
                if (endDate.HasValue)
                {
                    whereSql += " AND StartTime < @endDate";
                    cmd.Parameters.AddWithValue("@endDate", endDate.Value.AddDays(1).ToString("yyyy-MM-dd 00:00:00"));
                }
                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    whereSql += @" AND (
                        OrderId LIKE @keyword OR FilePath LIKE @keyword OR TrackingNumber LIKE @keyword
                        OR SourceOrderId LIKE @keyword OR BuyerMessage LIKE @keyword
                        OR SellerMemo LIKE @keyword OR ProductInfo LIKE @keyword
                        OR SourceDeviceName LIKE @keyword)";
                    cmd.Parameters.AddWithValue("@keyword", $"%{keyword}%");
                }
                cmd.CommandText = "SELECT COUNT(1) " + whereSql + ";";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public IReadOnlyDictionary<long, VideoRecord> QueryVideoStatuses(IEnumerable<long> ids)
        {
            long[] normalized = ids.Distinct().Take(100).ToArray();
            if (normalized.Length == 0)
                return new Dictionary<long, VideoRecord>();

            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                string[] parameters = normalized.Select((_, index) => $"@id{index}").ToArray();
                for (int index = 0; index < normalized.Length; index++)
                    cmd.Parameters.AddWithValue(parameters[index], normalized[index]);
                cmd.CommandText = @"
                    SELECT Id, OrderId, Mode, VideoCodec, VideoEncoder, FilePath, FileSizeBytes,
                           StartTime, EndTime, DurationSeconds, StopReason,
                           IsDeleted, DeletedAt, DeleteReason,
                           TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo, OrderInfoPushTime, OrderInfoJson,
                           SourceType, SourceDeviceId, SourceDeviceName, SourceSessionId, ContentSha256,
                           StorageState, RemoteVideoRecordId, SourceDeviceKind
                    FROM VideoRecords
                    WHERE Id IN (" + string.Join(",", parameters) + ");";
                var result = new Dictionary<long, VideoRecord>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    VideoRecord record = ReadVideoRecord(reader);
                    result[record.Id] = record;
                }
                return result;
            }
        }

        public CursorVideoResult QueryVideosByCursor(DateTime? cursorStartTime, long? cursorId, string keyword, int limit)
        {
            limit = Math.Clamp(limit, 1, 50);
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                string whereSql = "WHERE IsDeleted = 0";
                if (cursorStartTime.HasValue && cursorId.HasValue)
                {
                    whereSql += " AND (StartTime < @cursorStartTime OR (StartTime = @cursorStartTime AND Id < @cursorId))";
                    cmd.Parameters.AddWithValue("@cursorStartTime", cursorStartTime.Value.ToString("yyyy-MM-dd HH:mm:ss"));
                    cmd.Parameters.AddWithValue("@cursorId", cursorId.Value);
                }

                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    whereSql += @" AND (
                        OrderId LIKE @keyword OR FilePath LIKE @keyword OR TrackingNumber LIKE @keyword
                        OR SourceOrderId LIKE @keyword OR BuyerMessage LIKE @keyword
                        OR SellerMemo LIKE @keyword OR ProductInfo LIKE @keyword
                        OR SourceDeviceName LIKE @keyword)";
                    cmd.Parameters.AddWithValue("@keyword", $"%{keyword.Trim()}%");
                }

                cmd.CommandText = @"
                    SELECT Id, OrderId, Mode, VideoCodec, VideoEncoder, FilePath, FileSizeBytes,
                           StartTime, EndTime, DurationSeconds, StopReason,
                           IsDeleted, DeletedAt, DeleteReason,
                           TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo, OrderInfoPushTime, OrderInfoJson,
                           SourceType, SourceDeviceId, SourceDeviceName, SourceSessionId, ContentSha256,
                           StorageState, RemoteVideoRecordId, SourceDeviceKind
                    FROM VideoRecords " + whereSql + @"
                    ORDER BY StartTime DESC, Id DESC
                    LIMIT @limit;";
                cmd.Parameters.AddWithValue("@limit", limit + 1);

                var records = new List<VideoRecord>(limit + 1);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    records.Add(ReadVideoRecord(reader));

                bool hasMore = records.Count > limit;
                if (hasMore)
                    records.RemoveAt(records.Count - 1);
                return new CursorVideoResult { Records = records, HasMore = hasMore };
            }
        }

        /// <summary>
        /// 获取每日统计数据（替代 daily_stats.json）
        /// </summary>
        public List<DailyStat> GetDailyStats(int days = 7)
        {
            return GetRangeStats(DateTime.Now.AddDays(-days + 1), DateTime.Now);
        }

        /// <summary>
        /// 增加对时间段范围聚合统计（支持文件大小）
        /// </summary>
        public List<DailyStat> GetRangeStats(DateTime start, DateTime end)
        {
            lock (_lock)
            {
                var results = new List<DailyStat>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT 
                        substr(StartTime, 1, 10) AS Date,
                        COUNT(*) AS TotalPieces,
                        SUM(DurationSeconds) AS TotalDurationSec,
                        SUM(CASE WHEN Id = (
                            SELECT MIN(v2.Id) FROM VideoRecords v2
                            WHERE v2.FilePath = VideoRecords.FilePath AND v2.IsDeleted = 0
                        ) THEN FileSizeBytes ELSE 0 END) AS TotalBytes
                    FROM VideoRecords
                    WHERE StartTime >= @start AND StartTime <= @end
                      AND IsDeleted = 0
                      AND EndTime IS NOT NULL
                    GROUP BY substr(StartTime, 1, 10)
                    ORDER BY Date ASC;";
                cmd.Parameters.AddWithValue("@start", start.ToString("yyyy-MM-dd 00:00:00"));
                cmd.Parameters.AddWithValue("@end", end.ToString("yyyy-MM-dd 23:59:59"));

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new DailyStat
                    {
                        Date = reader.GetString(0),
                        TotalPieces = reader.GetInt32(1),
                        TotalDurationSec = reader.GetDouble(2),
                        TotalBytes = reader.IsDBNull(3) ? 0 : reader.GetInt64(3)
                    });
                }
                return results;
            }
        }

        public List<DailyStat> GetAggregatedStats(DateTime start, DateTime end, string groupBy = "day", string sourceType = null)
        {
            lock (_lock)
            {
                var results = new List<DailyStat>();
                using var cmd = _connection.CreateCommand();

                string dateSelector = groupBy switch
                {
                    "week" => "strftime('%Y-W%W', v.StartTime)",
                    "month" => "strftime('%Y-%m', v.StartTime)",
                    _ => "substr(v.StartTime, 1, 10)"
                };

                cmd.CommandText = $@"
                    WITH CanonicalFiles AS (
                        SELECT FilePath, MIN(Id) AS CanonicalId
                        FROM VideoRecords
                        WHERE IsDeleted = 0
                          AND (@sourceType = '' OR SourceType = @sourceType)
                        GROUP BY FilePath
                    )
                    SELECT 
                        {dateSelector} AS GroupDate,
                        COUNT(*) AS TotalPieces,
                        SUM(v.DurationSeconds) AS TotalDurationSec,
                        SUM(CASE WHEN v.Id = c.CanonicalId THEN v.FileSizeBytes ELSE 0 END) AS TotalBytes
                    FROM VideoRecords v
                    LEFT JOIN CanonicalFiles c ON c.FilePath = v.FilePath
                    WHERE v.StartTime >= @start AND v.StartTime <= @end
                      AND v.IsDeleted = 0 AND v.EndTime IS NOT NULL
                      AND (@sourceType = '' OR v.SourceType = @sourceType)
                    GROUP BY GroupDate
                    ORDER BY GroupDate ASC;";

                cmd.Parameters.AddWithValue("@start", start.ToString("yyyy-MM-dd 00:00:00"));
                cmd.Parameters.AddWithValue("@end", end.ToString("yyyy-MM-dd 23:59:59"));
                cmd.Parameters.AddWithValue("@sourceType", sourceType?.Trim() ?? "");

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new DailyStat
                    {
                        Date = reader.GetString(0),
                        TotalPieces = reader.GetInt32(1),
                        TotalDurationSec = reader.GetDouble(2),
                        TotalBytes = reader.IsDBNull(3) ? 0 : reader.GetInt64(3)
                    });
                }
                return results;
            }
        }

        /// <summary>
        /// 获取所有未删除视频的总磁盘占用
        /// </summary>
        public long GetTotalFileSizeBytes()
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT COALESCE(SUM(FileSizeBytes), 0)
                    FROM (
                        SELECT MAX(FileSizeBytes) AS FileSizeBytes
                        FROM VideoRecords
                        WHERE IsDeleted = 0
                        GROUP BY FilePath
                    );";
                return (long)cmd.ExecuteScalar();
            }
        }

        /// <summary>
        /// 获取所有未删除视频的总大小和总时长（用于估算可录制时长）
        /// </summary>
        public (long TotalBytes, double TotalDurationSec) GetGlobalSizeAndDuration()
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT
                        (SELECT COALESCE(SUM(FileSizeBytes), 0)
                         FROM (
                             SELECT MAX(FileSizeBytes) AS FileSizeBytes
                             FROM VideoRecords
                             WHERE IsDeleted = 0 AND DurationSeconds > 0 AND EndTime IS NOT NULL
                             GROUP BY FilePath
                         )),
                        COALESCE(SUM(DurationSeconds), 0)
                    FROM VideoRecords
                    WHERE IsDeleted = 0 AND DurationSeconds > 0 AND EndTime IS NOT NULL;";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                    return (reader.GetInt64(0), reader.GetDouble(1));
                return (0, 0);
            }
        }

        /// <summary>
        /// 按时间升序获取最旧的未删除视频（用于磁盘清理）
        /// </summary>
        public List<VideoRecord> GetOldestVideos(int limit = 100)
        {
            lock (_lock)
            {
                var results = new List<VideoRecord>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT MIN(Id), MIN(OrderId), FilePath, MAX(FileSizeBytes), MIN(StartTime)
                    FROM VideoRecords 
                    WHERE IsDeleted = 0
                    GROUP BY FilePath
                    ORDER BY MIN(StartTime) ASC
                    LIMIT @limit;";
                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new VideoRecord
                    {
                        Id = reader.GetInt64(0),
                        OrderId = reader.GetString(1),
                        FilePath = reader.GetString(2),
                        FileSizeBytes = reader.GetInt64(3),
                        StartTime = DateTime.Parse(reader.GetString(4))
                    });
                }
                return results;
            }
        }

        /// <summary>
        /// 获取删除日志
        /// </summary>
        public List<DeleteLogEntry> GetDeleteLogs(int limit = 100)
        {
            lock (_lock)
            {
                var results = new List<DeleteLogEntry>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, FilePath, OrderId, FileSizeBytes, DeletedAt, Reason
                    FROM DeleteLogs ORDER BY DeletedAt DESC LIMIT @limit;";
                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new DeleteLogEntry
                    {
                        Id = reader.GetInt64(0),
                        FilePath = reader.GetString(1),
                        OrderId = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        FileSizeBytes = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                        DeletedAt = DateTime.Parse(reader.GetString(4)),
                        Reason = reader.IsDBNull(5) ? "" : reader.GetString(5)
                    });
                }
                return results;
            }
        }

        public List<StorageVideoFile> GetActiveStorageVideoFiles()
        {
            lock (_lock)
            {
                var results = new List<StorageVideoFile>();
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT FilePath, MAX(FileSizeBytes), MIN(StartTime)
                    FROM VideoRecords
                    WHERE IsDeleted = 0
                      AND EndTime IS NOT NULL
                    GROUP BY FilePath
                    ORDER BY MIN(StartTime) ASC;";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new StorageVideoFile
                    {
                        FilePath = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        FileSizeBytes = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                        StartTime = DateTime.Parse(reader.GetString(2))
                    });
                }
                return results;
            }
        }

        private string GetVideoFilePath(long recordId)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT FilePath FROM VideoRecords WHERE Id = @id LIMIT 1;";
            cmd.Parameters.AddWithValue("@id", recordId);
            return cmd.ExecuteScalar() as string ?? "";
        }

        private void UpsertLocalVideoFileCore(
            string filePath,
            long fileSizeBytes,
            SqliteTransaction transaction = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT INTO LocalVideoFileInventory (FilePath, FileSizeBytes, UpdatedAt)
                VALUES (@filePath, @fileSize, @updatedAt)
                ON CONFLICT(FilePath) DO UPDATE SET
                    FileSizeBytes = excluded.FileSizeBytes,
                    UpdatedAt = excluded.UpdatedAt;";
            cmd.Parameters.AddWithValue("@filePath", Path.GetFullPath(filePath));
            cmd.Parameters.AddWithValue("@fileSize", Math.Max(0, fileSizeBytes));
            cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        private void RemoveLocalVideoFileCore(
            string filePath,
            SqliteTransaction transaction = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;
            string normalizedPath;
            try { normalizedPath = Path.GetFullPath(filePath); }
            catch { return; }
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM LocalVideoFileInventory WHERE FilePath = @filePath;";
            cmd.Parameters.AddWithValue("@filePath", normalizedPath);
            cmd.ExecuteNonQuery();
        }

        private static bool IsPathInside(string filePath, string rootPath)
        {
            string root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(filePath);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }


        private static void AddOrderInfoParameters(SqliteCommand cmd, OrderInfo item)
        {
            cmd.Parameters.AddWithValue("@trackingNumber", item.TrackingNumber?.Trim().ToUpperInvariant() ?? "");
            cmd.Parameters.AddWithValue("@sourceOrderId", item.OrderId ?? "");
            cmd.Parameters.AddWithValue("@buyerMessage", item.BuyerMessage ?? "");
            cmd.Parameters.AddWithValue("@sellerMemo", item.SellerMemo ?? "");
            cmd.Parameters.AddWithValue("@productInfo", item.ProductInfo ?? "");
            cmd.Parameters.AddWithValue("@pushTime", item.PushTime.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@orderInfoJson", SerializeOrderInfo(item));
        }

        private static string SerializeOrderInfo(OrderInfo item)
        {
            if (item == null) return "";
            try
            {
                return JsonSerializer.Serialize(item, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            }
            catch
            {
                return "";
            }
        }

        private static string FirstNotEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return "";
        }

        private void BackupBeforeSchemaMigrationIfNeeded(bool databaseExisted)
        {
            if (!databaseExisted) return;
            if (!TableExists("VideoRecords")) return;

            var columns = GetTableColumns("VideoRecords");
            string[] requiredColumns =
            {
                "VideoCodec",
                "VideoEncoder",
                "TrackingNumber",
                "SourceOrderId",
                "BuyerMessage",
                "SellerMemo",
                "ProductInfo",
                "OrderInfoPushTime",
                "OrderInfoJson"
            };

            if (requiredColumns.All(columns.Contains) && !columns.Contains("FileName"))
                return;

            ExecuteNonQuery("PRAGMA wal_checkpoint(FULL);");

            string backupDir = CreateSchemaMigrationBackupDirectory();
            string destinationPrefix = Path.Combine(backupDir, "videos-before-schema-migration");
            CopySqliteFileIfExists(_dbPath, destinationPrefix + ".db");
            CopySqliteFileIfExists(_dbPath + "-wal", destinationPrefix + ".db-wal");
            CopySqliteFileIfExists(_dbPath + "-shm", destinationPrefix + ".db-shm");
        }

        private void DropRedundantFileNameColumn()
        {
            if (!GetTableColumns("VideoRecords").Contains("FileName")) return;
            ExecuteNonQuery("ALTER TABLE VideoRecords DROP COLUMN FileName;");
        }

        private bool TableExists(string tableName)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name=$name;";
            cmd.Parameters.AddWithValue("$name", tableName);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        private HashSet<string> GetTableColumns(string tableName)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info('{tableName.Replace("'", "''")}');";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(reader.GetString(1));
            return result;
        }

        private static string CreateSchemaMigrationBackupDirectory()
        {
            Directory.CreateDirectory(AppPaths.BackupsDir);
            string baseName = $"schema-migration-videos-db-{DateTime.Now:yyyyMMdd-HHmmss}";
            string dir = Path.Combine(AppPaths.BackupsDir, baseName);
            int suffix = 1;
            while (Directory.Exists(dir))
            {
                suffix++;
                dir = Path.Combine(AppPaths.BackupsDir, $"{baseName}-{suffix}");
            }
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void CopySqliteFileIfExists(string sourcePath, string destinationPath)
        {
            if (!File.Exists(sourcePath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }


        private void ExecuteNonQuery(string sql)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private void EnsureColumnExists(string tableName, string columnName, string columnDefinition)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            ExecuteNonQuery($"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};");
        }

        public void Dispose()
        {
            try { _connection?.Close(); _connection?.Dispose(); } catch { }
        }
    }

    public sealed record MobileBackupDailyCount(
        string DeviceId,
        string DeviceName,
        int VideoCount,
        string DeviceKind = "");

    public sealed record MobileBackupOverview(
        IReadOnlyList<MobileBackupDailyCount> DeviceCounts,
        int TodayCount,
        int TotalCount);

    public sealed record VideoSourceInfo(
        string SourceType,
        string DeviceId,
        string DeviceName,
        int VideoCount);
}
