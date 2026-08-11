using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 录像存储计划：
/// WorkingRootPath = 当前录像主存储路径，录像直接写入此目录（非临时目录）；
/// ArchiveTarget = 网络归档目标，NAS 仅为异步复制目标（单向归档模型）。
/// NOT a cache/buffer directory.
/// 未来大版本建议将 WorkingRootPath 更名为 RecordingRootPath 或 PrimaryStoragePath。
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
        throw new IOException($"本地录像保存位置不可用。{result.Path}：{result.Reason}");
    }

    public static string Resolve(AppConfig config, bool allowDefaultFallback) =>
        ResolveRecordingPlan(config, allowDefaultFallback).WorkingRootPath;

    /// <summary>
    /// 解析当前录像保存计划：本地存储列表按优先级/空间选出主存储（WorkingRootPath）；
    /// 网络位置仅作为归档目标，不参与本地轮换。RequiresNetworkArchive 表示“存在归档目标”。
    /// </summary>
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
        string? firstNetworkRoot = null;
        StorageLocationEvaluation? localChoice = null;

        foreach (StorageLocation location in locations)
        {
            string configuredPath = NormalizePath(location.Path);
            if (StorageVolumeInfo.IsNetworkPath(configuredPath))
            {
                // 网络备份目标不参与本地轮换；优先选当前可用（可达且空间充足）的，
                // 全部不可用时回退到优先级最高的一个。
                firstNetworkRoot ??= configuredPath;
                continue;
            }

            if (localChoice.HasValue)
                continue;

            StorageLocationEvaluation result = Evaluate(location);
            if (result.CanUse)
            {
                localChoice = result;
                RuntimeLog.Info(
                    "Storage",
                    $"Selected storage path={result.Path}, priority={location.Priority}, free={FormatBytes(result.AvailableBytes)}, reserve={FormatBytes(result.ReserveBytes)}");
            }
            else
            {
                failures.Add($"{result.Path}：{result.Reason}");
                RuntimeLog.Warn("Storage", $"Skip storage path={result.Path}, priority={location.Priority}, reason={result.Reason}");
            }
        }

        if (localChoice is { CanUse: true } usable)
        {
            string? archiveTarget = SelectUsableArchiveRoot(config) ?? firstNetworkRoot;
            return new RecordingStoragePlan(
                usable.Path,
                archiveTarget ?? "",
                !string.IsNullOrWhiteSpace(archiveTarget));
        }

        if (!allowDefaultFallback)
        {
            string detail = failures.Count > 0
                ? $"。{string.Join("；", failures)}"
                : "";
            throw new IOException($"没有可用的本地录像保存位置（至少需要一个本地保存位置）{detail}");
        }

        RuntimeLog.Warn("Storage", $"No local storage path is safe for recording, fallback default path={defaultPath}");
        EnsureDirectoryWritable(defaultPath);
        return new RecordingStoragePlan(defaultPath, "", false);
    }

    /// <summary>
    /// 按优先级返回全部网络备份位置（不做可用性检查），供归档 Worker 与 UI 使用。
    /// </summary>
    public static IReadOnlyList<StorageLocation> GetOrderedNetworkLocations(
        AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return (config.StorageLocations ?? [])
            .Where(location => !string.IsNullOrWhiteSpace(location.Path))
            .OrderBy(location => location.Priority)
            .Where(location => StorageVolumeInfo.IsNetworkPath(
                NormalizePath(location.Path)))
            .ToList();
    }

    /// <summary>
    /// 按优先级选择第一个当前可用（可达且可用空间高于预留值）的网络备份位置；
    /// excludePath 非空时跳过该路径及其子路径（用于“NAS 满后切换到下一个”）。
    /// 全部不可用返回 null。
    /// </summary>
    public static string? SelectUsableArchiveRoot(
        IReadOnlyList<StorageLocation> locations,
        string? excludePath = null)
    {
        foreach (StorageLocation location in locations)
        {
            if (string.IsNullOrWhiteSpace(location.Path))
                continue;
            string root = NormalizePath(location.Path);
            if (excludePath != null && IsPathUnderRoot(root, excludePath))
                continue;
            if (!StorageVolumeInfo.TryGet(root, out StorageVolumeInfo volume))
                continue; // 离线/不可达
            long reserveBytes = StorageSpacePolicy.GetEffectiveReserveBytes(location, volume);
            if (!NetworkArchiveSpacePolicy.IsBelowReserve(
                    volume.AvailableFreeSpace,
                    reserveBytes))
            {
                return root;
            }
        }
        return null;
    }

    public static string? SelectUsableArchiveRoot(
        AppConfig config,
        string? excludePath = null) =>
        SelectUsableArchiveRoot(GetOrderedNetworkLocations(config), excludePath);

    private static bool IsPathUnderRoot(string root, string path)
    {
        try
        {
            string normalizedRoot = NormalizePath(root);
            string normalizedPath = NormalizePath(path);
            if (string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase))
                return true;
            string prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
