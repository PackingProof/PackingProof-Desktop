using ExpressPackingMonitoring.Logging;
using System.IO;
using System.Net;

namespace ExpressPackingMonitoring.Services.Extensions;

internal sealed record OfficialUserscriptPackageDownload(
    string PackagePath,
    string DisplayName,
    string Version,
    string Sha256);

internal interface IOfficialUserscriptPackageSource
{
    Task<OfficialUserscriptPackageDownload> DownloadLatestAsync(CancellationToken cancellationToken);
}

internal sealed class ExtensionMarketOfficialUserscriptPackageSource(ExtensionMarketClient marketClient)
    : IOfficialUserscriptPackageSource
{
    internal const string ExtensionId = "packingproof.kdzs";
    internal const string MinimumMigrationVersion = "2.15";

    public async Task<OfficialUserscriptPackageDownload> DownloadLatestAsync(
        CancellationToken cancellationToken)
    {
        ExtensionMarketSession session = await marketClient.LoadCatalogAsync(cancellationToken);
        ExtensionMarketCatalogItem item = session.Catalog.Extensions.FirstOrDefault(value =>
            string.Equals(value.Id, ExtensionId, StringComparison.Ordinal))
            ?? throw new InvalidDataException("扩展市场缺少官方快递助手扩展");
        ExtensionMarketDetails details = await marketClient.LoadDetailsAsync(
            session,
            item,
            cancellationToken);
        if (!string.Equals(details.Extension.Id, ExtensionId, StringComparison.Ordinal)
            || !string.Equals(details.Extension.Type, "userscript", StringComparison.Ordinal)
            || !string.Equals(details.Publisher.Id, "packingproof", StringComparison.Ordinal)
            || !string.Equals(details.Trust, "official", StringComparison.Ordinal))
        {
            throw new InvalidDataException("官方快递助手市场身份校验失败");
        }

        ExtensionMarketRelease release = details.Versions.FirstOrDefault(value =>
            string.Equals(value.Status, "available", StringComparison.Ordinal)
            && string.Equals(value.Release.Version, item.LatestVersion, StringComparison.Ordinal))?.Release
            ?? throw new InvalidDataException("官方快递助手没有可迁移版本");
        if (CompareTwoPartVersions(release.Version, MinimumMigrationVersion) < 0)
            throw new InvalidDataException($"官方快递助手迁移版本不能低于 {MinimumMigrationVersion}");

        string packagePath = await marketClient.DownloadPackageAsync(
            release,
            cancellationToken: cancellationToken);
        return new OfficialUserscriptPackageDownload(
            packagePath,
            details.Extension.Name,
            release.Version,
            release.Sha256);
    }

    private static int CompareTwoPartVersions(string left, string right)
    {
        return Version.TryParse(left, out Version? leftVersion)
            && Version.TryParse(right, out Version? rightVersion)
                ? leftVersion.CompareTo(rightVersion)
                : -1;
    }
}

