using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 录像存储计划：录像进程只写 WorkingRootPath；
/// 当选择网络位置时，WorkingRootPath 为本地缓冲，ArchiveTarget 为网络归档目标。
/// </summary>
internal readonly record struct RecordingStoragePlan(
    string WorkingRootPath,
    string ArchiveTarget,
    bool RequiresNetworkArchive);

internal static class StorageLocationResolver
{
    public static string Resolve(StorageLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        StorageLocationEvaluation result = Evaluate(location);
        if (result.CanUse)
            return result.Path;
        throw new IOException($"本地缓存位置不可用。{result.Path}：{result.Reason}");
    }

    public static string Resolve(AppConfig config, bool allowDefaultFallback) =>
        ResolveRecordingPlan(config, allowDefaultFallback).WorkingRootPath;

    public static RecordingStoragePlan ResolveRecordingPlan(
        AppConfig config,
        bool allowDefaultFallback)
    {
        ArgumentNullException.ThrowIfNull(config);

        string defaultPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Videos");
        List<StorageLocation> locations = config.StorageLocations?
            .Where(location => !string.IsNullOrWhiteSpace(location.Path))
            .OrderBy(location => location.Priority)
            .ToList() ?? [];

        if (locations.Count == 0)
        {
            if (!allowDefaultFallback)
                throw new IOException("未配置录像存储位置，请先选择“使用电脑摄像头录像”并完成存储设置");

            RuntimeLog.Warn("Storage", $"No storage locations configured, fallback default path={defaultPath}");
            EnsureDirectoryWritable(defaultPath);
            return new RecordingStoragePlan(defaultPath, "", false);
        }

        var failures = new List<string>();
        foreach (StorageLocation location in locations)
        {
            string configuredPath = NormalizePath(location.Path);
            if (StorageVolumeInfo.IsNetworkPath(configuredPath))
            {
                StorageLocationEvaluation buffer = EvaluateLocalBuffer(config.LocalRecordingBufferPath);
                if (!buffer.CanUse)
                {
                    failures.Add($"{configuredPath}：本地录像缓冲不可用：{buffer.Reason}");
                    RuntimeLog.Warn("Storage", $"Skip network storage path={configuredPath}, priority={location.Priority}, reason={buffer.Reason}");
                    continue;
                }

                RuntimeLog.Info(
                    "Storage",
                    $"Selected network archive={configuredPath}, localBuffer={buffer.Path}, priority={location.Priority}");
                return new RecordingStoragePlan(buffer.Path, configuredPath, true);
            }

            StorageLocationEvaluation result = Evaluate(location);
            if (result.CanUse)
            {
                RuntimeLog.Info(
                    "Storage",
                    $"Selected storage path={result.Path}, priority={location.Priority}, free={FormatBytes(result.AvailableBytes)}, reserve={FormatBytes(result.ReserveBytes)}");
                return new RecordingStoragePlan(result.Path, result.Path, false);
            }

            failures.Add($"{result.Path}：{result.Reason}");
            RuntimeLog.Warn("Storage", $"Skip storage path={result.Path}, priority={location.Priority}, reason={result.Reason}");
        }

        if (!allowDefaultFallback)
            throw new IOException($"没有可用的录像存储位置。{string.Join("；", failures)}");

        RuntimeLog.Warn("Storage", $"No configured storage path is safe for recording, fallback default path={defaultPath}");
        EnsureDirectoryWritable(defaultPath);
        return new RecordingStoragePlan(defaultPath, "", false);
    }

    public static bool IsValidLocalBufferPath(string path, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "请选择本地录像缓冲目录";
            return false;
        }

        if (StorageVolumeInfo.IsNetworkPath(path))
        {
            reason = "本地录像缓冲目录必须位于本机固定磁盘";
            return false;
        }

        try
        {
            string fullPath = NormalizePath(path);
            string root = Path.GetPathRoot(fullPath) ?? "";
            if (root.Length == 0 || new DriveInfo(root).DriveType != DriveType.Fixed)
            {
                reason = "本地录像缓冲目录必须位于本机固定磁盘";
                return false;
            }
            EnsureDirectoryWritable(fullPath);
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static StorageLocationEvaluation EvaluateLocalBuffer(string path)
    {
        if (!IsValidLocalBufferPath(path, out string reason))
            return StorageLocationEvaluation.Skip(NormalizePathOrOriginal(path), reason);

        string normalized = NormalizePath(path);
        if (!StorageVolumeInfo.TryGet(normalized, out StorageVolumeInfo volume))
            return StorageLocationEvaluation.Skip(normalized, "无法读取本地缓冲磁盘的可用空间");

        var location = new StorageLocation { Path = normalized, ReserveGB = 0 };
        long reserveBytes = StorageSpacePolicy.GetEffectiveReserveBytes(location, volume);
        if (volume.AvailableFreeSpace <= reserveBytes)
        {
            return StorageLocationEvaluation.Skip(
                normalized,
                $"剩余空间低于安全预留值（可用 {FormatBytes(volume.AvailableFreeSpace)}，需预留 {FormatBytes(reserveBytes)}）");
        }
        return StorageLocationEvaluation.Use(normalized, volume.AvailableFreeSpace, reserveBytes);
    }

    private static StorageLocationEvaluation Evaluate(StorageLocation location)
    {
        string path = NormalizePath(location.Path);
        try
        {
            Directory.CreateDirectory(path);
            EnsureDirectoryWritable(path);

            if (!StorageVolumeInfo.TryGet(path, out StorageVolumeInfo volume))
                return StorageLocationEvaluation.Skip(path, "无法读取存储位置的可用空间");

            long reserveBytes = StorageSpacePolicy.GetEffectiveReserveBytes(location, volume);
            long availableBytes = volume.AvailableFreeSpace;
            if (availableBytes <= reserveBytes)
            {
                return StorageLocationEvaluation.Skip(
                    path,
                    $"剩余空间低于预留值（可用 {FormatBytes(availableBytes)}，需预留 {FormatBytes(reserveBytes)}）");
            }

            return StorageLocationEvaluation.Use(path, availableBytes, reserveBytes);
        }
        catch (Exception ex)
        {
            return StorageLocationEvaluation.Skip(path, ex.Message);
        }
    }

    private static string NormalizePath(string path)
    {
        string combined = Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        return Path.GetFullPath(combined);
    }

    private static string NormalizePathOrOriginal(string path)
    {
        try { return NormalizePath(path); }
        catch { return path?.Trim() ?? ""; }
    }

    private static void EnsureDirectoryWritable(string path)
    {
        Directory.CreateDirectory(path);
        string probe = Path.Combine(path, $".write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }
        }
        finally
        {
            try { File.Delete(probe); } catch { }
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 GB";
        return $"{bytes / (double)StorageSpacePolicy.BytesPerGiB:F1} GB";
    }

    private readonly record struct StorageLocationEvaluation(
        bool CanUse,
        string Path,
        string Reason,
        long AvailableBytes,
        long ReserveBytes)
    {
        public static StorageLocationEvaluation Use(string path, long availableBytes, long reserveBytes) =>
            new(true, path, "", availableBytes, reserveBytes);

        public static StorageLocationEvaluation Skip(string path, string reason) =>
            new(false, path, reason, 0, 0);
    }
}
