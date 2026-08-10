using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ExpressPackingMonitoring.Config
{
    /// <summary>
    /// 提供本地卷与 UNC 网络共享的容量信息。
    /// DriveInfo 不接受 UNC 根路径，GetDiskFreeSpaceEx 同时支持盘符与 UNC。
    /// VolumeId 仅用于诊断和未来盘符变化重定位，本版本不实现重映射。
    /// </summary>
    internal readonly record struct StorageVolumeInfo(
        string RootPath,
        long TotalSize,
        long AvailableFreeSpace,
        string VolumeId)
    {
        public static bool TryGet(string path, out StorageVolumeInfo volume)
        {
            volume = default;
            if (string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                string fullPath = Path.GetFullPath(path);
                string? root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrWhiteSpace(root)) return false;

                string queryPath = Path.EndsInDirectorySeparator(root)
                    ? root
                    : root + Path.DirectorySeparatorChar;
                if (!GetDiskFreeSpaceEx(
                        queryPath,
                        out ulong availableBytes,
                        out ulong totalBytes,
                        out _))
                {
                    return false;
                }

                string normalizedRoot = root.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                volume = new StorageVolumeInfo(
                    normalizedRoot,
                    ClampToInt64(totalBytes),
                    ClampToInt64(availableBytes),
                    GetVolumeIdForRoot(queryPath));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 判断路径是否为网络位置（UNC 或映射网络盘）。
        /// </summary>
        public static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;

            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
                return root.Length > 0 && new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取盘符根的卷 GUID 名称（如 \\?\Volume{...}）；UNC 或无卷路径返回空。
        /// </summary>
        public static string GetVolumeIdForRoot(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath)) return "";
            if (rootPath.StartsWith(@"\\", StringComparison.Ordinal)) return "";

            try
            {
                string mountPoint = Path.EndsInDirectorySeparator(rootPath)
                    ? rootPath
                    : rootPath + Path.DirectorySeparatorChar;
                var buffer = new StringBuilder(64);
                if (!GetVolumeNameForVolumeMountPoint(mountPoint, buffer, (uint)buffer.Capacity))
                    return "";
                return buffer.ToString().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 把映射盘路径（Z:\folder）解析为 UNC（\\NAS\share\folder），避免盘符变化导致配置失效。
        /// UNC 原样返回；本地路径或解析失败返回 false 并保留原路径。
        /// </summary>
        public static bool TryResolveUncPath(string path, out string uncPath) =>
            TryResolveUncPath(path, out uncPath, ResolveMappedRootWithWNet);

        internal static bool TryResolveUncPath(
            string path,
            out string uncPath,
            Func<string, string?>? mappedRootResolver)
        {
            uncPath = "";
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                string trimmed = path.Trim().Trim('"');
                if (trimmed.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    uncPath = Path.GetFullPath(trimmed);
                    return true;
                }

                string fullPath = Path.GetFullPath(trimmed);
                string? root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrWhiteSpace(root))
                    return false;

                string? remote = mappedRootResolver?.Invoke(root.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(remote)
                    || !remote.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    return false;
                }

                string relative = fullPath[root.Length..].TrimStart(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                uncPath = string.IsNullOrWhiteSpace(relative)
                    ? remote
                    : Path.Combine(remote, relative);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string? ResolveMappedRootWithWNet(string rootPath)
        {
            try
            {
                var buffer = new StringBuilder(512);
                int length = buffer.Capacity;
                uint result = WNetGetConnection(rootPath, buffer, ref length);
                return result == 0 ? buffer.ToString() : null;
            }
            catch
            {
                return null;
            }
        }

        private static long ClampToInt64(ulong value) =>
            value > long.MaxValue ? long.MaxValue : (long)value;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(
            string directoryName,
            out ulong freeBytesAvailableToCaller,
            out ulong totalNumberOfBytes,
            out ulong totalNumberOfFreeBytes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeNameForVolumeMountPoint(
            string lpszVolumeMountPoint,
            StringBuilder lpszVolumeName,
            uint cchBufferLength);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint WNetGetConnection(
            string lpLocalName,
            StringBuilder lpRemoteName,
            ref int lpnLength);
    }
}
