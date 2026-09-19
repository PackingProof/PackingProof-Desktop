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
            => GetFloorReserveBytes(kind);

        /// <summary>
        /// 预留底线，用户配置不能低于它：系统盘 2GB，其他本地盘与网络位置 1GB。
        /// 不再按容量百分比：1TB 盘按百分比会算出几十 GB，机器剩 90 多 GB 也会被判空间不足。
        /// </summary>
        internal static long GetFloorReserveBytes(StorageReserveKind kind) => kind switch
        {
            StorageReserveKind.LocalSystemDrive => 2L * BytesPerGiB,
            _ => 1L * BytesPerGiB
        };

        /// <summary>用户没有单独设置时的默认预留：系统盘 10GB，其他本地盘与网络位置 5GB</summary>
        internal static long GetDefaultReserveBytes(StorageReserveKind kind) => kind switch
        {
            StorageReserveKind.LocalSystemDrive => 10L * BytesPerGiB,
            _ => 5L * BytesPerGiB
        };

        private static long CalculateMinimumReserveBytes(string rootPath, long totalSize)
        {
            StorageReserveKind kind = StorageVolumeInfo.IsBackupTargetPath(rootPath)
                ? StorageReserveKind.NetworkLocation
                : IsSystemDrive(rootPath)
                    ? StorageReserveKind.LocalSystemDrive
                    : StorageReserveKind.LocalOtherDrive;
            return CalculateMinimumReserveBytes(totalSize, kind);
        }

        public static long GetEffectiveReserveBytes(StorageLocation location, DriveInfo drive)
        {
            StorageReserveKind kind = ResolveKind(drive.RootDirectory.FullName);
            return ResolveReserveBytes(location.ReserveGB, kind);
        }

        internal static long GetEffectiveReserveBytes(
            StorageLocation location,
            StorageVolumeInfo volume)
        {
            return ResolveReserveBytes(location.ReserveGB, ResolveKind(volume.RootPath));
        }

        public static double GetEffectiveReserveGB(StorageLocation location)
        {
            StorageReserveKind kind = ResolveKind(location.Path);
            double floorGB = GetFloorReserveBytes(kind) / (double)BytesPerGiB;
            double defaultGB = GetDefaultReserveBytes(kind) / (double)BytesPerGiB;
            double requested = double.IsNaN(location.ReserveGB) || double.IsInfinity(location.ReserveGB)
                ? defaultGB
                : location.ReserveGB > 0 ? location.ReserveGB : defaultGB;
            return Math.Ceiling(Math.Max(floorGB, requested));
        }

        public static double NormalizeReserveGB(string path, double reserveGB)
        {
            // 0 表示"用户没有单独设置"，保留 0 让默认值生效；其余按底线收口
            if (double.IsNaN(reserveGB) || double.IsInfinity(reserveGB) || reserveGB <= 0)
                return 0;

            double floorGB = GetFloorReserveBytes(ResolveKind(path)) / (double)BytesPerGiB;
            return Math.Ceiling(Math.Max(floorGB, reserveGB));
        }

        /// <summary>用户设置优先（可低于默认），没有设置就用默认值，两者都不低于底线</summary>
        private static long ResolveReserveBytes(double configuredGB, StorageReserveKind kind)
        {
            long floorBytes = GetFloorReserveBytes(kind);
            if (double.IsNaN(configuredGB) || double.IsInfinity(configuredGB) || configuredGB <= 0)
                return Math.Max(floorBytes, GetDefaultReserveBytes(kind));

            return Math.Max(floorBytes, (long)Math.Ceiling(configuredGB) * BytesPerGiB);
        }

        /// <summary>按路径判定预留类别：备份目标算网络位置，其余按是否系统盘区分</summary>
        internal static StorageReserveKind ResolveKind(string path)
        {
            if (StorageVolumeInfo.IsBackupTargetPath(path))
                return StorageReserveKind.NetworkLocation;
            return IsSystemDrive(path) ? StorageReserveKind.LocalSystemDrive : StorageReserveKind.LocalOtherDrive;
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
