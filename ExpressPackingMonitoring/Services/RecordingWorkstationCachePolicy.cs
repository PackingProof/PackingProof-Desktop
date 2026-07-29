using ExpressPackingMonitoring.Config;
using System.IO;

namespace ExpressPackingMonitoring.Services;

internal readonly record struct RecordingCacheDriveCandidate(
    string RootPath,
    string SuggestedPath,
    bool IsReady,
    DriveType DriveType,
    bool IsWritable,
    bool IsSystemDrive,
    long TotalBytes,
    long AvailableBytes);

internal readonly record struct RecordingCacheSpaceSnapshot(
    long CacheBytes,
    long ConfiguredLimitBytes,
    long SafeCapacityBytes,
    long EffectiveLimitBytes,
    long AvailableBytes,
    long ReserveBytes)
{
    public long RemainingBytes => Math.Max(
        0,
        Math.Min(
            EffectiveLimitBytes - CacheBytes,
            AvailableBytes - ReserveBytes));

    public double UsagePercent => EffectiveLimitBytes <= 0
        ? 100
        : Math.Clamp(CacheBytes * 100d / EffectiveLimitBytes, 0, 100);
}

internal readonly record struct RecordingCacheCleanupItem(
    long Id,
    DateTime CreatedAt,
    long SizeBytes);

internal sealed record RecordingCacheCleanupPlan(
    IReadOnlyList<long> ItemIds,
    long TargetCacheBytes,
    long ProjectedCacheBytes);

internal static class RecordingWorkstationCachePolicy
{
    internal const int DefaultLimitGB = 100;
    internal const long RecordingAndPackagingHeadroomBytes =
        2L * StorageSpacePolicy.BytesPerGiB;
    internal const long HardStopHeadroomBytes =
        StorageSpacePolicy.BytesPerGiB / 2;
    internal const double WarningWatermark = 0.80;
    internal const double CleanupWatermark = 0.90;
    internal const double CleanupTargetWatermark = 0.70;

    internal static RecordingCacheDriveCandidate? SelectPreferredDrive(
        IEnumerable<RecordingCacheDriveCandidate> candidates,
        long minimumHeadroomBytes = RecordingAndPackagingHeadroomBytes)
    {
        return candidates
            .Where(candidate =>
                candidate.IsReady
                && candidate.DriveType == DriveType.Fixed
                && candidate.IsWritable
                && GetSafeAvailableBytes(candidate) >= minimumHeadroomBytes)
            .OrderBy(candidate => candidate.IsSystemDrive)
            .ThenByDescending(GetSafeAvailableBytes)
            .ThenBy(candidate => candidate.RootPath, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => (RecordingCacheDriveCandidate?)candidate)
            .FirstOrDefault();
    }

    internal static long GetSafeAvailableBytes(RecordingCacheDriveCandidate candidate)
    {
        long reserveBytes = StorageSpacePolicy.CalculateMinimumReserveBytes(
            candidate.TotalBytes,
            candidate.IsSystemDrive);
        return Math.Max(0, candidate.AvailableBytes - reserveBytes);
    }

    internal static RecordingCacheSpaceSnapshot CalculateSpace(
        long cacheBytes,
        long configuredLimitBytes,
        long availableBytes,
        long reserveBytes)
    {
        cacheBytes = Math.Max(0, cacheBytes);
        configuredLimitBytes = Math.Max(0, configuredLimitBytes);
        availableBytes = Math.Max(0, availableBytes);
        reserveBytes = Math.Max(0, reserveBytes);
        long safeCapacityBytes = cacheBytes + Math.Max(0, availableBytes - reserveBytes);
        long effectiveLimitBytes = Math.Min(configuredLimitBytes, safeCapacityBytes);
        return new RecordingCacheSpaceSnapshot(
            cacheBytes,
            configuredLimitBytes,
            safeCapacityBytes,
            effectiveLimitBytes,
            availableBytes,
            reserveBytes);
    }

