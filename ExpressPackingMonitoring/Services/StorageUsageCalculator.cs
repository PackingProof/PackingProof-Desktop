using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using System.IO;

namespace ExpressPackingMonitoring.Services;

internal sealed record StorageUsageSnapshot(long VideoBytes, long CapacityBytes)
{
    public double UsagePercent => CapacityBytes > 0
        ? Math.Min(100, VideoBytes * 100d / CapacityBytes)
        : 0;
}

internal static class StorageUsageCalculator
{
    private static readonly string[] VideoExtensions = [".mkv", ".mp4"];

    public static StorageUsageSnapshot Scan(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        long videoBytes = 0;
        long capacityBytes = 0;
        var scannedVolumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (StorageLocation location in config.StorageLocations ?? [])
        {
            if (string.IsNullOrWhiteSpace(location.Path)
                || StorageLocationResolver.IsBackupLocation(location))
            {
                continue;
            }

            string path = Path.IsPathRooted(location.Path)
                ? location.Path
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, location.Path);
            if (!Directory.Exists(path)
                || !StorageVolumeInfo.TryGet(path, out StorageVolumeInfo volume)
                || !scannedVolumes.Add(volume.RootPath))
            {
                continue;
            }

            long locationVideoBytes = GetVideoBytes(path);
            videoBytes += locationVideoBytes;
            long reserveBytes = StorageSpacePolicy.GetEffectiveReserveBytes(location, volume);
            capacityBytes += Math.Max(0, volume.AvailableFreeSpace - reserveBytes)
                + locationVideoBytes;
        }

        return new StorageUsageSnapshot(videoBytes, capacityBytes);
    }

    public static string Format(StorageUsageSnapshot snapshot, VideoDatabase? database)
    {
        double usedGB = snapshot.VideoBytes / (double)StorageSpacePolicy.BytesPerGiB;
        double capacityGB = snapshot.CapacityBytes / (double)StorageSpacePolicy.BytesPerGiB;
        string estimateText = "";
        try
        {
            var (databaseBytes, databaseSeconds) = database?.GetGlobalSizeAndDuration() ?? (0, 0);
            if (databaseBytes > 0 && databaseSeconds > 0 && snapshot.CapacityBytes > 0)
            {
                double retentionHours = snapshot.CapacityBytes
                    / (databaseBytes / (double)databaseSeconds)
                    / 3600;
                estimateText = retentionHours >= 1
                    ? $"，预计循环可录 {retentionHours:F0} 小时"
                    : $"，预计循环可录 {retentionHours * 60:F0} 分钟";
            }
        }
        catch
        {
        }

        return $"{usedGB:F1} / {capacityGB:F1} GB{estimateText}";
    }

    public static IReadOnlyList<string> GetManagedLocalRoots(AppConfig config) =>
        (config.StorageLocations ?? [])
        .Where(location => !string.IsNullOrWhiteSpace(location.Path))
        .Select(location => Path.IsPathRooted(location.Path)
            ? location.Path
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, location.Path))
        .Where(StorageVolumeInfo.IsConfirmedLocal)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static long GetVideoBytes(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*.*", SearchOption.AllDirectories)
                .Where(file => VideoExtensions.Contains(
                    file.Extension,
                    StringComparer.OrdinalIgnoreCase))
                .Sum(file => file.Length);
        }
        catch
        {
            return 0;
        }
    }
}
