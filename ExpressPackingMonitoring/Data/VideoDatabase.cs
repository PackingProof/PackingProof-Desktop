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
        public string VideoCodec { get; set; } = "";
        public string VideoEncoder { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
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
                    FilePath TEXT NOT NULL,
                    FileName TEXT NOT NULL,
                    FileSizeBytes INTEGER DEFAULT 0,
                    StartTime TEXT NOT NULL,
                    EndTime TEXT,
                    DurationSeconds REAL DEFAULT 0,
                    StopReason TEXT DEFAULT '',
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

            EnsureColumnExists("VideoRecords", "VideoCodec", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "VideoEncoder", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "TrackingNumber", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "SourceOrderId", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "BuyerMessage", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "SellerMemo", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "ProductInfo", "TEXT DEFAULT ''");
            EnsureColumnExists("VideoRecords", "OrderInfoPushTime", "TEXT");
            EnsureColumnExists("VideoRecords", "OrderInfoJson", "TEXT DEFAULT ''");

            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_orderid ON VideoRecords(OrderId);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_starttime ON VideoRecords(StartTime);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_filepath ON VideoRecords(FilePath);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_isdeleted ON VideoRecords(IsDeleted);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_active_starttime ON VideoRecords(IsDeleted, StartTime DESC);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_tracking ON VideoRecords(TrackingNumber);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_video_source_order ON VideoRecords(SourceOrderId);");
            ExecuteNonQuery("CREATE INDEX IF NOT EXISTS idx_orderinfo_source_order ON OrderInfoRecords(SourceOrderId);");
        }

        /// <summary>
        /// 录制开始时插入记录，返回记录 ID
        /// </summary>
        public long InsertVideoRecord(string orderId, string mode, string videoCodec, string videoEncoder, string filePath, DateTime startTime, OrderInfo orderInfo = null)
        {
            string orderInfoJson = SerializeOrderInfo(orderInfo);
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO VideoRecords (
                        OrderId, Mode, TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo,
                        OrderInfoPushTime, OrderInfoJson, VideoCodec, VideoEncoder, FilePath, FileName, StartTime)
                    VALUES (
                        @orderId, @mode, @trackingNumber, @sourceOrderId, @buyerMessage, @sellerMemo, @productInfo,
                        @orderInfoPushTime, @orderInfoJson, @videoCodec, @videoEncoder, @filePath, @fileName, @startTime);
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
                cmd.Parameters.AddWithValue("@videoCodec", videoCodec ?? "");
                cmd.Parameters.AddWithValue("@videoEncoder", videoEncoder ?? "");
                cmd.Parameters.AddWithValue("@filePath", filePath ?? "");
                cmd.Parameters.AddWithValue("@fileName", Path.GetFileName(filePath ?? ""));
                cmd.Parameters.AddWithValue("@startTime", startTime.ToString("yyyy-MM-dd HH:mm:ss"));
                return (long)cmd.ExecuteScalar();
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
                            UpdatedAt = excluded.UpdatedAt;";
                    AddOrderInfoParameters(cmd, item);
                    cmd.Parameters.AddWithValue("@now", now);
                    cmd.ExecuteNonQuery();
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
                          AND StartTime >= @since
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
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    UPDATE VideoRecords SET 
                        FilePath = @newPath,
                        FileName = @newFileName
                    WHERE FilePath = @oldPath;";
                cmd.Parameters.AddWithValue("@oldPath", oldPath);
                cmd.Parameters.AddWithValue("@newPath", newPath);
                cmd.Parameters.AddWithValue("@newFileName", Path.GetFileName(newPath));
                cmd.ExecuteNonQuery();
            }
        }

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

                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                }
            }
        }

        /// <summary>
        /// 检查最近72小时内是否已存在指定单号的未删除录像记录（含录制中的记录）
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
                cmd.Parameters.AddWithValue("@since", DateTime.Now.AddHours(-72).ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@excludeId", excludeRecordId);
                return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
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
                    SELECT Id, OrderId, Mode, VideoCodec, VideoEncoder, FilePath, FileName, FileSizeBytes, 
                           StartTime, EndTime, DurationSeconds, StopReason,
                           IsDeleted, DeletedAt, DeleteReason,
                           TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo, OrderInfoPushTime, OrderInfoJson
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
                        FileName = reader.GetString(6),
                        FileSizeBytes = reader.GetInt64(7),
                        StartTime = DateTime.Parse(reader.GetString(8)),
                        EndTime = reader.IsDBNull(9) ? DateTime.MinValue : DateTime.Parse(reader.GetString(9)),
                        DurationSeconds = reader.GetDouble(10),
                        StopReason = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        IsDeleted = reader.GetInt64(12) == 1,
                        DeletedAt = reader.IsDBNull(13) ? null : DateTime.Parse(reader.GetString(13)),
                        DeleteReason = reader.IsDBNull(14) ? "" : reader.GetString(14),
                        TrackingNumber = reader.IsDBNull(15) ? "" : reader.GetString(15),
                        SourceOrderId = reader.IsDBNull(16) ? "" : reader.GetString(16),
                        BuyerMessage = reader.IsDBNull(17) ? "" : reader.GetString(17),
                        SellerMemo = reader.IsDBNull(18) ? "" : reader.GetString(18),
                        ProductInfo = reader.IsDBNull(19) ? "" : reader.GetString(19),
                        OrderInfoPushTime = reader.IsDBNull(20) ? null : DateTime.Parse(reader.GetString(20)),
                        OrderInfoJson = reader.IsDBNull(21) ? "" : reader.GetString(21)
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
                      SELECT Id, OrderId, Mode, VideoCodec, VideoEncoder, FilePath, FileName, FileSizeBytes, 
                          StartTime, EndTime, DurationSeconds, StopReason,
                           IsDeleted, DeletedAt, DeleteReason,
                           TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo, OrderInfoPushTime, OrderInfoJson
                    FROM VideoRecords 
                    WHERE 1 = 1";

                if (startDate.HasValue)
                    sql += " AND StartTime >= @startDate";

                if (endDate.HasValue)
                    sql += " AND StartTime < @endDate";

                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    sql += @" AND (
                        OrderId LIKE @keyword OR FileName LIKE @keyword OR TrackingNumber LIKE @keyword
                        OR SourceOrderId LIKE @keyword OR BuyerMessage LIKE @keyword
                        OR SellerMemo LIKE @keyword OR ProductInfo LIKE @keyword)";
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
                        FileName = reader.GetString(6),
                        FileSizeBytes = reader.GetInt64(7),
                        StartTime = DateTime.Parse(reader.GetString(8)),
                        EndTime = reader.IsDBNull(9) ? DateTime.MinValue : DateTime.Parse(reader.GetString(9)),
                        DurationSeconds = reader.GetDouble(10),
                        StopReason = reader.IsDBNull(11) ? "" : reader.GetString(11),
                        IsDeleted = reader.GetInt64(12) == 1,
                        DeletedAt = reader.IsDBNull(13) ? null : DateTime.Parse(reader.GetString(13)),
                        DeleteReason = reader.IsDBNull(14) ? "" : reader.GetString(14),
                        TrackingNumber = reader.IsDBNull(15) ? "" : reader.GetString(15),
                        SourceOrderId = reader.IsDBNull(16) ? "" : reader.GetString(16),
                        BuyerMessage = reader.IsDBNull(17) ? "" : reader.GetString(17),
                        SellerMemo = reader.IsDBNull(18) ? "" : reader.GetString(18),
                        ProductInfo = reader.IsDBNull(19) ? "" : reader.GetString(19),
                        OrderInfoPushTime = reader.IsDBNull(20) ? null : DateTime.Parse(reader.GetString(20)),
                        OrderInfoJson = reader.IsDBNull(21) ? "" : reader.GetString(21)
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
                FileName = reader.GetString(6),
                FileSizeBytes = reader.GetInt64(7),
                StartTime = DateTime.Parse(reader.GetString(8)),
                EndTime = reader.IsDBNull(9) ? DateTime.MinValue : DateTime.Parse(reader.GetString(9)),
                DurationSeconds = reader.GetDouble(10),
                StopReason = reader.IsDBNull(11) ? "" : reader.GetString(11),
                IsDeleted = reader.GetInt64(12) == 1,
                DeletedAt = reader.IsDBNull(13) ? null : DateTime.Parse(reader.GetString(13)),
                DeleteReason = reader.IsDBNull(14) ? "" : reader.GetString(14),
                TrackingNumber = reader.IsDBNull(15) ? "" : reader.GetString(15),
                SourceOrderId = reader.IsDBNull(16) ? "" : reader.GetString(16),
                BuyerMessage = reader.IsDBNull(17) ? "" : reader.GetString(17),
                SellerMemo = reader.IsDBNull(18) ? "" : reader.GetString(18),
                ProductInfo = reader.IsDBNull(19) ? "" : reader.GetString(19),
                OrderInfoPushTime = reader.IsDBNull(20) ? null : DateTime.Parse(reader.GetString(20)),
                OrderInfoJson = reader.IsDBNull(21) ? "" : reader.GetString(21)
            };
        }

        public PagedVideoResult QueryVideosPaged(DateTime? startDate, DateTime? endDate, string keyword, int page, int pageSize, bool includeDeleted = false)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            int offset = (page - 1) * pageSize;

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

                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    whereSql += @" AND (
                        OrderId LIKE @keyword OR FileName LIKE @keyword OR TrackingNumber LIKE @keyword
                        OR SourceOrderId LIKE @keyword OR BuyerMessage LIKE @keyword
                        OR SellerMemo LIKE @keyword OR ProductInfo LIKE @keyword)";
                    countCmd.Parameters.AddWithValue("@keyword", $"%{keyword}%");
                }

                countCmd.CommandText = "SELECT COUNT(1) " + whereSql + ";";
                if (startDate.HasValue)
                    countCmd.Parameters.AddWithValue("@startDate", startDate.Value.ToString("yyyy-MM-dd 00:00:00"));
                if (endDate.HasValue)
                    countCmd.Parameters.AddWithValue("@endDate", endDate.Value.AddDays(1).ToString("yyyy-MM-dd 00:00:00"));
                int total = Convert.ToInt32(countCmd.ExecuteScalar());

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, OrderId, Mode, VideoCodec, VideoEncoder, FilePath, FileName, FileSizeBytes,
                           StartTime, EndTime, DurationSeconds, StopReason,
                           IsDeleted, DeletedAt, DeleteReason,
                           TrackingNumber, SourceOrderId, BuyerMessage, SellerMemo, ProductInfo, OrderInfoPushTime, OrderInfoJson "
                    + whereSql + @"
                    ORDER BY StartTime DESC
                    LIMIT @limit OFFSET @offset;";
                if (startDate.HasValue)
                    cmd.Parameters.AddWithValue("@startDate", startDate.Value.ToString("yyyy-MM-dd 00:00:00"));
                if (endDate.HasValue)
                    cmd.Parameters.AddWithValue("@endDate", endDate.Value.AddDays(1).ToString("yyyy-MM-dd 00:00:00"));
                cmd.Parameters.AddWithValue("@limit", pageSize);
                cmd.Parameters.AddWithValue("@offset", offset);
                if (!string.IsNullOrWhiteSpace(keyword))
                    cmd.Parameters.AddWithValue("@keyword", $"%{keyword}%");

                var records = new List<VideoRecord>(pageSize);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    records.Add(ReadVideoRecord(reader));

                return new PagedVideoResult { Total = total, Records = records };
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
                        SUM(FileSizeBytes) AS TotalBytes
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

        public List<DailyStat> GetAggregatedStats(DateTime start, DateTime end, string groupBy = "day")
        {
            lock (_lock)
            {
                var results = new List<DailyStat>();
                using var cmd = _connection.CreateCommand();

                string dateSelector = groupBy switch
                {
                    "week" => "strftime('%Y-W%W', StartTime)",
                    "month" => "strftime('%Y-%m', StartTime)",
                    _ => "substr(StartTime, 1, 10)"
                };

                cmd.CommandText = $@"
                    SELECT 
                        {dateSelector} AS GroupDate,
                        COUNT(*) AS TotalPieces,
                        SUM(DurationSeconds) AS TotalDurationSec,
                        SUM(FileSizeBytes) AS TotalBytes
                    FROM VideoRecords 
                    WHERE StartTime >= @start AND StartTime <= @end
                      AND IsDeleted = 0 AND EndTime IS NOT NULL
                    GROUP BY GroupDate
                    ORDER BY GroupDate ASC;";

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

        /// <summary>
        /// 获取所有未删除视频的总磁盘占用
        /// </summary>
        public long GetTotalFileSizeBytes()
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(SUM(FileSizeBytes), 0) FROM VideoRecords WHERE IsDeleted = 0;";
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
                cmd.CommandText = @"SELECT COALESCE(SUM(FileSizeBytes), 0), COALESCE(SUM(DurationSeconds), 0) 
                                    FROM VideoRecords WHERE IsDeleted = 0 AND DurationSeconds > 0 AND EndTime IS NOT NULL;";
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
                    SELECT Id, OrderId, FilePath, FileSizeBytes, StartTime
                    FROM VideoRecords 
                    WHERE IsDeleted = 0 
                    ORDER BY StartTime ASC 
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
                    SELECT FilePath, FileSizeBytes, StartTime
                    FROM VideoRecords
                    WHERE IsDeleted = 0
                      AND EndTime IS NOT NULL
                    ORDER BY StartTime ASC;";

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

            if (requiredColumns.All(columns.Contains))
                return;

            ExecuteNonQuery("PRAGMA wal_checkpoint(FULL);");

            string backupDir = CreateSchemaMigrationBackupDirectory();
            string destinationPrefix = Path.Combine(backupDir, "videos-before-schema-migration");
            CopySqliteFileIfExists(_dbPath, destinationPrefix + ".db");
            CopySqliteFileIfExists(_dbPath + "-wal", destinationPrefix + ".db-wal");
            CopySqliteFileIfExists(_dbPath + "-shm", destinationPrefix + ".db-shm");
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
}
