using System.Text.Json;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

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
    private readonly Func<string, string> _apiTokenProvider;
    private readonly int _attemptsPerSource;
    private readonly TimeSpan _retryDelay;
    private readonly Action<string>? _log;
    private readonly bool _allowPrerelease;

    /// <param name="allowPrerelease">
    /// 本客户端是否接收预览版（prerelease）。默认 false = 只认正式版；
    /// 测试机通过 <see cref="UpdateChannelPolicy.AllowPrereleaseFromEnvironment"/> 打开。
    /// 草稿任何情况下都不接收。
    /// </param>
    public UpdateMetadataClient(
        HttpClient httpClient,
        string userAgent = "ExpressPackingMonitoring",
        int attemptsPerSource = 1,
        TimeSpan? retryDelay = null,
        Action<string>? log = null,
        Func<string, string>? apiTokenProvider = null,
        bool allowPrerelease = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? "ExpressPackingMonitoring" : userAgent.Trim();
        _apiTokenProvider = apiTokenProvider ?? (_ => "");
        _attemptsPerSource = Math.Max(1, attemptsPerSource);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(500);
        _log = log;
        _allowPrerelease = allowPrerelease;
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
                    // 这条是"直接取单个 release"的老路径（/releases/latest），
                    // 同样不能把草稿或预览版当成可更新版本。
                    if (!UpdateReleaseSelection.IsDeliverable(release.RootElement, _allowPrerelease))
                        throw new InvalidDataException("该 Release 是草稿或预览版，当前更新渠道不接收");
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

    /// <summary>
    /// 取"最新且带本平台安装包"的 release：在 release 列表里按新到旧找第一个带
    /// 指定平台包的版本。版本号两个平台共用，只看最新 tag 会把"只修了另一个平台"
    /// 的版本推给本平台，用户白下载一次。
    /// </summary>
    public async Task<ResolvedUpdateRelease> FetchLatestReleaseWithAssetAsync(
        IReadOnlyList<string> releaseListUrls,
        Func<string, bool> assetPredicate,
        CancellationToken cancellationToken)
    {
        return await ExecuteWithFallbackAsync(
            releaseListUrls,
            (sourceUrl, token) => FetchLatestReleaseWithAssetPagedAsync(sourceUrl, assetPredicate, token),
            cancellationToken);
    }

    /// <summary>release 列表最多翻多少页（一页 100 条）：服务端异常返回时不能无限翻。</summary>
    private const int MaxReleaseListPages = 10;

    /// <summary>
    /// 翻完整个 release 列表，挑出「版本最高、且带本平台资产」的那一个。
    ///
    /// 必须翻页翻完，不能只读第一页：Gitee 的 releases 是**旧 → 新**返回，刚发布的版本在最后一页。
    /// 旧实现是"per_page=30 只读第一页 + 取第一个匹配项"，于是 Gitee 上永远看不到新版本 ——
    /// 现场（店里 0.0.73）手动检查更新一直说"已是最新"就是这个原因。
    /// 停止条件用"这一页没填满"，不依赖各平台是否提供总数/分页头。
    /// </summary>
    private async Task<ResolvedUpdateRelease> FetchLatestReleaseWithAssetPagedAsync(
        string sourceUrl,
        Func<string, bool> assetPredicate,
        CancellationToken cancellationToken)
    {
        int perPage = ReadPageSize(sourceUrl);
        JsonDocument? best = null;
        string bestVersion = "";
        int pagesScanned = 0;

        try
        {
            for (int page = 1; page <= MaxReleaseListPages; page++)
            {
                JsonDocument list = await GetJsonAsync(BuildPageUrl(sourceUrl, page), cancellationToken);
                int pageCount;
                try
                {
                    if (list.RootElement.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("release 列表不是数组");

                    pagesScanned = page;
                    pageCount = list.RootElement.GetArrayLength();

                    int index = UpdateReleaseSelection.FindLatestWithAsset(
                        list.RootElement,
                        assetPredicate,
                        _allowPrerelease);
                    if (index >= 0)
                    {
                        JsonElement candidate = list.RootElement[index];
                        string tag = ReadString(candidate, "tag_name");
                        if (best == null || UpdateReleaseSelection.CompareVersions(tag, bestVersion) > 0)
                        {
                            // 下游按"单个 release 对象"解析，这里把它单独复制出来，列表可以马上释放
                            JsonDocument copy = JsonDocument.Parse(candidate.GetRawText());
                            best?.Dispose();
                            best = copy;
                            bestVersion = tag;
                        }
                    }
                }
                finally
                {
                    list.Dispose();
                }

                if (pageCount <= 0 || pageCount < perPage)
                    break;
            }
        }
        catch
        {
            best?.Dispose();
            throw;
        }

        if (best == null)
            throw new InvalidDataException("没有找到带本平台安装包的版本");

        RequireLatestVersion(best.RootElement);
        // 现场排查用：把"翻了几页、最后挑中哪一版"落进日志，
        // 下次再有人说"检查不到新版本"，一眼就能看出是挑错了版本还是压根没看到。
        _log?.Invoke($"release list scanned source={sourceUrl} pages={pagesScanned} chosen={bestVersion}");
        return new ResolvedUpdateRelease(best, sourceUrl);
    }

    /// <summary>列表地址里的分页大小；解析不出来按 100（README/策略里的默认值）处理。</summary>
    private static int ReadPageSize(string url)
    {
        Match match = Regex.Match(url, "[?&]per_page=(?<size>\\d+)", RegexOptions.IgnoreCase);
        return match.Success
            && int.TryParse(match.Groups["size"].Value, out int size)
            && size > 0
                ? size
                : 100;
    }

    /// <summary>在列表地址上换页：清掉已有的 page 参数，再拼当前页。</summary>
    private static string BuildPageUrl(string url, int page)
    {
        string cleaned = Regex.Replace(url, "[?&]page=\\d+", "", RegexOptions.IgnoreCase);
        string separator = cleaned.Contains('?') ? "&" : "?";
        return $"{cleaned}{separator}page={page}";
    }

    public async Task<ResolvedUpdateManifest> FetchLatestManifestAsync(
        IReadOnlyList<string> sourceUrls,
        CancellationToken cancellationToken)
    {
        // 只发另一个平台的版本（例如只发 macOS 的 DMG）没有 update_v*.json：直接拿 /releases/latest
        // 会报"Release 缺少 update_v*.json"，启动器的自动更新就一直在失败。改成翻完整个列表、
        // 挑"版本最高且带更新清单"的那一个，与 macOS 端"只认带本平台安装包的版本"是同一条规则。
        IReadOnlyList<string> releaseListUrls = UpdateEndpointPolicy.ToReleaseListUrls(sourceUrls);
        if (releaseListUrls.Count == 0)
            releaseListUrls = sourceUrls;

        return await ExecuteWithFallbackAsync(
            releaseListUrls,
            async (sourceUrl, token) =>
            {
                // 与"应用内检查更新"共用同一条挑选路径：翻完整个列表、按版本号挑最高的一版。
                ResolvedUpdateRelease resolved = await FetchLatestReleaseWithAssetPagedAsync(
                    sourceUrl,
                    UpdateReleaseSelection.IsUpdateManifest,
                    token);

                JsonDocument release = resolved.Release;
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
                    resolved.Dispose();
                    throw;
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// 尝试按版本号查找指定 Release 的 update 清单。
    /// 仅支持形如 .../releases/latest 的 GitHub/Gitee API 更新源；
    /// 找不到对应 Release、清单资产或清单内容时返回 null。
    /// </summary>
    public async Task<ResolvedUpdateManifest?> TryResolveManifestForVersionAsync(
        IReadOnlyList<string> sourceUrls,
        string targetVersion,
        CancellationToken cancellationToken)
    {
        string normalizedTarget = NormalizeVersion(targetVersion);
        if (normalizedTarget.Length == 0)
            return null;

        foreach (string sourceUrl in sourceUrls)
        {
            string tagUrl = DeriveReleaseByTagUrl(sourceUrl, normalizedTarget);
            if (tagUrl.Length == 0)
                continue;

            try
            {
                JsonDocument release = await GetJsonAsync(tagUrl, cancellationToken);
                JsonDocument? manifest = null;
                try
                {
                    string tag = NormalizeVersion(ReadString(release.RootElement, "tag_name"));
                    if (tag.Length == 0
                        || !string.Equals(tag, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        release.Dispose();
                        continue;
                    }

                    string manifestUrl = FindUpdateManifestUrl(release.RootElement, normalizedTarget);
                    if (manifestUrl.Length == 0)
                    {
                        release.Dispose();
                        continue;
                    }

                    manifest = await GetJsonAsync(manifestUrl, cancellationToken);
                    _log?.Invoke($"target manifest resolved version={normalizedTarget} url={manifestUrl}");
                    return new ResolvedUpdateManifest(
                        release,
                        manifest,
                        sourceUrl,
                        manifestUrl,
                        normalizedTarget);
                }
                catch
                {
                    manifest?.Dispose();
                    release.Dispose();
                    throw;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"target manifest lookup failed version={normalizedTarget} url={tagUrl}, error={ex.Message}");
            }
        }

        return null;
    }

    public static string DeriveReleaseByTagUrl(string sourceUrl, string targetVersion)
    {
        if (!UpdateEndpointPolicy.IsSecureAbsoluteUrl(sourceUrl))
            return "";

        string trimmed = sourceUrl.TrimEnd('/');
        if (!trimmed.EndsWith("/releases/latest", StringComparison.OrdinalIgnoreCase))
            return "";

        string apiBase = trimmed[..^"/latest".Length];
        string tag = NormalizeVersion(targetVersion);
        if (tag.Length == 0)
            return "";

        return $"{apiBase}/tags/v{tag}";
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
        // 可选令牌：未认证的 GitHub API 每 IP 每小时只有 60 次，
        // 同一出口 IP 下多台机器（主机、手机版本策略）会互相挤掉配额，配上令牌就宽裕得多
        string token = _apiTokenProvider(url);
        if (token.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                    && uri.Host.EndsWith("gitee.com", StringComparison.OrdinalIgnoreCase)
                        ? "token"
                        : "Bearer",
                token);
        }
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
