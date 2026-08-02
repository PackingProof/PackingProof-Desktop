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
        @"^v?(?<version>\d+\.\d+\.\d+)(?:\+(?<build>\d+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const int MaximumBuildManifestBytes = 64 * 1024;
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
        MinimumVersion: BackupCompatibilityPolicy.MinimumMobileVersion,
        MinimumBuildNumber: BackupCompatibilityPolicy.MinimumMobileBuildNumber,
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
        if (release.BuildNumber <= 0)
        {
            int buildNumber = await TryResolveBuildNumberAsync(
                json,
                release.Version,
                cancellationToken);
            if (buildNumber > 0)
                release = release with { BuildNumber = buildNumber };
        }
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
        if (!match.Success)
            throw new InvalidDataException("手机版最新版本格式无效");

        int buildNumber = 0;
        string buildText = match.Groups["build"].Value;
        if (buildText.Length > 0
            && (!int.TryParse(buildText, out buildNumber) || buildNumber <= 0))
        {
            throw new InvalidDataException("手机版最新版本格式无效");
        }

        string version = match.Groups["version"].Value;
        return new MobileAppReleaseInfo(
            tagName,
            version,
            buildNumber,
            ResolveDownloadUrl(document?.Assets));
    }

    private static async Task<int> TryResolveBuildNumberAsync(
        string releaseJson,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        string manifestUrl = ResolveBuildManifestUrl(releaseJson);
        if (manifestUrl.Length == 0)
            return 0;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
            request.Headers.UserAgent.ParseAdd("PackingProof-Desktop/1.0");
            using HttpResponseMessage response = await Client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength
                && contentLength > MaximumBuildManifestBytes)
            {
                throw new InvalidDataException("手机版构建清单过大");
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length > MaximumBuildManifestBytes)
                throw new InvalidDataException("手机版构建清单过大");

            return ParseBuildManifest(
                System.Text.Encoding.UTF8.GetString(buffer.ToArray()),
                expectedVersion);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RuntimeLog.Warn("MobileUpdate", "手机版构建清单读取超时，改用版本号比较");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or JsonException
            or InvalidDataException)
        {
            RuntimeLog.Warn("MobileUpdate", $"手机版构建清单读取失败，改用版本号比较：{ex.Message}");
            return 0;
        }
    }

    internal static string ResolveBuildManifestUrl(string releaseJson)
    {
        MobileAppReleaseDocument? document = JsonSerializer.Deserialize<MobileAppReleaseDocument>(
            releaseJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        MobileAppReleaseAsset? asset = document?.Assets.FirstOrDefault(candidate =>
            string.Equals(
                candidate.Name?.Trim(),
                "build-manifest.json",
                StringComparison.OrdinalIgnoreCase));
        return TryResolveTrustedGiteeUrl(asset?.BrowserDownloadUrl, out string url)
            ? url
            : "";
    }

    internal static int ParseBuildManifest(string json, string expectedVersion)
    {
        MobileAppBuildManifest? manifest = JsonSerializer.Deserialize<MobileAppBuildManifest>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest == null
            || manifest.VersionCode <= 0
            || !string.Equals(
                manifest.VersionName?.Trim(),
                expectedVersion?.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("手机版构建清单与 Release 版本不匹配");
        }

        return manifest.VersionCode;
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
            if (TryResolveTrustedGiteeUrl(asset.BrowserDownloadUrl, out string url))
                return url;
        }

        return ReleasesUrl;
    }

    private static bool TryResolveTrustedGiteeUrl(string? value, out string url)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "gitee.com", StringComparison.OrdinalIgnoreCase))
        {
            url = uri.AbsoluteUri;
            return true;
        }

        url = "";
        return false;
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

    private sealed class MobileAppBuildManifest
    {
        public string VersionName { get; set; } = "";
        public int VersionCode { get; set; }
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
