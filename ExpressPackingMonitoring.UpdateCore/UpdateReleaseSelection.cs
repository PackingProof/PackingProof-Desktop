using System.Text.Json;

namespace ExpressPackingMonitoring.UpdateCore;

/// <summary>
/// 按平台挑"最新版本"。
///
/// 版本号是整个项目共用的，但发布不总是两个平台一起发：有的版本只修 Windows，
/// 根本没出 macOS 包。如果只看最新 tag，Mac 端会被提示去装一个跟自己无关的版本。
/// 所以 macOS 要挑"版本最高、且确实带 macOS 包"的那一个，Windows 保持原行为。
/// </summary>
public static class UpdateReleaseSelection
{
    /// <summary>macOS 分发包：release 里上传的 DMG。</summary>
    public static bool IsMacOsPackage(string? assetName)
    {
        string name = (assetName ?? "").Trim();
        if (name.Length == 0) return false;
        if (!name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase)) return false;
        return name.Contains("macos", StringComparison.OrdinalIgnoreCase)
            || name.Contains("mac-", StringComparison.OrdinalIgnoreCase)
            || name.Contains("mac_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在 releases 列表（GitHub/Gitee 的数组响应，已按新到旧排列）里找出第一个
    /// 带指定平台包的 release 下标；找不到返回 -1。响应不是数组时同样返回 -1。
    /// </summary>
    public static int FindLatestWithAsset(JsonElement releases, Func<string, bool> assetPredicate)
    {
        ArgumentNullException.ThrowIfNull(assetPredicate);
        if (releases.ValueKind != JsonValueKind.Array) return -1;

        int index = 0;
        foreach (JsonElement release in releases.EnumerateArray())
        {
            if (HasMatchingAsset(release, assetPredicate)) return index;
            index++;
        }

        return -1;
    }

    private static bool HasMatchingAsset(JsonElement release, Func<string, bool> assetPredicate)
    {
        if (!release.TryGetProperty("assets", out JsonElement assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out JsonElement name)) continue;
            if (assetPredicate(name.GetString())) return true;
        }

        return false;
    }
}
