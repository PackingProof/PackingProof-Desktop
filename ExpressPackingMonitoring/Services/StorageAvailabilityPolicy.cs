namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 判断录像所在的存储位置当前是不是"确实访问不了"。
///
/// 这里的口径必须比写入时更保守：写入时读不到存储位置就直接拒收，最多是不收；
/// 但对外宣称"磁盘没接入"是在告诉用户录像还在、接回来就行，说错会让人以为数据安全。
/// 因此只有配置的存储位置与它的上级目录都访问不到（盘被拔掉、盘符消失）才算不可用；
/// 存储位置还在、只是文件没了，一律按文件丢失处理。
/// </summary>
internal static class StorageAvailabilityPolicy
{
    /// <summary>存储位置当前是否访问不了（盘不在了）。</summary>
    internal static bool IsLocationUnavailable(string? locationPath)
        => IsLocationUnavailable(locationPath, Directory.Exists);

    /// <summary>供测试注入目录探测。</summary>
    internal static bool IsLocationUnavailable(string? locationPath, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        if (string.IsNullOrWhiteSpace(locationPath)) return false;

        try
        {
            string trimmed = locationPath.Trim();
            if (!Path.IsPathRooted(trimmed)) return false; // 相对路径无从判断，按可用处理
            string fullPath = Path.GetFullPath(trimmed);
            // 只看卷根：D:\ 或 /Volumes/xxx 不在了，才是盘被拔掉/盘符消失。
            // 存储位置目录本身被删而卷还在，属于文件丢失，不能对外说磁盘没接入。
            string? root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrWhiteSpace(root) && !directoryExists(root);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>录像文件是否落在某个当前访问不了的存储位置内。</summary>
    internal static bool IsUnderUnavailableLocation(
        string? filePath,
        IReadOnlyList<string> unavailableLocations)
    {
        if (string.IsNullOrWhiteSpace(filePath) || unavailableLocations.Count == 0) return false;

        string normalizedFile = NormalizePathKey(filePath);
        if (normalizedFile.Length == 0) return false;

        foreach (string location in unavailableLocations)
        {
            string normalizedLocation = NormalizePathKey(location);
            if (normalizedLocation.Length == 0) continue;
            if (string.Equals(normalizedFile, normalizedLocation, StringComparison.Ordinal)
                || normalizedFile.StartsWith(normalizedLocation + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizePathKey(string path)
    {
        try
        {
            return path.Trim()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/')
                .ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }
}
