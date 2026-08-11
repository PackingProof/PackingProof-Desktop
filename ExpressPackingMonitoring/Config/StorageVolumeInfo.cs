using System;
using System.IO;
using System.Net;
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

        /// <summary>
        /// 归一化网络位置的“共享标识”（\\服务器\共享，小写，忽略子目录），
        /// 用于识别同一磁盘通过 UNC、映射盘、主机名/IP 等不同写法重复添加。
        /// </summary>
        public static bool TryGetNetworkShareIdentity(
            string path,
            out string identity) =>
            TryGetNetworkShareIdentity(
                path,
                out identity,
                mappedRootResolver: null,
                hostResolver: null);

        internal static bool TryGetNetworkShareIdentity(
            string path,
            out string identity,
            Func<string, string?>? mappedRootResolver,
            Func<string, string?>? hostResolver)
        {
            identity = "";
            try
            {
                string normalized = path?.Trim().Trim('"') ?? "";
                if (string.IsNullOrWhiteSpace(normalized))
                    return false;
                if (!normalized.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    if (!IsNetworkPath(normalized))
                        return false;
                    if (!TryResolveUncPath(normalized, out string unc, mappedRootResolver))
                        return false;
                    normalized = unc;
                }

                string trimmed = normalized.TrimStart('\\');
                int firstSeparator = trimmed.IndexOf('\\');
                if (firstSeparator <= 0)
                    return false; // 需要“服务器\共享”两层
                string server = trimmed[..firstSeparator];
                string rest = trimmed[(firstSeparator + 1)..];
                int shareEnd = rest.IndexOf('\\');
                string share = shareEnd < 0 ? rest : rest[..shareEnd];
                if (string.IsNullOrWhiteSpace(share))
                    return false;

                identity = @"\\"
                    + NormalizeServerHost(server, hostResolver)
                    + @"\"
                    + share.ToLowerInvariant();
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

        /// <summary>
        /// 归一化服务器主机：IP 原样保留，localhost/127.0.0.1/::1 统一为 localhost；
        /// 主机名尽力解析为 IP（2 秒超时，失败回退文本），多 IP 取第一个。
        /// </summary>
        private static string NormalizeServerHost(
            string server,
            Func<string, string?>? hostResolver)
        {
            string host = server.Trim().ToLowerInvariant();
            if (host.Length == 0)
                return host;
            if (host is "localhost" or "127.0.0.1" or "::1")
                return "localhost";
            if (IPAddress.TryParse(host, out _))
                return host;

            try
            {
                string resolved;
                if (hostResolver != null)
                {
                    string? custom = hostResolver(host);
                    if (string.IsNullOrWhiteSpace(custom))
                        return host;
                    resolved = custom.Trim().ToLowerInvariant();
                }
                else
                {
                    Task<IPAddress[]> lookup = Task.Run(
                        () => Dns.GetHostAddresses(host));
                    if (!lookup.Wait(TimeSpan.FromSeconds(2))
                        || lookup.Result.Length == 0)
                    {
                        return host;
                    }
                    resolved = lookup.Result[0].ToString().ToLowerInvariant();
                }
                return resolved is "127.0.0.1" or "::1"
                    ? "localhost"
                    : resolved;
            }
            catch
            {
                return host;
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
