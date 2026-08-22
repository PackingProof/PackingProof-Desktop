#nullable disable
using ExpressPackingMonitoring.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace ExpressPackingMonitoring.Config
{
    internal static class AppPaths
    {
        public static readonly string UserDataDir = Path.Combine(GetLocalAppDataRoot(), "ExpressPackingMonitoring");

        public static readonly string LogDir = Path.Combine(UserDataDir, "log");
        public static readonly string CacheDir = Path.Combine(UserDataDir, "cache");
        public static readonly string BackupsDir = Path.Combine(UserDataDir, "backups");
        // 手机/电脑备份与配对的持久化状态目录：不得放在 cache 中，避免被缓存清理或上传状态清理误删。
        public static readonly string MobileBackupStateDir = Path.Combine(UserDataDir, "mobile-backup-state");
        public static readonly string ClipPreviewDir = Path.Combine(CacheDir, "clip_previews");
        public static readonly string ClipsDir = Path.Combine(CacheDir, "clips");
        public static readonly string TranscodeCacheDir = Path.Combine(CacheDir, "transcode");
        public static readonly string TtsCacheDir = Path.Combine(CacheDir, "tts");
        public static readonly string GuideCacheDir = Path.Combine(CacheDir, "guide");
        public static readonly string FeedbackCacheDir = Path.Combine(CacheDir, "feedback");
        public static readonly string UserscriptsDir = Path.Combine(UserDataDir, "userscripts");

        public static readonly string ConfigPath = Path.Combine(UserDataDir, "config.json");
        public static readonly string VideoDatabasePath = Path.Combine(UserDataDir, "videos.db");
        public static readonly string WebDebugLogPath = Path.Combine(LogDir, "web_debug.log");
        public static readonly string EncoderDetectLogPath = Path.Combine(LogDir, "encoder_detect.log");
        public static readonly string RuntimeLogPath = Path.Combine(LogDir, "runtime.log");
        // 仅用于把旧版本 JSON 缓存一次性迁移到 SQLite；新版本不再写入此文件。
        public static readonly string OrderInfoCachePath = Path.Combine(CacheDir, "orderinfo_cache.json");
        public static readonly string UpdateCheckCachePath = Path.Combine(CacheDir, "update_check_cache.json");
        public static readonly string UpdatesCacheDir = Path.Combine(CacheDir, "updates");
        public static readonly string MobileAppUpdateCachePath = Path.Combine(CacheDir, "mobile_app_update_cache.json");

        static AppPaths()
        {
            EnsureUserDataDirectories();
            MigrateLegacyRuntimeData();
            MigrateMobileBackupState();
        }

        public static void EnsureUserDataDirectories()
        {
            Directory.CreateDirectory(UserDataDir);
            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(CacheDir);
            Directory.CreateDirectory(BackupsDir);
            Directory.CreateDirectory(MobileBackupStateDir);
            Directory.CreateDirectory(ClipPreviewDir);
            Directory.CreateDirectory(ClipsDir);
            Directory.CreateDirectory(TranscodeCacheDir);
            Directory.CreateDirectory(TtsCacheDir);
            Directory.CreateDirectory(GuideCacheDir);
            Directory.CreateDirectory(FeedbackCacheDir);
            Directory.CreateDirectory(UserscriptsDir);
        }

        private static void MigrateMobileBackupState()
        {
            // 旧版本把备份状态放在 cache\mobile-backup；持久化状态与上传分块统一迁移到持久状态目录。
            MigrateMobileBackupState(Path.Combine(CacheDir, "mobile-backup"), MobileBackupStateDir);
        }

        internal static void MigrateMobileBackupState(string legacyDirectory, string destinationDirectory)
        {
            MoveDirectoryContents(legacyDirectory, destinationDirectory);
        }

        private static string GetLocalAppDataRoot()
        {
            string overridePath = Environment.GetEnvironmentVariable("EPM_USER_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overridePath))
                return Path.GetFullPath(overridePath);

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localAppData)
                ? AppDomain.CurrentDomain.BaseDirectory
                : localAppData;
        }

        public static string FindFFmpeg()
        {
            return FindFFmpeg(
                AppDomain.CurrentDomain.BaseDirectory,
                Environment.GetEnvironmentVariable("PATH"));
        }

        internal static string FindFFmpeg(string baseDir, string pathEnvironment)
        {
            string toolsPath = Path.Combine(baseDir, "tools", "ffmpeg.exe");
            if (File.Exists(toolsPath)) return toolsPath;

            string legacyPath = Path.Combine(baseDir, "ffmpeg.exe");
            if (File.Exists(legacyPath)) return legacyPath;

            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                string projectPath = Path.Combine(dir.FullName, "ffmpeg.exe");
                if (File.Exists(projectPath)) return projectPath;
            }

            if (!string.IsNullOrWhiteSpace(pathEnvironment))
            {
                foreach (string rawDirectory in pathEnvironment.Split(
                             Path.PathSeparator,
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    string directory = rawDirectory.Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(directory))
                        continue;

                    try
                    {
                        string pathFfmpeg = Path.Combine(directory, "ffmpeg.exe");
                        if (File.Exists(pathFfmpeg))
                            return Path.GetFullPath(pathFfmpeg);
                    }
                    catch
                    {
                        // PATH 中的无效条目不应影响其它候选路径。
                    }
                }
            }

            return null;
        }

        private static void MigrateLegacyRuntimeData()
        {
            MoveDirectoryContents(Path.Combine(LogDir, "guide"), GuideCacheDir);
            MigrateFeedbackCache(Path.Combine(BackupsDir, "feedback"), FeedbackCacheDir);

            foreach (string legacyRoot in GetLegacyRuntimeRoots())
            {
                MoveFileIfDestinationMissing(Path.Combine(legacyRoot, "config.json"), ConfigPath);
                MigrateLegacyVideoDatabase(legacyRoot);
                MoveFileIfDestinationMissing(Path.Combine(legacyRoot, "orderinfo_cache.json"), OrderInfoCachePath);
                MoveFileIfDestinationMissing(Path.Combine(legacyRoot, "web_debug.log"), WebDebugLogPath);
                MoveFileIfDestinationMissing(Path.Combine(legacyRoot, "encoder_detect.log"), EncoderDetectLogPath);

                MoveDirectoryContents(Path.Combine(legacyRoot, "transcache"), TranscodeCacheDir);
                MoveLegacyTtsCacheDirectory(Path.Combine(legacyRoot, "tts_cache"));
                MoveLegacyTtsCacheDirectory(Path.Combine(legacyRoot, "tts"));
            }
        }

        internal static void MigrateFeedbackCache(string legacyDirectory, string destinationDirectory)
        {
            MoveDirectoryContents(legacyDirectory, destinationDirectory);
        }

        private static void MigrateLegacyVideoDatabase(string legacyRoot)
        {
            string legacyDbPath = Path.Combine(legacyRoot, "videos.db");
            try
            {
                if (!File.Exists(legacyDbPath)) return;
                if (string.Equals(Path.GetFullPath(legacyDbPath), Path.GetFullPath(VideoDatabasePath), StringComparison.OrdinalIgnoreCase)) return;

                if (!File.Exists(VideoDatabasePath))
                {
                    MoveFileIfDestinationMissing(legacyDbPath, VideoDatabasePath);
                    MoveFileIfDestinationMissing(legacyDbPath + "-wal", VideoDatabasePath + "-wal");
                    MoveFileIfDestinationMissing(legacyDbPath + "-shm", VideoDatabasePath + "-shm");
                    return;
                }

                string backupDir = CreateBackupDirectory("legacy-videos-db");
                CopySqliteFileSet(VideoDatabasePath, Path.Combine(backupDir, "appdata-before-merge"));
                CopySqliteFileSet(legacyDbPath, Path.Combine(backupDir, "legacy-before-merge"));

                int inserted = MergeVideoRecords(legacyDbPath, VideoDatabasePath);
                WriteMigrationMarker(backupDir, legacyRoot, inserted);

                MoveSqliteFileSetToDirectory(legacyDbPath, backupDir, "legacy");
            }
            catch
            {
                // 迁移失败不能阻止主程序启动；旧库保留在原位置，后续启动仍可重试。
            }
        }

        private static int MergeVideoRecords(string legacyDbPath, string destinationDbPath)
        {
            using var connection = new SqliteConnection($"Data Source={destinationDbPath}");
            connection.Open();

            ExecuteSql(connection, "PRAGMA busy_timeout=5000;");
            EnsureVideoMergeColumns(connection, "main");
            if (!TableExists(connection, "main", "VideoRecords"))
                throw new InvalidDataException("Destination video database has no VideoRecords table.");

            string escapedLegacyPath = legacyDbPath.Replace("'", "''");
            ExecuteSql(connection, $"ATTACH DATABASE '{escapedLegacyPath}' AS legacy;");
            try
            {
                if (!TableExists(connection, "legacy", "VideoRecords"))
                    return 0;

                EnsureVideoMergeColumns(connection, "legacy");
                var mergeColumns = VideoMergeColumns.ToList();
                bool destinationNeedsLegacyFileName = GetTableColumns(connection, "main", "VideoRecords").Contains("FileName");
                string sourceColumns = string.Join(", ", mergeColumns.Select(c => $"s.{c}"));
                string targetColumns = string.Join(", ", mergeColumns);
                if (destinationNeedsLegacyFileName)
                {
                    // 旧结构要求 FileName 非空；合并完成后 VideoDatabase 会删除此冗余列。
                    targetColumns += ", FileName";
                    sourceColumns += ", s.FilePath";
                }

                using var cmd = connection.CreateCommand();
                cmd.CommandText = $@"
                    INSERT INTO main.VideoRecords ({targetColumns})
                    SELECT {sourceColumns}
                    FROM legacy.VideoRecords s
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM main.VideoRecords d
                        WHERE d.FilePath = s.FilePath
                    );";
                return cmd.ExecuteNonQuery();
            }
            finally
            {
                ExecuteSql(connection, "DETACH DATABASE legacy;");
            }
        }

        private static readonly string[] VideoMergeColumns =
        {
            "OrderId",
            "Mode",
            "TrackingNumber",
            "SourceOrderId",
            "BuyerMessage",
            "SellerMemo",
            "ProductInfo",
            "OrderInfoPushTime",
            "OrderInfoJson",
            "VideoCodec",
            "VideoEncoder",
            "FilePath",
            "FileSizeBytes",
            "StartTime",
            "EndTime",
            "DurationSeconds",
            "StopReason",
            "IsDeleted",
            "DeletedAt",
            "DeleteReason"
        };

        private static void EnsureVideoMergeColumns(SqliteConnection connection, string schema)
        {
            if (!TableExists(connection, schema, "VideoRecords"))
                return;

            var columns = GetTableColumns(connection, schema, "VideoRecords");
            var definitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TrackingNumber"] = "TEXT DEFAULT ''",
                ["SourceOrderId"] = "TEXT DEFAULT ''",
                ["BuyerMessage"] = "TEXT DEFAULT ''",
                ["SellerMemo"] = "TEXT DEFAULT ''",
                ["ProductInfo"] = "TEXT DEFAULT ''",
                ["OrderInfoPushTime"] = "TEXT",
                ["OrderInfoJson"] = "TEXT DEFAULT ''",
                ["VideoCodec"] = "TEXT DEFAULT ''",
                ["VideoEncoder"] = "TEXT DEFAULT ''",
                ["IsDeleted"] = "INTEGER DEFAULT 0",
                ["DeletedAt"] = "TEXT",
                ["DeleteReason"] = "TEXT DEFAULT ''"
            };

            foreach (var definition in definitions)
            {
                if (columns.Contains(definition.Key)) continue;
                ExecuteSql(connection, $"ALTER TABLE {schema}.VideoRecords ADD COLUMN {definition.Key} {definition.Value};");
            }
        }

        private static bool TableExists(SqliteConnection connection, string schema, string tableName)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(1) FROM {schema}.sqlite_master WHERE type='table' AND name=$name;";
            cmd.Parameters.AddWithValue("$name", tableName);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        private static HashSet<string> GetTableColumns(SqliteConnection connection, string schema, string tableName)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA {schema}.table_info('{tableName.Replace("'", "''")}');";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(reader.GetString(1));
            return result;
        }

        private static void ExecuteSql(SqliteConnection connection, string sql)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private static string CreateBackupDirectory(string prefix)
        {
            string baseName = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}";
            string dir = Path.Combine(BackupsDir, baseName);
            int suffix = 1;
            while (Directory.Exists(dir))
            {
                suffix++;
                dir = Path.Combine(BackupsDir, $"{baseName}-{suffix}");
            }
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void CopySqliteFileSet(string dbPath, string destinationPrefix)
        {
            CopyFileIfExists(dbPath, destinationPrefix + ".db");
            CopyFileIfExists(dbPath + "-wal", destinationPrefix + ".db-wal");
            CopyFileIfExists(dbPath + "-shm", destinationPrefix + ".db-shm");
        }

        private static void MoveSqliteFileSetToDirectory(string dbPath, string destinationDir, string prefix)
        {
            MoveFileIfExists(dbPath, Path.Combine(destinationDir, prefix + ".db"));
            MoveFileIfExists(dbPath + "-wal", Path.Combine(destinationDir, prefix + ".db-wal"));
            MoveFileIfExists(dbPath + "-shm", Path.Combine(destinationDir, prefix + ".db-shm"));
        }

        private static void CopyFileIfExists(string sourcePath, string destinationPath)
        {
            if (!File.Exists(sourcePath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        private static void MoveFileIfExists(string sourcePath, string destinationPath)
        {
            if (!File.Exists(sourcePath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(sourcePath, destinationPath);
        }

        private static void WriteMigrationMarker(string backupDir, string legacyRoot, int inserted)
        {
            string markerPath = Path.Combine(backupDir, "merge-summary.txt");
            File.WriteAllText(markerPath,
                $"LegacyRoot={legacyRoot}{Environment.NewLine}InsertedVideoRecords={inserted}{Environment.NewLine}MergedAt={DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
        }

        private static IEnumerable<string> GetLegacyRuntimeRoots()
        {
            string baseDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            yield return baseDir;

            var dir = new DirectoryInfo(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (dir.Parent != null && string.Equals(dir.Name, "app", StringComparison.OrdinalIgnoreCase))
                yield return dir.Parent.FullName;
        }

        private static void MoveFileIfDestinationMissing(string sourcePath, string destinationPath)
        {
            try
            {
                if (!File.Exists(sourcePath) || File.Exists(destinationPath)) return;
                if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Move(sourcePath, destinationPath);
            }
            catch { }
        }

        private static void MoveDirectoryContents(string sourceDir, string destinationDir)
        {
            try
            {
                if (!Directory.Exists(sourceDir)) return;
                Directory.CreateDirectory(destinationDir);

                if (string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(destinationDir), StringComparison.OrdinalIgnoreCase)) return;

                foreach (string sourcePath in Directory.EnumerateFileSystemEntries(sourceDir, "*", SearchOption.AllDirectories).ToList())
                {
                    string relativePath = Path.GetRelativePath(sourceDir, sourcePath);
                    string destinationPath = Path.Combine(destinationDir, relativePath);

                    if (Directory.Exists(sourcePath))
                    {
                        Directory.CreateDirectory(destinationPath);
                        continue;
                    }

                    if (File.Exists(sourcePath) && !File.Exists(destinationPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                        File.Move(sourcePath, destinationPath);
                    }
                }

                TryDeleteEmptyDirectoryTree(sourceDir);
            }
            catch { }
        }

        private static void MoveLegacyTtsCacheDirectory(string sourceDir)
        {
            try
            {
                if (!Directory.Exists(sourceDir)) return;
                if (string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(TtsCacheDir), StringComparison.OrdinalIgnoreCase)) return;

                Directory.CreateDirectory(TtsCacheDir);

                foreach (string sourcePath in Directory.EnumerateFileSystemEntries(sourceDir, "*", SearchOption.AllDirectories).ToList())
                {
                    if (Directory.Exists(sourcePath)) continue;

                    string relativePath = Path.GetRelativePath(sourceDir, sourcePath);
                    string destinationPath = Path.Combine(TtsCacheDir, relativePath);
                    if (File.Exists(destinationPath)) continue;

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                        File.Move(sourcePath, destinationPath);
                    }
                    catch
                    {
                        // TTS 缓存可重新生成；单个文件迁移失败不影响启动，后面会清理旧兼容目录。
                    }
                }
            }
            catch { }
            finally
            {
                DeleteLegacyTtsCacheDirectory(sourceDir);
            }
        }

        private static void DeleteLegacyTtsCacheDirectory(string sourceDir)
        {
            try
            {
                if (!Directory.Exists(sourceDir)) return;
                if (string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(TtsCacheDir), StringComparison.OrdinalIgnoreCase)) return;
                Directory.Delete(sourceDir, recursive: true);
            }
            catch { }
        }

        private static void TryDeleteEmptyDirectoryTree(string directoryPath)
        {
            try
            {
                if (!Directory.Exists(directoryPath)) return;
                foreach (string dir in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                if (!Directory.EnumerateFileSystemEntries(directoryPath).Any())
                    Directory.Delete(directoryPath);
            }
            catch { }
        }
    }
}
