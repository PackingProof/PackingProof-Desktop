using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Config;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ExpressPackingMonitoring.Services;

internal sealed class MobileAppUpdatePolicyProvider
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(5);
    private static readonly Regex VersionPattern = new(
        @"^v?(?<version>\d+\.\d+\.\d+)\+(?<build>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };
    private const string LatestReleaseApiUrl =
        "https://gitee.com/api/v5/repos/PackingProof/PackingProof-Mobile/releases/latest";
    internal const string ReleasesUrl =
        "https://gitee.com/PackingProof/PackingProof-Mobile/releases";

    private readonly object _gate = new();
    private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;
    private Task? _refreshTask;
    private MobileAppReleaseInfo? _latestRelease;

    internal static MobileAppUpdatePolicyProvider Shared { get; } = new();
    internal static MobileAppUpdatePolicy MinimumPolicy { get; } = new(
        SchemaVersion: 2,
        MinimumVersion: "0.5.6",
        MinimumBuildNumber: 11006,
        Message: "当前 APP 版本过低，需要更新");
    internal const string RepositoryUrl = "https://gitee.com/PackingProof/PackingProof-Mobile";

    internal MobileAppUpdatePolicyProvider()
    {
        _latestRelease = LoadCachedRelease();
    }

    internal MobileAppReleaseInfo? LatestRelease
    {
        get
        {
            lock (_gate)
                return _latestRelease;
        }
    }

    internal void RefreshInBackground()
    {
        lock (_gate)
        {
            TimeSpan minimumInterval = _latestRelease == null
                ? FailureRetryInterval
                : RefreshInterval;
            if (_refreshTask is { IsCompleted: false }
                || DateTimeOffset.UtcNow - _lastAttemptUtc < minimumInterval)
            {
                return;
            }

            _lastAttemptUtc = DateTimeOffset.UtcNow;
            _refreshTask = RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            await CheckLatestAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or InvalidDataException)
        {
            RuntimeLog.Warn("MobileUpdate", $"手机版本检查失败：{ex.Message}");
        }
    }

    internal async Task<MobileAppReleaseInfo> CheckLatestAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        request.Headers.UserAgent.ParseAdd("PackingProof-Desktop/1.0");
        using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        MobileAppReleaseInfo release = ParseLatestRelease(json);
        lock (_gate)
            _latestRelease = release;
        SaveCachedRelease(release);
        return release;
    }

    internal static MobileAppReleaseInfo ParseLatestRelease(string json)
    {
        MobileAppReleaseDocument? document = JsonSerializer.Deserialize<MobileAppReleaseDocument>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        string tagName = document?.TagName?.Trim() ?? "";
        Match match = VersionPattern.Match(tagName);
        if (!match.Success
            || !int.TryParse(match.Groups["build"].Value, out int buildNumber)
            || buildNumber <= 0)
            throw new InvalidDataException("手机版最新版本格式无效");

        string version = match.Groups["version"].Value;
        return new MobileAppReleaseInfo(
            tagName,
            version,
            buildNumber,
            ResolveDownloadUrl(document?.Assets));
    }

    private static string ResolveDownloadUrl(IReadOnlyList<MobileAppReleaseAsset>? assets)
    {
        if (assets == null || assets.Count == 0)
            return ReleasesUrl;

        IEnumerable<MobileAppReleaseAsset> candidates = assets
            .OrderByDescending(asset => string.Equals(
                asset.Name?.Trim(),
                "PackingProof-Mobile.apk",
                StringComparison.OrdinalIgnoreCase))
            .Where(asset => asset.Name?.Trim().EndsWith(
                ".apk",
                StringComparison.OrdinalIgnoreCase) == true);

        foreach (MobileAppReleaseAsset asset in candidates)
        {
            if (Uri.TryCreate(asset.BrowserDownloadUrl?.Trim(), UriKind.Absolute, out Uri? uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(uri.Host, "gitee.com", StringComparison.OrdinalIgnoreCase))
            {
                return uri.AbsoluteUri;
            }
        }

        return ReleasesUrl;
    }

    internal static bool IsUpdateAvailable(int currentBuildNumber, MobileAppReleaseInfo latestRelease)
    {
        return latestRelease != null
            && (currentBuildNumber <= 0
                || currentBuildNumber < latestRelease.BuildNumber);
    }

    private sealed class MobileAppReleaseDocument
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("assets")]
        public List<MobileAppReleaseAsset> Assets { get; set; } = [];
    }

    private sealed class MobileAppReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }

    private static MobileAppReleaseInfo? LoadCachedRelease()
    {
        try
        {
            if (!File.Exists(AppPaths.MobileAppUpdateCachePath))
                return null;
            return JsonSerializer.Deserialize<MobileAppReleaseInfo>(
                File.ReadAllText(AppPaths.MobileAppUpdateCachePath));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCachedRelease(MobileAppReleaseInfo release)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.MobileAppUpdateCachePath)!);
            File.WriteAllText(
                AppPaths.MobileAppUpdateCachePath,
                JsonSerializer.Serialize(release));
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("MobileUpdate", $"手机版本缓存保存失败：{ex.Message}");
        }
    }

}

internal sealed record MobileAppUpdatePolicy(
    int SchemaVersion,
    string MinimumVersion,
    int MinimumBuildNumber,
    string Message);

internal sealed record MobileAppReleaseInfo(
    string TagName,
    string Version,
    int BuildNumber,
    string DownloadUrl);

internal sealed record MobileAppUpdateAvailableInfo(
    string DeviceName,
    string CurrentVersion,
    int CurrentBuildNumber,
    MobileAppReleaseInfo LatestRelease);
