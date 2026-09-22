namespace ExpressPackingMonitoring.UpdateCore;

public static class UpdateEndpointPolicy
{
    public const string DefaultGiteeCheckUrl =
        "https://gitee.com/api/v5/repos/PackingProof/PackingProof-Desktop/releases/latest";
    public const string DefaultGithubCheckUrl =
        "https://api.github.com/repos/PackingProof/PackingProof-Desktop/releases/latest";

    public static IReadOnlyList<string> ResolveCheckUrls(
        string? configuredPrimary,
        string? configuredFallback)
    {
        string primary = configuredPrimary?.Trim() ?? "";
        string fallback = configuredFallback?.Trim() ?? "";
        if (primary.Length == 0)
            primary = DefaultGiteeCheckUrl;

        if (fallback.Length == 0)
        {
            if (string.Equals(primary, DefaultGiteeCheckUrl, StringComparison.OrdinalIgnoreCase))
                fallback = DefaultGithubCheckUrl;
            else if (string.Equals(primary, DefaultGithubCheckUrl, StringComparison.OrdinalIgnoreCase))
                fallback = DefaultGiteeCheckUrl;
        }

        return new[] { primary, fallback }
            .Where(IsSecureAbsoluteUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsSecureAbsoluteUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback);
    }

    /// <summary>
    /// 把"最新版本"检查地址换成"release 列表"地址：按平台挑版本时要看整份列表，
    /// 而不是只看最新那一个（有的版本只发了另一个平台）。
    /// 认不出的地址原样拼成 /releases?per_page=30，做不到时调用方退回原来的单版本检查。
    /// </summary>
    public static IReadOnlyList<string> ToReleaseListUrls(IReadOnlyList<string> checkUrls)
    {
        var list = new List<string>();
        foreach (string raw in checkUrls)
        {
            string url = raw?.Trim() ?? "";
            if (url.Length == 0) continue;

            string trimmed = url.TrimEnd('/');
            const string latestSuffix = "/latest";
            if (trimmed.EndsWith(latestSuffix, StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[..^latestSuffix.Length];
            if (!trimmed.EndsWith("/releases", StringComparison.OrdinalIgnoreCase))
                trimmed += "/releases";

            string withPage = trimmed + "?per_page=30";
            if (!list.Contains(withPage, StringComparer.OrdinalIgnoreCase))
                list.Add(withPage);
        }

        return list;
    }
}
