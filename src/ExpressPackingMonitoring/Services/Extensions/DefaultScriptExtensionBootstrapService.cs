using ExpressPackingMonitoring.Config;
using System.IO;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services.Extensions;

/// <summary>
/// 随 Desktop 发布的默认脚本清单。文件只列出扩展 ID，名称、发布者、版本和包地址
/// 始终以签名扩展市场为准；代码不内置具体脚本 ID。
/// </summary>
internal sealed class DefaultScriptExtensionManifest
{
    public int SchemaVersion { get; set; }
    public List<string> ScriptIds { get; set; } = [];

    internal static DefaultScriptExtensionManifest Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("默认脚本清单不存在", fullPath);

        DefaultScriptExtensionManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DefaultScriptExtensionManifest>(
                File.ReadAllText(fullPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("默认脚本清单为空");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("默认脚本清单格式无效", ex);
        }

        if (manifest.SchemaVersion != 1)
            throw new InvalidDataException("默认脚本清单版本不受支持");

        manifest.ScriptIds = manifest.ScriptIds
            .Select(id => id?.Trim() ?? "")
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (manifest.ScriptIds.Count == 0
            || manifest.ScriptIds.Any(id => !IsValidExtensionId(id)))
        {
            throw new InvalidDataException("默认脚本清单包含无效扩展 ID");
        }

        return manifest;
    }

    private static bool IsValidExtensionId(string id) =>
        id.Length <= 160
        && char.IsAsciiLetterOrDigit(id[0])
        && id.All(value =>
            char.IsAsciiLetterOrDigit(value)
            || value is '.' or '_' or '-');
}

internal sealed record DefaultScriptExtensionBootstrapResult(
    IReadOnlyList<string> Satisfied,
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> Failures);

/// <summary>
/// 启动或打开安装向导时，把清单内的脚本从签名扩展市场安装到 Desktop。
/// 这里只处理缺失项；脚本版本更新继续由 ExtensionAutoUpdateService 统一负责。
/// </summary>
internal sealed class DefaultScriptExtensionBootstrapService
{
    private readonly IExtensionMarketSource _market;
    private readonly IExtensionInstallationTarget _installation;
    private readonly string _appVersion;
    private readonly string _manifestPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<string>? _scriptIds;

    internal static DefaultScriptExtensionBootstrapService Shared { get; } = new(
        new ExtensionMarketClient(),
        new ExtensionInstallationService(),
        AppVersion.Current,
        Path.Combine(AppContext.BaseDirectory, "Resources", "DefaultScriptExtensions.json"));

    internal DefaultScriptExtensionBootstrapService(
        IExtensionMarketSource market,
        IExtensionInstallationTarget installation,
        string appVersion,
        string manifestPath)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _installation = installation ?? throw new ArgumentNullException(nameof(installation));
        _appVersion = appVersion ?? "";
        _manifestPath = manifestPath ?? throw new ArgumentNullException(nameof(manifestPath));
    }

    internal async Task<DefaultScriptExtensionBootstrapResult> EnsureInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<string> scriptIds = LoadScriptIds();
            Dictionary<string, InstalledExtensionRecord> installed = _installation.GetInstalled()
                .Where(record => string.Equals(record.Type, "userscript", StringComparison.Ordinal))
                .GroupBy(record => record.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            var satisfied = scriptIds
                .Where(installed.ContainsKey)
                .ToList();
            string[] missing = scriptIds
                .Where(id => !installed.ContainsKey(id))
                .ToArray();
            if (missing.Length == 0)
                return new DefaultScriptExtensionBootstrapResult(satisfied, [], []);

            ExtensionMarketSession session = await _market.LoadCatalogAsync(cancellationToken);
            Dictionary<string, ExtensionMarketCatalogItem> catalog = session.Catalog.Extensions
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            var installedNow = new List<string>();
            var failures = new List<string>();
            foreach (string extensionId in missing)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                if (!catalog.TryGetValue(extensionId, out ExtensionMarketCatalogItem? item)
                    || !string.Equals(item.Type, "userscript", StringComparison.Ordinal))
                {
                    failures.Add($"{extensionId}: 市场缺少脚本条目");
                    continue;
                }

                string latestVersion = item.LatestVersion ?? "";
                string packagePath = "";
                try
                {
                    ExtensionMarketDetails details = await _market.LoadDetailsAsync(
                        session,
                        item,
                        cancellationToken);
                    if (!string.Equals(details.Extension.Id, extensionId, StringComparison.Ordinal)
                        || !string.Equals(details.Extension.Type, "userscript", StringComparison.Ordinal))
                    {
                        failures.Add($"{extensionId}: 市场身份校验失败");
                        continue;
                    }

                    ExtensionMarketRelease? release = ExtensionMarketVersionPolicy.SelectInstallableRelease(
                        details,
                        latestVersion);
                    if (release == null)
                    {
                        failures.Add($"{extensionId}: 市场没有可安装版本");
                        continue;
                    }

                    if (!ExtensionMarketVersionPolicy.IsAppCompatible(
                            release.Compatibility.MinPackingProofVersion,
                            _appVersion))
                    {
                        failures.Add($"{extensionId}: 需要更高版本的 PackingProof");
                        continue;
                    }

                    packagePath = await _market.DownloadPackageAsync(
                        release,
                        null,
                        cancellationToken);
                    _installation.Install(
                        packagePath,
                        item.Name,
                        extensionId,
                        release.Version,
                        "userscript",
                        release.Sha256);
                    installedNow.Add(extensionId);
                    satisfied.Add(extensionId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failures.Add($"{extensionId}: {ex.Message}");
                }
                finally
                {
                    TryDeletePackage(packagePath);
                }
            }

            return new DefaultScriptExtensionBootstrapResult(satisfied, installedNow, failures);
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<string> LoadScriptIds()
    {
        if (_scriptIds != null) return _scriptIds;
        DefaultScriptExtensionManifest manifest = DefaultScriptExtensionManifest.Load(_manifestPath);
        _scriptIds = manifest.ScriptIds;
        return _scriptIds;
    }

    private static void TryDeletePackage(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath)) return;
        try
        {
            if (File.Exists(packagePath)) File.Delete(packagePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
