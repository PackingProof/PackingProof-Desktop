using System.Text.Json;

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

    /// <summary>应用设置里"接收预览版更新"的字段名，与 AppConfig.AllowPrereleaseUpdates 一致。</summary>
    public const string AppConfigPropertyName = "AllowPrereleaseUpdates";

    private const string UserDataDirectoryOverrideKey = "EPM_USER_DATA_DIR";
    private const string UserDataDirectoryName = "ExpressPackingMonitoring";
    private const string AppConfigFileName = "config.json";

    /// <summary>
    /// 本机是否接收预览版：环境变量优先（测试机临时打开/关闭都方便），
    /// 环境变量没设置时才看应用设置里的"接收预览版更新"。
    /// 应用内检查更新、启动器自动更新、启动器包更新都用这一个判定。
    /// </summary>
    public static bool AllowPrerelease() =>
        ResolveAllowPrerelease(
            Environment.GetEnvironmentVariable(AllowPrereleaseKey),
            AllowPrereleaseFromAppConfig());

    /// <summary>测试机用：从环境变量读"这台机器要不要收预览版"。</summary>
    public static bool AllowPrereleaseFromEnvironment() =>
        ParseToggle(Environment.GetEnvironmentVariable(AllowPrereleaseKey));

    /// <summary>
    /// 优先级判定：环境变量认得出就用它（0/false/off/no 是明确的"关"），
    /// 认不出（没设置或写了个没意义的值）才回退到应用设置。
    /// </summary>
    public static bool ResolveAllowPrerelease(string? environmentValue, bool appConfigValue) =>
        TryParseToggle(environmentValue, out bool fromEnvironment) ? fromEnvironment : appConfigValue;

    /// <summary>从应用 config.json 的 "AllowPrereleaseUpdates" 读开关；读不到或配置损坏都当关闭。</summary>
    public static bool AllowPrereleaseFromAppConfig(string? configPath = null) =>
        ReadAppConfigToggle(
            configPath ?? ResolveAppConfigPath(),
            AppConfigPropertyName,
            fallback: false);

    /// <summary>应用运行数据目录，与 AppPaths / 启动器的计算保持一致（含 EPM_USER_DATA_DIR 覆盖）。</summary>
    public static string ResolveUserDataDirectory()
    {
        string? overridePath = Environment.GetEnvironmentVariable(UserDataDirectoryOverrideKey);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string root = string.IsNullOrWhiteSpace(localAppData) ? AppContext.BaseDirectory : localAppData;
        return Path.Combine(root, UserDataDirectoryName);
    }

    /// <summary>应用配置文件的完整路径。</summary>
    public static string ResolveAppConfigPath() =>
        Path.Combine(ResolveUserDataDirectory(), AppConfigFileName);

    /// <summary>
    /// 从应用配置里读一个布尔开关。只认 JSON 里的 true/false；文件缺失、损坏或字段不是布尔时
    /// 一律返回 <paramref name="fallback"/>。启动器读 EnableAutoCheckUpdate 也走这里，两处口径一致。
    /// </summary>
    public static bool ReadAppConfigToggle(
        string configPath,
        string propertyName,
        bool fallback,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            return fallback;

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(configPath, System.Text.Encoding.UTF8));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(propertyName, out JsonElement value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"读取 {propertyName} 失败：{ex.Message}");
        }

        return fallback;
    }

    /// <summary>
    /// 开关取值解析：1/true/yes/on（忽略大小写与首尾空格）为真，其它一律为假。
    /// 单独抽出来是为了可测：写错一个值就等于悄悄把预览版推给了所有店铺。
    /// </summary>
    public static bool ParseToggle(string? value) => TryParseToggle(value, out bool result) && result;

    /// <summary>
    /// 三态解析：认得出就返回 true 并把结果写进 <paramref name="result"/>，
    /// 认不出（空值、没意义的值）返回 false，交给调用方决定回退。
    /// </summary>
    internal static bool TryParseToggle(string? value, out bool result)
    {
        string normalized = value?.Trim() ?? "";
        switch (normalized.ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                result = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
