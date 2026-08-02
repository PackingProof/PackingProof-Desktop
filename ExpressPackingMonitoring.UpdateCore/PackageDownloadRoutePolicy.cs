namespace ExpressPackingMonitoring.UpdateCore;

public sealed record PackageDownloadRoute(
    string GithubUrl,
    string GiteeUrl,
    string SelectedUrl,
    bool PreferGitee);

public static class PackageDownloadRoutePolicy
{
    public static PackageDownloadRoute Resolve(
        string? githubUrl,
        string? giteeUrl,
        string? legacyUrl,
        string? derivedGithubUrl,
        int consecutiveGithubFailures,
        int fallbackThreshold)
    {
        string github = FirstValid(githubUrl, derivedGithubUrl);
        string gitee = FirstValid(giteeUrl, legacyUrl);
        if (github.Length == 0)
            github = gitee;
        if (gitee.Length == 0)
            gitee = github;
        if (github.Length == 0)
            throw new InvalidOperationException("更新包没有可用的 HTTPS 下载地址");

        bool preferGitee = Math.Max(0, consecutiveGithubFailures) >= Math.Max(1, fallbackThreshold)
            && !string.Equals(github, gitee, StringComparison.OrdinalIgnoreCase);
        return new PackageDownloadRoute(
            github,
            gitee,
            preferGitee ? gitee : github,
            preferGitee);
    }

    private static string FirstValid(params string?[] candidates)
    {
        return candidates
            .Select(candidate => candidate?.Trim() ?? "")
            .FirstOrDefault(UpdateEndpointPolicy.IsSecureAbsoluteUrl)
            ?? "";
    }
}
