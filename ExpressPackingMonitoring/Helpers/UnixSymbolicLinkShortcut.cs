using System.IO;

namespace ExpressPackingMonitoring.Helpers;

/// <summary>
/// macOS / Linux 下的"快捷方式"：指向设备目录的符号链接。
///
/// Windows 用 .lnk（见 <see cref="WindowsShellShortcut"/>）；macOS 没有 .lnk，Finder 把符号链接
/// 显示成带箭头的替身，双击进去就是目标目录，对用户是同一个东西，所以这里用符号链接顶上。
///
/// 与 .lnk 一致的两条约束：建不出来（只读的 NAS、权限不足）只返回原因，绝不影响录像与备份；
/// 只删自己建过的链接，不碰用户的文件与目录。注意指向目录的符号链接在 <c>File.Exists</c> 下是
/// false（悬空时反过来是 true），所以存在性与目标都必须走这里的方法判断。
/// </summary>
internal static class UnixSymbolicLinkShortcut
{
    /// <summary>建一个指向目录的符号链接；已存在的链接会被替换（只删链接，不动目标）。</summary>
    internal static bool TryCreate(string linkPath, string targetPath, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(linkPath) || string.IsNullOrWhiteSpace(targetPath))
        {
            error = "链接路径或目标为空";
            return false;
        }

        try
        {
            string? directory = Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            if (IsLink(linkPath))
            {
                // 删链接不等于删目标：这里只把旧链接摘掉
                File.Delete(linkPath);
            }
            else if (File.Exists(linkPath) || Directory.Exists(linkPath))
            {
                // 用户自己放的同名文件或目录：不覆盖，让上层记一次日志
                error = "同名文件或目录已存在，且不是本程序建的链接";
                return false;
            }

            Directory.CreateSymbolicLink(linkPath, targetPath);
            if (IsLink(linkPath))
                return true;

            error = "创建之后链接仍然不存在";
            return false;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>读链接指向的目标；不是链接或读不到时返回 null。</summary>
    internal static string? TryReadTarget(string linkPath)
    {
        if (string.IsNullOrWhiteSpace(linkPath))
            return null;

        try
        {
            string? target = new DirectoryInfo(linkPath).LinkTarget;
            if (!string.IsNullOrEmpty(target))
                return target;

            return new FileInfo(linkPath).LinkTarget;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>链接条目是否存在（包含目标已被删掉的悬空链接）。</summary>
    internal static bool Exists(string linkPath) =>
        !string.IsNullOrWhiteSpace(linkPath)
        && (File.Exists(linkPath) || Directory.Exists(linkPath) || IsLink(linkPath));

    /// <summary>
    /// 目录里的所有符号链接。默认枚举会把指向目录的链接当成普通子目录返回、还会递归进去，
    /// 所以清理必须显式枚举条目再按 LinkTarget 过滤。
    /// </summary>
    internal static IEnumerable<string> Enumerate(string directoryPath)
    {
        var directory = new DirectoryInfo(directoryPath);
        if (!directory.Exists)
            return [];

        return directory
            .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly)
            .Where(entry => entry.LinkTarget != null)
            .Select(entry => entry.FullName)
            .ToArray();
    }

    private static bool IsLink(string linkPath)
    {
        if (string.IsNullOrWhiteSpace(linkPath))
            return false;

        try
        {
            return new DirectoryInfo(linkPath).LinkTarget != null
                || new FileInfo(linkPath).LinkTarget != null;
        }
        catch
        {
            return false;
        }
    }
}
