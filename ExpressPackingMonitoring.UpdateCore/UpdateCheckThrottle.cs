namespace ExpressPackingMonitoring.UpdateCore;

/// <summary>
/// 后台自动检查更新的节流判定：同一台机器、同一个版本、同一个更新渠道在窗口期内只查一次。
///
/// 渠道（是否接收预览版）必须参与判定，否则会出现现场那种情况：用户打开“接收预览版更新”，
/// 可上一次“已是最新”的结论是按正式渠道得出的，缓存把它挡住，最长 12 小时都收不到预览版。
/// 版本号变了（刚升级完）或没到窗口期结束都照常跳过。
/// </summary>
public static class UpdateCheckThrottle
{
    /// <summary>记录里的版本号与当前版本一致、且时间在窗口期内才跳过。</summary>
    public static bool ShouldSkip(
        string? stateVersion,
        string? checkedAtUtcText,
        string? currentVersion,
        bool stateAllowPrerelease,
        bool allowPrerelease,
        DateTimeOffset nowUtc,
        TimeSpan window)
    {
        // 渠道变了：上一次的结论不适用于现在这个渠道，必须重新查
        if (stateAllowPrerelease != allowPrerelease)
            return false;

        if (!string.Equals(
                Normalize(stateVersion),
                Normalize(currentVersion),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(checkedAtUtcText, out DateTimeOffset checkedAt))
            return false;
        if (checkedAt > nowUtc)
            return false; // 时钟被改过：宁可多查一次

        return nowUtc - checkedAt <= window;
    }

    private static string Normalize(string? value)
    {
        string normalized = value?.Trim() ?? "";
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[1..];
        int suffixIndex = normalized.IndexOfAny(['+', '-']);
        return suffixIndex >= 0 ? normalized[..suffixIndex] : normalized;
    }
}
