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

        /// <summary>
        /// 该位置未单独设置时的默认预留（GB），等同建议值：低于它仍可录制，
        /// 但磁盘写满风险上升，设置页需要提示用户。
        /// </summary>
        public static double GetDefaultReserveGB(string path) =>
            GetDefaultReserveBytes(ResolveKind(path)) / (double)BytesPerGiB;

        /// <summary>
        /// 旧版本（0.0.67 及更早）按容量百分比自动算出的预留：
        /// 系统盘 max(30GB, 10%)、其他本地盘 max(20GB, 5%)、网络位置 max(10GB, 2%)。
        /// 只用于识别升级前的陈旧配置，不再是现行规则。
        /// </summary>
        internal static long CalculateLegacyAutoReserveBytes(
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

        /// <summary>
        /// 升级迁移：只有与旧版自动值一致（容差 1GB）的预留才判定为旧规则自动写入，
        /// 收敛到新的默认预留；用户自己调过的值原样保留，只做底线收口，
        /// 避免把用户刻意留出的容量上限（上限越小，预留越大）改成默认值。
        /// </summary>
        public static double MigrateLegacyReserveGB(string path, double reserveGB)
        {
            if (double.IsNaN(reserveGB) || double.IsInfinity(reserveGB) || reserveGB <= 0)
                return 0;

            if (TryGetLocalDriveTotalBytes(path, out long totalSize))
            {
                return MigrateLegacyReserveGB(reserveGB, totalSize, ResolveKind(path));
            }
            return NormalizeReserveGB(path, reserveGB);
        }

        internal static double MigrateLegacyReserveGB(
            double reserveGB,
            long totalSize,
            StorageReserveKind kind)
        {
            if (double.IsNaN(reserveGB) || double.IsInfinity(reserveGB) || reserveGB <= 0)
                return 0;

            double legacyAutoGB =
                CalculateLegacyAutoReserveBytes(totalSize, kind) / (double)BytesPerGiB;
            if (Math.Abs(reserveGB - legacyAutoGB) <= 1.0)
                return GetDefaultReserveBytes(kind) / (double)BytesPerGiB;

            double floorGB = GetFloorReserveBytes(kind) / (double)BytesPerGiB;
            return Math.Ceiling(Math.Max(floorGB, reserveGB));
        }

        private static bool TryGetLocalDriveTotalBytes(string path, out long totalSize)
        {
            totalSize = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
                if (root.Length == 0 || root.StartsWith(@"\\", StringComparison.Ordinal))
                    return false;

                var drive = new DriveInfo(root);
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) return false;
                totalSize = drive.TotalSize;
                return totalSize > 0;
            }
            catch
            {
                return false;
            }
        }

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

        /// <summary>
        /// 按路径判定预留类别：备份目标算网络位置，其余按所在盘是否为系统盘区分。
        /// 传入录像目录这类子目录时也要归到磁盘根上判断，否则系统盘上的目录会被当成其他盘。
        /// </summary>
        internal static StorageReserveKind ResolveKind(string path)
        {
            if (StorageVolumeInfo.IsBackupTargetPath(path))
                return StorageReserveKind.NetworkLocation;
            return IsSystemDrive(GetDriveRoot(path))
                ? StorageReserveKind.LocalSystemDrive
                : StorageReserveKind.LocalOtherDrive;
        }

        private static string GetDriveRoot(string path)
        {
            try
            {
                return Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
            }
            catch
            {
                return path;
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
