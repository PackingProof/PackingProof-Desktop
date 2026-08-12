using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
        /// <summary>存储位置类型：明确本地 / 明确网络 / 无法确认（fail-closed 按不可用处理）。</summary>
        internal enum StorageLocationKind
        {
            Local,
            Network,
            Unknown
        }

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
        public static bool IsNetworkPath(string path) =>
            ClassifyStorageLocation(path) == StorageLocationKind.Network;

        /// <summary>
        /// 三态判定存储位置类型（不缓存，每次现算）：
        /// UNC/映射盘/网络共享挂载点 → Network；明确本地卷 → Local；无法确认 → Unknown。
        /// </summary>
        public static StorageLocationKind ClassifyStorageLocation(string path) =>
            ClassifyStorageLocation(path, ResolveFinalPathOfNearestExistingAncestor);

        /// <summary>供测试注入 finalPathResolver 的三态判定重载。</summary>
        internal static StorageLocationKind ClassifyStorageLocation(
            string path,
            Func<string, string?>? finalPathResolver)
        {
            if (string.IsNullOrWhiteSpace(path))
                return StorageLocationKind.Unknown;

            try
            {
                string fullPath = Path.GetFullPath(path.Trim().Trim('"'));
                string? root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrWhiteSpace(root))
                    return StorageLocationKind.Unknown;
                if (root.StartsWith(@"\\", StringComparison.Ordinal))
                    return StorageLocationKind.Network;

                try
                {
                    if (new DriveInfo(root).DriveType == DriveType.Network)
                        return StorageLocationKind.Network;
                    if (!string.IsNullOrWhiteSpace(ResolveMappedRootWithWNet(
                            root.TrimEnd(
                                Path.DirectorySeparatorChar,
                                Path.AltDirectorySeparatorChar))))
                    {
                        return StorageLocationKind.Network;
                    }
                }
                catch
                {
                    // 继续按 final path 判定
                }

                string? final = finalPathResolver?.Invoke(fullPath);
                return ClassifyFinalPath(final);
            }
            catch
            {
                return StorageLocationKind.Unknown;
            }
        }

        /// <summary>是否明确是本地存储位置（主存储 fail-closed 的唯一放行条件）。</summary>
        public static bool IsConfirmedLocal(string path) =>
            ClassifyStorageLocation(path) == StorageLocationKind.Local;

        /// <summary>
        /// 规范化 final path 前缀后判定：
        /// \\?\UNC\... 与 \\server\share 为网络；\\?\C:\ 等去掉 \\?\ 后为本地；
        /// 卷 GUID 路径（\\?\Volume{...}）为本地；其他无法识别形态按 Unknown fail-closed。
        /// </summary>
        private static StorageLocationKind ClassifyFinalPath(string? finalPath)
        {
            if (string.IsNullOrWhiteSpace(finalPath))
                return StorageLocationKind.Unknown;
            string normalized = finalPath.Trim();
            if (normalized.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                return StorageLocationKind.Network;
            if (normalized.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            {
                string stripped = normalized[4..];
                if (stripped.StartsWith("Volume", StringComparison.OrdinalIgnoreCase))
                    return StorageLocationKind.Local;
                if (stripped.Length >= 2
                    && char.IsLetter(stripped[0])
                    && stripped[1] == ':')
                {
                    return StorageLocationKind.Local;
                }
                return StorageLocationKind.Unknown;
            }
            if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
                return StorageLocationKind.Network;
            return StorageLocationKind.Local;
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
                    bool isNetwork = IsNetworkPath(normalized);
                    if (!isNetwork && mappedRootResolver != null)
                    {
                        // 测试注入的解析器能把盘符根解析为 UNC 时，同样按网络路径处理，
                        // 避免测试依赖真实机器上是否映射了网络盘。
                        isNetwork = TryResolveUncPath(
                            normalized,
                            out _,
                            mappedRootResolver);
                    }
                    if (!isNetwork)
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

        private const uint FileReadAttributes = 0x80;
        private const uint ShareReadWriteDelete = 1 | 2 | 4;
        private const uint OpenExisting = 3;
        private const uint FlagBackupSemantics = 0x02000000;

        /// <summary>解析路径的真实最终路径（穿透目录挂载点/连接点），失败返回 null。</summary>
        private static string? ResolveFinalPath(string path)
        {
            try
            {
                using SafeFileHandle handle = CreateFile(
                    path,
                    FileReadAttributes,
                    ShareReadWriteDelete,
                    IntPtr.Zero,
                    OpenExisting,
                    FlagBackupSemantics,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                    return null;

                var buffer = new StringBuilder(1024);
                uint length = GetFinalPathNameByHandle(
                    handle,
                    buffer,
                    (uint)buffer.Capacity,
                    0);
                if (length == 0 || length >= buffer.Capacity)
                    return null;
                return buffer.ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 解析“路径本身或最近存在的父目录”的真实最终路径：
        /// 尚未创建的本地目录会向上解析到已存在的父目录，从而正确识别父目录是否位于网络挂载点上。
        /// </summary>
        private static string? ResolveFinalPathOfNearestExistingAncestor(string path)
        {
            string? current = path;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (Directory.Exists(current))
                    return ResolveFinalPath(current);
                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent)
                    || string.Equals(
                        parent,
                        current,
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = parent;
            }
            return null;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle hFile,
            StringBuilder lpszFilePath,
            uint cchFilePath,
            uint dwFlags);
    }
}