internal sealed class OfficialUserscriptMigrationService
{
    internal const string LegacyRequestId = "__packingproof_legacy_kdzs__";
    internal const string CurrentDisplayName = "快递端油猴脚本";
    internal const string LegacyFilePath = "/PackingProof-Order-Integration-KDZS.user.js";
    internal const string OlderLegacyFilePath = "/kuaidizs-order-push.user.js";

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);
    private readonly IOfficialUserscriptPackageSource _packageSource;
    private readonly ExtensionInstallationService _installationService;
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private readonly object _scheduleLock = new();
    private Task? _scheduledMigration;
    private DateTimeOffset _retryAfterUtc;

    internal static OfficialUserscriptMigrationService Shared { get; } = new(
        new ExtensionMarketOfficialUserscriptPackageSource(new ExtensionMarketClient()),
        new ExtensionInstallationService());

    internal OfficialUserscriptMigrationService(
        IOfficialUserscriptPackageSource packageSource,
        ExtensionInstallationService installationService)
    {
        _packageSource = packageSource;
        _installationService = installationService;
    }

    internal static bool TryParseDownloadPath(string path, out string scriptId)
    {
        if (string.Equals(path, LegacyFilePath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, OlderLegacyFilePath, StringComparison.OrdinalIgnoreCase))
        {
            scriptId = LegacyRequestId;
            return true;
        }

        const string prefix = "/api/userscripts/";
        const string suffix = "/download";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && path.Length > prefix.Length + suffix.Length)
        {
            scriptId = path[prefix.Length..^suffix.Length];
            return true;
        }

        scriptId = "";
        return false;
    }

    internal static bool IsDownloadPathAllowed(string path, string method) =>
        string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        && TryParseDownloadPath(path, out _);

    internal static bool IsVersionedOfficialHeartbeat(ConnectedClientHeartbeat? heartbeat) =>
        IsOfficialHeartbeat(heartbeat)
        && Version.TryParse(heartbeat!.AppVersion?.Trim(), out _);

    internal static bool IsEarlyOfficialHeartbeat(ConnectedClientInfo? client) =>
        client != null
        && string.Equals(client.ClientType, "userscript", StringComparison.OrdinalIgnoreCase)
        && string.Equals(client.DisplayName, CurrentDisplayName, StringComparison.Ordinal)
        && !Version.TryParse(client.AppVersion?.Trim(), out _);

    internal static bool HasConcurrentLegacyAndCurrentScripts(
        IEnumerable<ConnectedClientInfo>? clients) =>
        (clients ?? [])
            .Where(client => string.Equals(client.ClientType, "userscript", StringComparison.OrdinalIgnoreCase)
                && string.Equals(client.DisplayName, CurrentDisplayName, StringComparison.Ordinal))
            .GroupBy(client => client.RemoteAddress, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Select(client => client.ClientId).Distinct(StringComparer.Ordinal).Count() > 1
                && group.Any(IsEarlyOfficialHeartbeat)
                && group.Any(client => Version.TryParse(client.AppVersion?.Trim(), out _)));

    internal string ResolveRequestedScriptId(string? requestedId)
    {
        if (!string.Equals(requestedId, LegacyRequestId, StringComparison.Ordinal))
            return string.IsNullOrWhiteSpace(requestedId) ? "" : Uri.UnescapeDataString(requestedId);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return EnsureInstalledAsync(timeout.Token).GetAwaiter().GetResult().ManagedItemId;
    }

    internal void ObserveHeartbeat(ConnectedClientHeartbeat heartbeat)
    {
        if (!IsVersionedOfficialHeartbeat(heartbeat) || FindInstalled() != null)
            return;

        lock (_scheduleLock)
        {
            if (_scheduledMigration is { IsCompleted: false }
                || DateTimeOffset.UtcNow < _retryAfterUtc)
                return;
            _scheduledMigration = Task.Run(RunScheduledMigrationAsync);
        }
    }

    internal async Task<InstalledExtensionRecord> EnsureInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        InstalledExtensionRecord? existing = FindInstalled();
        if (existing != null) return existing;

        await _migrationLock.WaitAsync(cancellationToken);
        try
        {
            existing = FindInstalled();
            if (existing != null) return existing;

            OfficialUserscriptPackageDownload package = await _packageSource.DownloadLatestAsync(cancellationToken);
            try
            {
                ExtensionInstallResult result = _installationService.Install(
                    package.PackagePath,
                    package.DisplayName,
                    ExtensionMarketOfficialUserscriptPackageSource.ExtensionId,
                    package.Version,
                    "userscript",
                    package.Sha256);
                RuntimeLog.Info(
                    "Extensions",
                    $"官方快递助手已静默迁移到扩展市场管理，version={result.Record.Version}");
                return result.Record;
            }
            finally
            {
                TryDeletePackage(package.PackagePath);
            }
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    internal static string BuildDownloadUrl(string scheme, string authority, string scriptId) =>
        $"{scheme}://{authority}/api/userscripts/{Uri.EscapeDataString(scriptId)}/download";

    internal static string BuildChoice(UserscriptDescriptor item, string scheme, string authority)
    {
        string url = BuildDownloadUrl(scheme, authority, item.Id);
        bool hasWarning = item.Warnings.Count > 0;
        string warning = hasWarning ? $"有提示：{string.Join("；", item.Warnings)}" : "可自动维护";
        string warningClass = hasWarning ? " has-warning" : " is-maintainable";
        return $"<div class=\"script-choice{warningClass}\"><div><strong>{WebUtility.HtmlEncode(item.Name)}</strong><span class=\"hint\"><span>版本</span> {WebUtility.HtmlEncode(item.Version)} · <span>{WebUtility.HtmlEncode(warning)}</span></span></div><a class=\"primary\" href=\"{WebUtility.HtmlEncode(url)}\" target=\"_blank\" rel=\"noopener\">安装</a></div>";
    }

    private static bool IsOfficialHeartbeat(ConnectedClientHeartbeat? heartbeat) =>
        heartbeat != null
        && heartbeat.Connected != false
        && string.Equals(heartbeat.ClientType, "userscript", StringComparison.OrdinalIgnoreCase)
        && string.Equals(heartbeat.DisplayName, CurrentDisplayName, StringComparison.Ordinal);

    private InstalledExtensionRecord? FindInstalled() =>
        _installationService.GetInstalled().FirstOrDefault(value =>
            string.Equals(
                value.Id,
                ExtensionMarketOfficialUserscriptPackageSource.ExtensionId,
                StringComparison.Ordinal)
            && string.Equals(value.Type, "userscript", StringComparison.Ordinal));

    private async Task RunScheduledMigrationAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await EnsureInstalledAsync(timeout.Token);
            lock (_scheduleLock) _retryAfterUtc = DateTimeOffset.MinValue;
        }
        catch (Exception ex)
        {
            lock (_scheduleLock) _retryAfterUtc = DateTimeOffset.UtcNow.Add(RetryDelay);
            RuntimeLog.Warn("Extensions", $"官方快递助手静默迁移失败：{ex.Message}");
        }
    }

    private static void TryDeletePackage(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Extensions", $"清理快递助手迁移包失败：{ex.Message}");
        }
    }
}
