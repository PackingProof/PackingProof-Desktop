using System;
using System.IO;

namespace ExpressPackingMonitoring.Config
{
    internal enum StorageReserveKind
    {
        LocalSystemDrive,
        LocalOtherDrive,
        NetworkLocation
    }

    public static class StorageSpacePolicy
    {
        public const long BytesPerGiB = 1024L * 1024L * 1024L;

        public static long CalculateMinimumReserveBytes(DriveInfo drive)
        {
            return CalculateMinimumReserveBytes(drive.RootDirectory.FullName, drive.TotalSize);
        }

        public static long CalculateMinimumReserveBytes(
            long totalSize,
            bool isSystemDrive) =>
            CalculateMinimumReserveBytes(
                totalSize,
                isSystemDrive
                    ? StorageReserveKind.LocalSystemDrive
                    : StorageReserveKind.LocalOtherDrive);

        public static long CalculateNetworkMinimumReserveBytes(long totalSize) =>
            CalculateMinimumReserveBytes(totalSize, StorageReserveKind.NetworkLocation);

        internal static long CalculateMinimumReserveBytes(StorageVolumeInfo volume) =>
            CalculateMinimumReserveBytes(volume.RootPath, volume.TotalSize);

        internal static long CalculateMinimumReserveBytes(
            long totalSize,
            StorageReserveKind kind)
        {
            long minimumBytes = kind switch
            {
                StorageReserveKind.LocalSystemDrive => 30L * BytesPerGiB,
                StorageReserveKind.NetworkLocation => 10L * BytesPerGiB,
                _ => 20L * BytesPerGiB
            };
            double percent = kind switch
            {
                StorageReserveKind.LocalSystemDrive => 0.10,
                StorageReserveKind.NetworkLocation => 0.02,
                _ => 0.05
            };
            long percentBytes = (long)Math.Ceiling(
                Math.Max(0, totalSize) * percent
                / (double)BytesPerGiB) * BytesPerGiB;
            return Math.Max(minimumBytes, percentBytes);
        }

        private static long CalculateMinimumReserveBytes(string rootPath, long totalSize)
        {
            StorageReserveKind kind = StorageVolumeInfo.IsNetworkPath(rootPath)
                ? StorageReserveKind.NetworkLocation
                : IsSystemDrive(rootPath)
                    ? StorageReserveKind.LocalSystemDrive
                    : StorageReserveKind.LocalOtherDrive;
            return CalculateMinimumReserveBytes(totalSize, kind);
        }

        public static long GetEffectiveReserveBytes(StorageLocation location, DriveInfo drive)
        {
            long minimumReserveBytes = CalculateMinimumReserveBytes(drive);
            long configuredReserveBytes = location.ReserveGB > 0
                ? (long)Math.Ceiling(location.ReserveGB) * BytesPerGiB
                : 0;
            return Math.Max(minimumReserveBytes, configuredReserveBytes);
        }

        internal static long GetEffectiveReserveBytes(
            StorageLocation location,
            StorageVolumeInfo volume)
        {
            long minimumReserveBytes = CalculateMinimumReserveBytes(volume);
            long configuredReserveBytes = location.ReserveGB > 0
                ? (long)Math.Ceiling(location.ReserveGB) * BytesPerGiB
                : 0;
            return Math.Max(minimumReserveBytes, configuredReserveBytes);
        }

        public static double GetEffectiveReserveGB(StorageLocation location)
        {
            double minimumReserveGB = GetMinimumReserveGB(location.Path);
            return Math.Ceiling(Math.Max(minimumReserveGB, location.ReserveGB));
        }

        public static double NormalizeReserveGB(string path, double reserveGB)
        {
            double minimumReserveGB = GetMinimumReserveGB(path);
            if (double.IsNaN(reserveGB) || double.IsInfinity(reserveGB))
                return minimumReserveGB;
            return Math.Ceiling(Math.Max(minimumReserveGB, reserveGB));
        }

        public static double GetMinimumReserveGB(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return 20.0;

                string normalizedPath = Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
                if (!StorageVolumeInfo.TryGet(normalizedPath, out StorageVolumeInfo volume))
                    return 20.0;
                return Math.Ceiling(CalculateMinimumReserveBytes(volume) / (double)BytesPerGiB);
            }
            catch
            {
                return 20.0;
            }
        }

        public static bool IsSystemDrive(string driveRoot)
        {
            string systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "";
            return string.Equals(
                Path.GetFullPath(driveRoot).TrimEnd(Path.DirectorySeparatorChar),
                systemRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
