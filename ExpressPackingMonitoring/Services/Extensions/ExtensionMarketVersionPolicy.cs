using System.Linq;

namespace ExpressPackingMonitoring.Services.Extensions;

/// <summary>
/// 扩展版本口径：市场窗口和后台自动更新共用，避免两处各写一套比较规则。
/// 版本号统一去掉 v 前缀和预发布后缀再比较，"2.14" 与 "2.14.0" 视为同一个版本。
/// </summary>
internal static class ExtensionMarketVersionPolicy
{
    internal static string NormalizeVersion(string? value)
    {
        string normalized = (value ?? "").Trim().TrimStart('v', 'V').Split('-', '+')[0];
        string[] parts = normalized.Split('.');
        return parts.Length switch
        {
            1 => normalized + ".0.0",
            2 => normalized + ".0",
            _ => normalized
        };
    }

    /// <summary>市场要求的最低 PackingProof 版本与当前程序版本是否兼容。</summary>
    internal static bool IsAppCompatible(string? minimumVersion, string? appVersion)
    {
        if (!Version.TryParse(NormalizeVersion(minimumVersion), out Version? minimum)) return false;
        return Version.TryParse(NormalizeVersion(appVersion), out Version? current) && current >= minimum;
    }

    /// <summary>候选版本是否比已安装版本新。解析不出来就返回 false——宁可不动，也不要乱升级。</summary>
    internal static bool IsNewerVersion(string? candidate, string? installed)
    {
        if (!Version.TryParse(NormalizeVersion(candidate), out Version? candidateVersion)) return false;
        if (!Version.TryParse(NormalizeVersion(installed), out Version? installedVersion)) return false;
        return candidateVersion > installedVersion;
    }

    /// <summary>优先取市场标注的最新版，取不到就取第一个可用版本（与市场窗口的选法一致）。</summary>
    internal static ExtensionMarketRelease? SelectInstallableRelease(
        ExtensionMarketDetails? details,
        string? latestVersion)
    {
        if (details == null) return null;
        return details.Versions
                .FirstOrDefault(value => value.Status == "available"
                    && string.Equals(value.Release.Version, latestVersion, StringComparison.OrdinalIgnoreCase))?.Release
            ?? details.Versions.FirstOrDefault(value => value.Status == "available")?.Release;
    }
}
