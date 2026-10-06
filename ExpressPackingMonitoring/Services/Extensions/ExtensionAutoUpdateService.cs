using System.IO;
using System.Linq;

namespace ExpressPackingMonitoring.Services.Extensions;

/// <summary>扩展市场来源（目录已做签名校验，包按市场登记的 SHA-256 校验）。</summary>
internal interface IExtensionMarketSource
{
    Task<ExtensionMarketSession> LoadCatalogAsync(CancellationToken cancellationToken = default);

    Task<ExtensionMarketDetails> LoadDetailsAsync(
        ExtensionMarketSession session,
        ExtensionMarketCatalogItem item,
        CancellationToken cancellationToken = default);

    Task<string> DownloadPackageAsync(
        ExtensionMarketRelease release,
        IProgress<ExtensionPackageProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>已安装扩展的读取与安装入口。</summary>
internal interface IExtensionInstallationTarget
{
    IReadOnlyList<InstalledExtensionRecord> GetInstalled();

    ExtensionInstallResult Install(
        string packagePath,
        string displayName,
        string? expectedId = null,
        string? expectedVersion = null,
        string? expectedType = null,
        string? expectedSha256 = null);
}

internal sealed record ExtensionAutoUpdateResult(
    bool Skipped,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> SkippedItems,
    IReadOnlyList<string> Failures);

/// <summary>
/// 后台把"扩展市场里登记过"的已安装扩展更新到最新版。
/// 市场里查不到的扩展一律不动；市场要求更高版本的 PackingProof、或者外部适配器正在运行时也跳过，
/// 等下一次启动再试。目录与安装包都沿用市场那一套签名与 SHA-256 校验。
/// </summary>
internal sealed class ExtensionAutoUpdateService
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly IExtensionMarketSource _market;
    private readonly IExtensionInstallationTarget _installation;
    private readonly string _appVersion;

    internal ExtensionAutoUpdateService(
        IExtensionMarketSource market,
        IExtensionInstallationTarget installation,
        string appVersion)
    {
        _market = market ?? throw new ArgumentNullException(nameof(market));
        _installation = installation ?? throw new ArgumentNullException(nameof(installation));
        _appVersion = appVersion ?? "";
    }

    internal async Task<ExtensionAutoUpdateResult> RunAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<InstalledExtensionRecord> installed = _installation.GetInstalled();
        if (installed.Count == 0)
            return new ExtensionAutoUpdateResult(true, [], [], []);

        ExtensionMarketSession session = await _market.LoadCatalogAsync(cancellationToken);
        Dictionary<string, ExtensionMarketCatalogItem> registry = session.Catalog.Extensions
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var updated = new List<string>();
        var skipped = new List<string>();
        var failures = new List<string>();
        foreach (InstalledExtensionRecord record in installed)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!registry.TryGetValue(record.Id, out ExtensionMarketCatalogItem? item)) continue;

            string latestVersion = item.LatestVersion ?? "";
            if (!ExtensionMarketVersionPolicy.IsNewerVersion(latestVersion, record.Version)) continue;
            if (IsRunningExternalAdapter(record))
            {
                skipped.Add($"{record.Id}: 程序正在运行，本次不更新");
                continue;
            }

            string packagePath = "";
            try
            {
                ExtensionMarketDetails details = await _market.LoadDetailsAsync(session, item, cancellationToken);
                ExtensionMarketRelease? release = ExtensionMarketVersionPolicy.SelectInstallableRelease(details, latestVersion);
                if (release == null)
                {
                    failures.Add($"{record.Id}: 市场没有可安装的版本");
                    continue;
                }

                if (!ExtensionMarketVersionPolicy.IsAppCompatible(release.Compatibility.MinPackingProofVersion, _appVersion))
                {
                    failures.Add($"{record.Id}: 需要 PackingProof {release.Compatibility.MinPackingProofVersion} 或更高版本");
                    continue;
                }

                packagePath = await _market.DownloadPackageAsync(release, null, cancellationToken);
                _installation.Install(
                    packagePath,
                    item.Name,
                    item.Id,
                    release.Version,
                    item.Type,
                    release.Sha256);
                updated.Add($"{record.Id} {record.Version}→{release.Version}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures.Add($"{record.Id}: {ex.Message}");
            }
            finally
            {
                TryDeletePackage(packagePath);
            }
        }

        return new ExtensionAutoUpdateResult(false, updated, skipped, failures);
    }

    /// <summary>一天最多检查一次；时间戳解析不出来（或没记录过）就按"该检查"处理。</summary>
    internal static bool ShouldCheckNow(string? lastCheckText, DateTime now)
    {
        if (!DateTime.TryParse(
                lastCheckText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out DateTime lastCheck))
        {
            return true;
        }

        return now - lastCheck >= CheckInterval;
    }

    private static bool IsRunningExternalAdapter(InstalledExtensionRecord record)
    {
        if (!string.Equals(record.Type, "external-adapter", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(record.InstallDirectory)) return false;
        return ExtensionProcessManager.FindRunningProcesses(record.InstallDirectory).Count > 0;
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
