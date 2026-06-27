#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ExpressPackingMonitoring
{
    internal static class AppPaths
    {
        public static readonly string UserDataDir = Path.Combine(GetLocalAppDataRoot(), "ExpressPackingMonitoring");

        public static readonly string LogDir = Path.Combine(UserDataDir, "log");
        public static readonly string CacheDir = Path.Combine(UserDataDir, "cache");
        public static readonly string BackupsDir = Path.Combine(UserDataDir, "backups");
        public static readonly string TranscodeCacheDir = Path.Combine(CacheDir, "transcode");
        public static readonly string TtsCacheDir = Path.Combine(CacheDir, "tts");

        public static readonly string ConfigPath = Path.Combine(UserDataDir, "config.json");
        public static readonly string VideoDatabasePath = Path.Combine(UserDataDir, "videos.db");
        public static readonly string WebDebugLogPath = Path.Combine(LogDir, "web_debug.log");
        public static readonly string EncoderDetectLogPath = Path.Combine(LogDir, "encoder_detect.log");
        public static readonly string OrderInfoCachePath = Path.Combine(CacheDir, "orderinfo_cache.json");

        static AppPaths()
        {
            EnsureUserDataDirectories();
            MigrateLegacyRuntimeData();
        }

        public static void EnsureUserDataDirectories()
        {
            Directory.CreateDirectory(UserDataDir);
            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(CacheDir);
            Directory.CreateDirectory(BackupsDir);
            Directory.CreateDirectory(TranscodeCacheDir);
            Directory.CreateDirectory(TtsCacheDir);
        }

        private static string GetLocalAppDataRoot()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localAppData)
                ? AppDomain.CurrentDomain.BaseDirectory
                : localAppData;
        }

        public static string FindFFmpeg()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
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

            return null;
        }

        private static void MigrateLegacyRuntimeData()
        {
            foreach (string legacyRoot in GetLegacyRuntimeRoots())
            {
                MoveFileIfDestinationMissing(Path.Combine(legacyRoot, "config.json"), ConfigPath);
                MoveFileIfDestinationMissing(Path.Combine(legacyRoot, "videos.db"), VideoDatabasePath);
                MoveFileIfDestinationMissing(Path.Combine(legacyRoot, "videos.db-wal"), VideoDatabasePath + "-wal");
                MoveFileIfDestinationMissing(Path.Combine(legacyRoot, "videos.db-shm"), VideoDatabasePath + "-shm");
                MoveFileIfDestinationMissing(Path.Combine(legacyRoot, "orderinfo_cache.json"), OrderInfoCachePath);
                MoveFileIfDestinationMissing(Path.Combine(legacyRoot, "web_debug.log"), WebDebugLogPath);
                MoveFileIfDestinationMissing(Path.Combine(legacyRoot, "encoder_detect.log"), EncoderDetectLogPath);

                MoveDirectoryContents(Path.Combine(legacyRoot, "transcache"), TranscodeCacheDir);
                MoveDirectoryContents(Path.Combine(legacyRoot, "tts_cache"), TtsCacheDir);
            }
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
