namespace ExpressPackingMonitoring.UpdateCore;

/// <summary>
/// 更新渠道策略：正式版与预览版（prerelease）。
///
/// 预览版是内部测试渠道：标了 prerelease 的 release 默认不推给任何客户端，
/// 只有把环境变量 <see cref="AllowPrereleaseKey"/> 设成 1/true/yes/on 的机器（测试机）
/// 才会照常收到。以前"标成预览版"只是个标记，客户端照样会自动更新到它，
/// 名字和实际行为是反的 —— 对打包台这种一升级就是全店铺铺开的场景太危险。
///
/// 草稿（draft）不在此列：草稿永远不推，客户端连看都不该看到。
/// </summary>
public static class UpdateChannelPolicy
{
    /// <summary>允许接收预览版的开关。不设置（或设成 0/false/no/off）时只认正式版。</summary>
    public const string AllowPrereleaseKey = "UPDATE_ALLOW_PRERELEASE";

    /// <summary>测试机用：从环境变量读"这台机器要不要收预览版"。</summary>
    public static bool AllowPrereleaseFromEnvironment() =>
        ParseToggle(Environment.GetEnvironmentVariable(AllowPrereleaseKey));

    /// <summary>
    /// 开关取值解析：1/true/yes/on（忽略大小写与首尾空格）为真，其它一律为假。
    /// 单独抽出来是为了可测：写错一个值就等于悄悄把预览版推给了所有店铺。
    /// </summary>
    public static bool ParseToggle(string? value)
    {
        string normalized = value?.Trim() ?? "";
        return normalized.Equals("1", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}
