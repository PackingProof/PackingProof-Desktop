using System.Text.Json;

namespace ExpressPackingMonitoring.UpdateCore;

/// <summary>
/// 按平台挑"最新版本"。
///
/// 版本号是整个项目共用的，但发布不总是两个平台一起发：有的版本只修 Windows（没有 DMG），
/// 有的版本只发 macOS（没有更新清单、没有 Setup）。只看最新 tag 的话，两端都会被提示去装
/// 一个跟自己无关的版本：Mac 会下载 DMG 之外的包，Windows 会提示有新版本却下到 DMG、
/// 启动器自动更新还会因为"Release 缺少 update_v*.json"一直失败。
/// 所以两端都只认"版本最高、且确实带本平台发布资产"的那一个。
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
    /// 桌面端（Windows）的更新清单资产：<c>update_v&lt;版本&gt;.json</c>。
    /// 发布流程里 Windows 版本必定带它（GitHub 与 Gitee 都传），只发 macOS 的版本没有它，
    /// 所以它既能代表"这个版本有 Windows 更新"，也是启动器判断能不能更新所需的资产。
    /// </summary>
    public static bool IsUpdateManifest(string? assetName)
    {
        string name = (assetName ?? "").Trim();
        return name.StartsWith("update_v", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>本平台判定"版本带本平台发布资产"的谓词：macOS 认 DMG，其它平台认更新清单。</summary>
    public static Func<string, bool> PlatformAssetPredicate(bool isMacOs) =>
        isMacOs ? IsMacOsPackage : IsUpdateManifest;

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
