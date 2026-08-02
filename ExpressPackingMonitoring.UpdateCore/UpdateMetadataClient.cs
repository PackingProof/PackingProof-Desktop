using System.Text.Json;

namespace ExpressPackingMonitoring.UpdateCore;

public sealed class ResolvedUpdateRelease : IDisposable
{
    internal ResolvedUpdateRelease(JsonDocument release, string sourceUrl)
    {
        Release = release;
        SourceUrl = sourceUrl;
    }

    public JsonDocument Release { get; }
    public string SourceUrl { get; }

    public void Dispose() => Release.Dispose();
}

public sealed class ResolvedUpdateManifest : IDisposable
{
    internal ResolvedUpdateManifest(
        JsonDocument release,
        JsonDocument manifest,
        string sourceUrl,
        string manifestUrl,
        string latestVersion)
    {
        Release = release;
        Manifest = manifest;
        SourceUrl = sourceUrl;
        ManifestUrl = manifestUrl;
        LatestVersion = latestVersion;
    }

    public JsonDocument Release { get; }
    public JsonDocument Manifest { get; }
    public string SourceUrl { get; }
    public string ManifestUrl { get; }
    public string LatestVersion { get; }

    public void Dispose()
    {
        Manifest.Dispose();
        Release.Dispose();
    }
}

public sealed class UpdateMetadataClient
{
    private readonly HttpClient _httpClient;
    private readonly string _userAgent;
    private readonly int _attemptsPerSource;
    private readonly TimeSpan _retryDelay;
    private readonly Action<string>? _log;

    public UpdateMetadataClient(
        HttpClient httpClient,
        string userAgent = "ExpressPackingMonitoring",
        int attemptsPerSource = 1,
        TimeSpan? retryDelay = null,
        Action<string>? log = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? "ExpressPackingMonitoring" : userAgent.Trim();
        _attemptsPerSource = Math.Max(1, attemptsPerSource);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(500);
        _log = log;
    }

    public async Task<ResolvedUpdateRelease> FetchLatestReleaseAsync(
        IReadOnlyList<string> sourceUrls,
        CancellationToken cancellationToken)
    {
        return await ExecuteWithFallbackAsync(
            sourceUrls,
            async (sourceUrl, token) =>
            {
                JsonDocument release = await GetJsonAsync(sourceUrl, token);
                try
                {
                    RequireLatestVersion(release.RootElement);
                    return new ResolvedUpdateRelease(release, sourceUrl);
                }
                catch
                {
                    release.Dispose();
                    throw;
                }
            },
            cancellationToken);
    }

    public async Task<ResolvedUpdateManifest> FetchLatestManifestAsync(
        IReadOnlyList<string> sourceUrls,
        CancellationToken cancellationToken)
    {
        return await ExecuteWithFallbackAsync(
            sourceUrls,
            async (sourceUrl, token) =>
            {
                JsonDocument release = await GetJsonAsync(sourceUrl, token);
                JsonDocument? manifest = null;
                try
                {
                    string latestVersion = RequireLatestVersion(release.RootElement);
                    string manifestUrl = FindUpdateManifestUrl(release.RootElement, latestVersion);
                    if (manifestUrl.Length == 0)
                        throw new InvalidDataException($"Release 缺少 update_v{latestVersion}.json");
                    manifest = await GetJsonAsync(manifestUrl, token);
                    return new ResolvedUpdateManifest(
                        release,
                        manifest,
                        sourceUrl,
                        manifestUrl,
                        latestVersion);
                }
                catch
                {
                    manifest?.Dispose();
                    release.Dispose();
                    throw;
                }
            },
            cancellationToken);
    }

    public static string FindUpdateManifestUrl(JsonElement releaseRoot, string latestVersion)
    {
        if (!releaseRoot.TryGetProperty("assets", out JsonElement assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        string preferred = $"update_v{NormalizeVersion(latestVersion)}.json";
        string fallback = "";
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string name = ReadString(asset, "name").Trim();
            string url = ReadString(asset, "browser_download_url").Trim();
            if (url.Length == 0)
                url = ReadString(asset, "url").Trim();
            if (!UpdateEndpointPolicy.IsSecureAbsoluteUrl(url))
                continue;

            if (string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase))
                return url;
            if (fallback.Length == 0 && string.Equals(name, "update.json", StringComparison.OrdinalIgnoreCase))
                fallback = url;
        }

        return fallback;
    }

    public static string NormalizeVersion(string? value)
    {
        string normalized = value?.Trim() ?? "";
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];
        int suffixIndex = normalized.IndexOfAny(['+', '-']);
        return suffixIndex >= 0 ? normalized[..suffixIndex] : normalized;
    }

    private async Task<T> ExecuteWithFallbackAsync<T>(
        IReadOnlyList<string> sourceUrls,
        Func<string, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        string[] sources = sourceUrls
            .Where(UpdateEndpointPolicy.IsSecureAbsoluteUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sources.Length == 0)
            throw new InvalidOperationException("更新检查地址未配置");

        Exception? lastError = null;
        foreach (string sourceUrl in sources)
        {
            for (int attempt = 1; attempt <= _attemptsPerSource; attempt++)
            {
                try
                {
                    T result = await action(sourceUrl, cancellationToken);
                    _log?.Invoke($"metadata source succeeded url={sourceUrl}");
                    return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _log?.Invoke(
                        $"metadata source failed url={sourceUrl}, attempt={attempt}/{_attemptsPerSource}, error={ex.Message}");
                    if (attempt < _attemptsPerSource)
                        await Task.Delay(_retryDelay, cancellationToken);
                }
            }
        }

        throw new HttpRequestException("所有更新检查来源均不可用", lastError);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(_userAgent);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string RequireLatestVersion(JsonElement releaseRoot)
    {
        string latestVersion = NormalizeVersion(ReadString(releaseRoot, "tag_name"));
        if (latestVersion.Length == 0)
            throw new InvalidDataException("Release 信息缺少 tag_name");
        return latestVersion;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
    }
}
