namespace ExpressPackingMonitoring.UpdateCore;

public static class UpdateEndpointPolicy
{
    public const string DefaultGiteeCheckUrl =
        "https://gitee.com/api/v5/repos/chenjjian/ExpressPackingMonitoring/releases/latest";
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
}
