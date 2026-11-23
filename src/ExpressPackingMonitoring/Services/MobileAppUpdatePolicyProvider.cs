using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.UpdateCore;
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
    private static readonly TimeSpan InFlightRecheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinimumScheduleDelay = TimeSpan.FromSeconds(5);
    private static readonly Regex VersionPattern = new(
        @"^v?(?<version>\d+\.\d+\.\d+)(?:\+(?<build>\d+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const int MaximumBuildManifestBytes = 64 * 1024;
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };
    private static readonly string LatestReleaseApiUrl = BrandMetadata.MobileLatestReleaseApiUrl;
    private static readonly string ReleasesListApiUrl = BrandMetadata.MobileReleasesApiUrl;
    private const string IosLatestManifestName = "ios-latest.json";
    private const int ReleaseListPageSize = 100;
    private const int MaximumReleaseListPages = 20;
    internal static string ReleasesUrl => BrandMetadata.MobileReleasesUrl;
    internal const string TestFlightJoinUrl =
        "https://testflight.apple.com/join/KR4qNs6t";

    private readonly object _gate = new();
    private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;
    private bool _lastAttemptFailed;
    private Task? _refreshTask;
    private MobileAppReleaseInfo? _latestRelease;
    private MobileAppReleaseInfo? _latestIosRelease;

    internal static MobileAppUpdatePolicyProvider Shared { get; } = new();
    internal static MobileAppUpdatePolicy MinimumPolicy { get; } = new(
        SchemaVersion: 2,
        MinimumVersion: BackupCompatibilityPolicy.MinimumMobileVersion,
        MinimumBuildNumber: BackupCompatibilityPolicy.MinimumMobileBuildNumber,
        Message: "当前 APP 版本过低，需要更新");
    internal static string RepositoryUrl => BrandMetadata.MobileRepositoryUrl;

    internal MobileAppUpdatePolicyProvider()
    {
        _latestRelease = LoadCachedRelease(AppPaths.MobileAppUpdateCachePath);
        _latestIosRelease = LoadCachedRelease(AppPaths.MobileIosAppUpdateCachePath);
    }

    internal MobileAppReleaseInfo? LatestRelease
    {
        get
        {
            lock (_gate)
                return _latestRelease;
        }
    }

    internal MobileAppReleaseInfo? LatestIosRelease
    {
        get
        {
            lock (_gate)
                return _latestIosRelease;
        }
    }

    internal Task? RefreshInBackground()
    {
        if (!ProductCapabilityRuntime.CurrentFeatures.MobileApp)
            return null;

        lock (_gate)
        {
            if (_refreshTask is { IsCompleted: false }
                || DateTimeOffset.UtcNow - _lastAttemptUtc < ResolveRefreshIntervalLocked())
            {
                return null;
            }

            _lastAttemptUtc = DateTimeOffset.UtcNow;
            _refreshTask = RefreshAsync();
            return _refreshTask;
        }
    }

    private TimeSpan ResolveRefreshIntervalLocked() =>
        _lastAttemptFailed || _latestRelease == null
            ? FailureRetryInterval
            : RefreshInterval;

    /// <summary>
    /// 距离下一次允许检查的等待时间。调用方据此安排单次计时，不再用固定 15 秒轮询空转。
    /// </summary>
    internal TimeSpan GetTimeUntilNextRefresh()
    {
        lock (_gate)
        {
            if (_refreshTask is { IsCompleted: false })
                return InFlightRecheckInterval;
            return ComputeRefreshDelay(
                DateTimeOffset.UtcNow,
                _lastAttemptUtc,
                _latestRelease != null || _latestIosRelease != null,
                _lastAttemptFailed);
        }
    }

    internal static TimeSpan ComputeRefreshDelay(
        DateTimeOffset now,
        DateTimeOffset lastAttemptUtc,
        bool hasCachedRelease,
        bool lastAttemptFailed)
    {
        TimeSpan interval = lastAttemptFailed || !hasCachedRelease
            ? FailureRetryInterval
            : RefreshInterval;
        TimeSpan delay = lastAttemptUtc + interval - now;
        return delay > MinimumScheduleDelay ? delay : MinimumScheduleDelay;
    }

    private async Task RefreshAsync()
    {
        bool androidSucceeded = false;
        bool iosSucceeded = false;
        try
        {
            await CheckLatestAsync();
            androidSucceeded = true;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or InvalidDataException)
        {
            RuntimeLog.Warn("MobileUpdate", $"手机版本检查失败：{ex.Message}");
        }
        try
        {
            await CheckLatestIosAsync();
            iosSucceeded = true;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException
            or JsonException
            or InvalidDataException)
        {
            RuntimeLog.Warn("MobileUpdate", $"iOS 手机版本检查失败：{ex.Message}");
        }
        finally
        {
            lock (_gate)
                _lastAttemptFailed = !androidSucceeded || !iosSucceeded;
        }
    }

    internal async Task<MobileAppReleaseInfo> CheckLatestAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        request.Headers.UserAgent.ParseAdd(BrandMetadata.ArtifactPrefix + "-Desktop/1.0");
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
        SaveCachedRelease(AppPaths.MobileAppUpdateCachePath, release);
        return release;
    }

    internal async Task<MobileAppReleaseInfo?> CheckLatestIosAsync(
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<(string TagName, string ManifestUrl)>();
        for (int page = 1; page <= MaximumReleaseListPages; page++)
        {
            string url =
                $"{ReleasesListApiUrl}?page={page}&per_page={ReleaseListPageSize}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(BrandMetadata.ArtifactPrefix + "-Desktop/1.0");
            using HttpResponseMessage response = await Client.SendAsync(
                request,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            List<MobileAppReleaseDocument> releases =
                JsonSerializer.Deserialize<List<MobileAppReleaseDocument>>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? [];
            foreach (MobileAppReleaseDocument release in releases)
            {
                string tagName = release.TagName?.Trim() ?? "";
                MobileAppReleaseAsset? manifestAsset = release.Assets.FirstOrDefault(
                    asset => string.Equals(
                        asset.Name?.Trim(),
                        IosLatestManifestName,
                        StringComparison.OrdinalIgnoreCase));
                if (tagName.Length > 0
                    && TryResolveTrustedGiteeUrl(
                        manifestAsset?.BrowserDownloadUrl,
                        out string manifestUrl))
                {
                    candidates.Add((tagName, manifestUrl));
                }
            }

            if (releases.Count < ReleaseListPageSize)
                break;
        }

        MobileAppReleaseInfo? latest = null;
        foreach ((string tagName, string manifestUrl) in candidates)
        {
            MobileAppReleaseInfo candidate = await FetchIosReleaseAsync(
                tagName,
                manifestUrl,
                cancellationToken);
            if (latest == null
                || candidate.BuildNumber > latest.BuildNumber
                || (candidate.BuildNumber == latest.BuildNumber
                    && string.CompareOrdinal(candidate.Version, latest.Version) > 0))
            {
                latest = candidate;
            }
        }

        lock (_gate)
            _latestIosRelease = latest;
        if (latest == null)
            DeleteCachedRelease(AppPaths.MobileIosAppUpdateCachePath);
        else
            SaveCachedRelease(AppPaths.MobileIosAppUpdateCachePath, latest);
        return latest;
    }

    private static async Task<MobileAppReleaseInfo> FetchIosReleaseAsync(
        string tagName,
        string manifestUrl,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        request.Headers.UserAgent.ParseAdd(BrandMetadata.ArtifactPrefix + "-Desktop/1.0");
        using HttpResponseMessage response = await Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength > MaximumBuildManifestBytes)
        {
            throw new InvalidDataException("iOS 版本清单过大");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MaximumBuildManifestBytes)
            throw new InvalidDataException("iOS 版本清单过大");
        return ParseIosLatestManifest(
            System.Text.Encoding.UTF8.GetString(buffer.ToArray()),
            tagName);
    }

    internal static MobileAppReleaseInfo ParseIosLatestManifest(
        string json,
        string releaseTag)
    {
        MobileIosLatestManifest? manifest =
            JsonSerializer.Deserialize<MobileIosLatestManifest>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        string version = manifest?.VersionName?.Trim() ?? "";
        int buildNumber = manifest?.BuildNumber ?? 0;
        if (manifest == null
            || !string.Equals(manifest.Platform, "ios", StringComparison.OrdinalIgnoreCase)
            || buildNumber <= 0
            || !Version.TryParse(version, out _))
        {
            throw new InvalidDataException("iOS 版本清单格式无效");
        }

        Match tagMatch = VersionPattern.Match(releaseTag?.Trim() ?? "");
        if (tagMatch.Success)
        {
            string tagVersion = tagMatch.Groups["version"].Value;
            string tagBuildText = tagMatch.Groups["build"].Value;
            if (!string.Equals(tagVersion, version, StringComparison.OrdinalIgnoreCase)
                || (tagBuildText.Length > 0
                    && (!int.TryParse(tagBuildText, out int tagBuild)
                        || tagBuild != buildNumber)))
            {
                throw new InvalidDataException("iOS 版本清单与 Release 标签不一致");
            }
        }

        string downloadUrl = TryResolveTrustedTestFlightUrl(
            manifest.TestFlightUrl,
            out string testFlightUrl)
            ? testFlightUrl
            : TestFlightJoinUrl;
        return new MobileAppReleaseInfo(
            releaseTag?.Trim() ?? "",
            version,
            buildNumber,
            downloadUrl,
            "ios");
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
            ResolveDownloadUrl());
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
            request.Headers.UserAgent.ParseAdd(BrandMetadata.ArtifactPrefix + "-Desktop/1.0");
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

    private static string ResolveDownloadUrl() => ReleasesUrl;

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

    internal static MobileAppReleaseInfo? SelectLatestReleaseForPlatform(
        string? platform,
        MobileAppReleaseInfo? androidRelease,
        MobileAppReleaseInfo? iosRelease) =>
        (platform ?? "").Trim().ToLowerInvariant() switch
        {
            "android" => androidRelease,
            "ios" => iosRelease,
            _ => null
        };

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

    private sealed class MobileIosLatestManifest
    {
        [JsonPropertyName("platform")]
        public string Platform { get; set; } = "";

        [JsonPropertyName("versionName")]
        public string VersionName { get; set; } = "";

        [JsonPropertyName("buildNumber")]
        public int BuildNumber { get; set; }

        [JsonPropertyName("sourceRevision")]
        public string SourceRevision { get; set; } = "";

        [JsonPropertyName("publishedAtUtc")]
        public string PublishedAtUtc { get; set; } = "";

        [JsonPropertyName("testFlightUrl")]
        public string TestFlightUrl { get; set; } = "";
    }

    private static bool TryResolveTrustedTestFlightUrl(
        string? value,
        out string url)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(
                uri.Host,
                "testflight.apple.com",
                StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/join/", StringComparison.Ordinal))
        {
            url = uri.AbsoluteUri;
            return true;
        }

        url = "";
        return false;
    }

    private static MobileAppReleaseInfo? LoadCachedRelease(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath))
                return null;
            return JsonSerializer.Deserialize<MobileAppReleaseInfo>(
                File.ReadAllText(cachePath));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCachedRelease(
        string cachePath,
        MobileAppReleaseInfo release)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(
                cachePath,
                JsonSerializer.Serialize(release));
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("MobileUpdate", $"手机版本缓存保存失败：{ex.Message}");
        }
    }

    private static void DeleteCachedRelease(string cachePath)
    {
        try
        {
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
        catch
        {
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
    string DownloadUrl,
    string Platform = "android");

internal sealed record MobileAppUpdateAvailableInfo(
    string DeviceName,
    string CurrentVersion,
    int CurrentBuildNumber,
    MobileAppReleaseInfo LatestRelease);