    internal static RecordingCacheCleanupPlan CreateCleanupPlan(
        RecordingCacheSpaceSnapshot snapshot,
        long requiredHeadroomBytes,
        IEnumerable<RecordingCacheCleanupItem> verifiedItems)
    {
        requiredHeadroomBytes = Math.Max(0, requiredHeadroomBytes);
        bool needsCleanup =
            snapshot.CacheBytes + requiredHeadroomBytes > snapshot.EffectiveLimitBytes
            || snapshot.AvailableBytes - snapshot.ReserveBytes < requiredHeadroomBytes
            || (snapshot.EffectiveLimitBytes > 0
                && snapshot.CacheBytes >= snapshot.EffectiveLimitBytes * CleanupWatermark);
        if (!needsCleanup)
        {
            return new RecordingCacheCleanupPlan(
                Array.Empty<long>(),
                snapshot.CacheBytes,
                snapshot.CacheBytes);
        }

        long limitAfterHeadroom = Math.Max(
            0,
            snapshot.EffectiveLimitBytes - requiredHeadroomBytes);
        long lowWatermark = (long)Math.Floor(
            snapshot.EffectiveLimitBytes * CleanupTargetWatermark);
        long targetBytes = Math.Min(limitAfterHeadroom, lowWatermark);
        long projectedBytes = snapshot.CacheBytes;
        var selected = new List<long>();
        foreach (RecordingCacheCleanupItem item in verifiedItems
                     .Where(item => item.SizeBytes > 0)
                     .OrderBy(item => item.CreatedAt)
                     .ThenBy(item => item.Id))
        {
            if (projectedBytes <= targetBytes)
                break;
            selected.Add(item.Id);
            projectedBytes = Math.Max(0, projectedBytes - item.SizeBytes);
        }

        return new RecordingCacheCleanupPlan(selected, targetBytes, projectedBytes);
    }

    internal static IReadOnlyList<RecordingCacheDriveCandidate> EnumerateLocalFixedDrives()
    {
        var candidates = new List<RecordingCacheDriveCandidate>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                    continue;

                bool isSystemDrive = StorageSpacePolicy.IsSystemDrive(
                    drive.RootDirectory.FullName);
                string suggestedPath = GetSuggestedPath(
                    drive.RootDirectory.FullName,
                    isSystemDrive);
                candidates.Add(new RecordingCacheDriveCandidate(
                    drive.RootDirectory.FullName,
                    suggestedPath,
                    drive.IsReady,
                    drive.DriveType,
                    ProbeDirectoryWritable(suggestedPath),
                    isSystemDrive,
                    drive.TotalSize,
                    drive.AvailableFreeSpace));
            }
            catch
            {
                // 单个磁盘状态异常不应影响其他候选磁盘。
            }
        }
        return candidates;
    }

    internal static bool ConfigureInitialLocation(
        AppConfig config,
        bool preserveExistingLocation)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (preserveExistingLocation
            && config.StorageLocations?
                .OrderBy(location => location.Priority)
                .Any(location => !string.IsNullOrWhiteSpace(location.Path)) == true)
        {
            return false;
        }

        RecordingCacheDriveCandidate? selected = SelectPreferredDrive(
            EnumerateLocalFixedDrives());
        if (selected == null)
            return false;

        config.StorageLocations =
        [
            new StorageLocation
            {
                Path = selected.Value.SuggestedPath,
                ReserveGB = StorageSpacePolicy.GetMinimumReserveGB(
                    selected.Value.SuggestedPath),
                Priority = 0
            }
        ];
        config.RecordingCachePolicy = "KeepWithinSize";
        if (config.RecordingCacheMaxGB <= 0)
            config.RecordingCacheMaxGB = DefaultLimitGB;
        return true;
    }

    internal static StorageLocation? GetConfiguredLocation(AppConfig config) =>
        config.StorageLocations?
            .Where(location => !string.IsNullOrWhiteSpace(location.Path))
            .OrderBy(location => location.Priority)
            .FirstOrDefault();

    internal static string GetSuggestedPath(string rootPath, bool isSystemDrive)
    {
        if (isSystemDrive)
        {
            string videosPath = Environment.GetFolderPath(
                Environment.SpecialFolder.MyVideos);
            if (!string.IsNullOrWhiteSpace(videosPath))
                return Path.Combine(videosPath, "快递打包视频");
        }
        return Path.Combine(rootPath, "快递打包视频");
    }

    private static bool ProbeDirectoryWritable(string path)
    {
        bool existed = Directory.Exists(path);
        string probePath = Path.Combine(
            path,
            $".recording-cache-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(path);
            using (new FileStream(
                       probePath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
            }
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { File.Delete(probePath); } catch { }
            if (!existed)
            {
                try
                {
                    if (Directory.Exists(path)
                        && !Directory.EnumerateFileSystemEntries(path).Any())
                    {
                        Directory.Delete(path);
                    }
                }
                catch { }
            }
        }
    }
}
