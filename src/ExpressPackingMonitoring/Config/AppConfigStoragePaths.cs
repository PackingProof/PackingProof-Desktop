using ExpressPackingMonitoring.UpdateCore;
using System.IO;

namespace ExpressPackingMonitoring.Config;

internal readonly record struct StorageDriveCandidate(
    string RootPath,
    bool IsReady,
    DriveType DriveType);

internal static class AppConfigStoragePaths
{
    internal const string LegacyStorageFolderName = "快递打包视频";

    internal static string? ReadOnlySystemRoot =>
        OperatingSystem.IsMacOS() ? "/" : null;

    internal static bool MigrateRecordingCacheDefaultPath(AppConfig config)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        string? legacyPath = LegacyRecordingCachePath;
        if (legacyPath == null)
            return false;

        StorageLocation? legacy = config.StorageLocations.FirstOrDefault(location =>
            IsSamePath(location.Path, legacyPath));
        if (legacy == null)
            return false;

        string preferredPath = DefaultStoragePath;
        StorageLocation? preferred = config.StorageLocations.FirstOrDefault(location =>
            IsSamePath(location.Path, preferredPath));
        if (preferred != null && preferred.Priority < legacy.Priority)
            return false;
        if (preferred == null)
        {
            preferred = new StorageLocation
            {
                Path = preferredPath,
                ReserveGB = StorageSpacePolicy.GetDefaultReserveGB(preferredPath),
                Priority = legacy.Priority
            };
            config.StorageLocations.Add(preferred);
        }

        int preferredPriority = preferred.Priority;
        preferred.Priority = legacy.Priority;
        legacy.Priority = Math.Max(preferredPriority, preferred.Priority + 1);
        return true;
    }

    internal static string? LegacyRecordingCachePath
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return null;

            string videosRoot = Environment.GetFolderPath(
                Environment.SpecialFolder.MyVideos);
            return string.IsNullOrWhiteSpace(videosRoot)
                ? null
                : Path.Combine(videosRoot, DefaultStorageFolderName);
        }
    }

    internal static bool RemoveUnusableStorageLocations(
        List<StorageLocation> locations,
        string? readOnlySystemRoot)
    {
        if (string.IsNullOrWhiteSpace(readOnlySystemRoot))
            return false;

        return locations.RemoveAll(location =>
            IsDirectChildOfRoot(location.Path, readOnlySystemRoot)) > 0;
    }

    internal static List<StorageLocation> CreateDefaultStorageLocations()
    {
        try
        {
            return CreateDefaultStorageLocations(
                DriveInfo.GetDrives().Select(drive =>
                    new StorageDriveCandidate(
                        drive.Name,
                        drive.IsReady,
                        drive.DriveType)),
                ReadOnlySystemRoot);
        }
        catch
        {
            return CreateDefaultStorageLocations(
                Array.Empty<StorageDriveCandidate>(),
                ReadOnlySystemRoot);
        }
    }

    internal static List<StorageLocation> CreateDefaultStorageLocations(
        IEnumerable<StorageDriveCandidate> drives) =>
        CreateDefaultStorageLocations(drives, ReadOnlySystemRoot);

    internal static List<StorageLocation> CreateDefaultStorageLocations(
        IEnumerable<StorageDriveCandidate> drives,
        string? readOnlySystemRoot,
        string? fallbackRoot = null)
    {
        var roots = drives
            .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
            .Select(drive => Path.GetPathRoot(drive.RootPath) ?? drive.RootPath)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Where(root => !IsSamePath(root, readOnlySystemRoot))
            .Select(root =>
                root.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(root => root, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (roots.Count == 0)
            roots.Add(fallbackRoot ?? DefaultStorageRoot);

        return roots
            .Select((root, index) =>
            {
                string path = Path.Combine(root, DefaultStorageFolderName);
                return new StorageLocation
                {
                    Path = path,
                    ReserveGB = StorageSpacePolicy.GetDefaultReserveGB(path),
                    Priority = index
                };
            })
            .ToList();
    }

    internal static string DefaultStorageRoot =>
        OperatingSystem.IsWindows()
            ? Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\"
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Movies") + Path.DirectorySeparatorChar;

    internal static string DefaultStorageFolderName =>
        BrandMetadata.ArtifactPrefix + "Videos";

    internal static string DefaultStoragePath =>
        Path.Combine(DefaultStorageRoot, DefaultStorageFolderName);

    private static bool IsDirectChildOfRoot(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(path.Trim())) ?? "";
            return string.Equals(
                TrimTrailingSeparators(parent),
                TrimTrailingSeparators(Path.GetFullPath(root)),
                StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string TrimTrailingSeparators(string path) =>
        path.Length <= 1
            ? path
            : path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

    private static bool IsSamePath(string left, string? right)
    {
        if (string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return string.Equals(
                TrimTrailingSeparators(Path.GetFullPath(left)),
                TrimTrailingSeparators(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
