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
///
/// 另一条同样重要的规则是渠道：草稿永远不参与挑选，预览版（prerelease）只有在调用方
/// 明确允许时才算 —— 见 <see cref="UpdateChannelPolicy"/>。
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
    /// 在 releases 列表里找出「**版本最高**、且带指定平台包」的那一个下标；找不到返回 -1。
    /// 响应不是数组时同样返回 -1。
    ///
    /// 不能按"数组里第一个匹配项"当最新：GitHub 的 releases 是新 → 旧，而 Gitee 是旧 → 新，
    /// 按位置挑在 Gitee 上会挑到列表里最旧的那一版 —— 现场就是这样：正式发到 v0.0.74，
    /// 应用内的手动检查却一直说"已是最新"。所以这里一律按 tag_name 的版本号比大小，
    /// 不看服务端返回顺序。
    /// </summary>
    /// <param name="allowPrerelease">
    /// true 时才把预览版算进候选；默认 false = 只认正式版。
    /// </param>
    public static int FindLatestWithAsset(
        JsonElement releases,
        Func<string, bool> assetPredicate,
        bool allowPrerelease = false)
    {
        ArgumentNullException.ThrowIfNull(assetPredicate);
        if (releases.ValueKind != JsonValueKind.Array) return -1;

        int bestIndex = -1;
        string bestVersion = "";
        int index = -1;
        foreach (JsonElement release in releases.EnumerateArray())
        {
            index++;
            if (!IsDeliverable(release, allowPrerelease)) continue;

            if (HasMatchingAsset(release, assetPredicate)
                && release.TryGetProperty("tag_name", out JsonElement tag)
                && tag.ValueKind == JsonValueKind.String)
            {
                string version = tag.GetString() ?? "";
                if (bestIndex < 0 || CompareVersions(version, bestVersion) > 0)
                {
                    bestIndex = index;
                    bestVersion = version;
                }
            }
        }

        return bestIndex;
    }

    /// <summary>
    /// 这个 release 能不能推给客户端：草稿永远不能（客户端连看都不该看到），
    /// 预览版只有调用方允许时才能。缺字段一律当正式版，避免误伤服务端不返回该字段的情况。
    /// </summary>
    internal static bool IsDeliverable(JsonElement release, bool allowPrerelease) =>
        !ReadBool(release, "draft") && (allowPrerelease || !ReadBool(release, "prerelease"));

    private static bool ReadBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// 比较两个 release 版本号（可带 v 前缀与 -/+ 后缀，与更新检查的口径一致）。
    /// 解析不出来时退回字符串比较，保证永远给得出一个确定的顺序。
    /// </summary>
    public static int CompareVersions(string? left, string? right)
    {
        string leftNormalized = NormalizeVersion(left);
        string rightNormalized = NormalizeVersion(right);

        if (Version.TryParse(leftNormalized, out Version? leftVersion)
            && Version.TryParse(rightNormalized, out Version? rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.CompareOrdinal(leftNormalized, rightNormalized);
    }

    private static string NormalizeVersion(string? value)
    {
        string normalized = value?.Trim() ?? "";
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[1..];

        int suffixIndex = normalized.IndexOfAny(['+', '-']);
        return suffixIndex >= 0 ? normalized[..suffixIndex] : normalized;
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
            // 资产名可能为空（JSON 里显式 null）：别把 null 传进判断回调。
            if (assetPredicate(name.GetString() ?? "")) return true;
        }

        return false;
    }
}
