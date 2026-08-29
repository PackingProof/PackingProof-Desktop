namespace ExpressPackingMonitoring.Services.Extensions;

internal sealed class ExtensionMarketCatalog
{
    public int SchemaVersion { get; set; }
    public List<ExtensionMarketCatalogItem> Extensions { get; set; } = new();
}

internal sealed class ExtensionMarketCatalogItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Type { get; set; } = "";
    public string SourceAvailability { get; set; } = "open-source";
    public List<string> RiskLabels { get; set; } = new();
    public ExtensionMarketPublisher Publisher { get; set; } = new();
    public string? LatestVersion { get; set; }
    public string Details { get; set; } = "";
    public string DetailsSha256 { get; set; } = "";
}

internal sealed class ExtensionMarketDetails
{
    public int SchemaVersion { get; set; }
    public ExtensionMarketDescriptor Extension { get; set; } = new();
    public ExtensionMarketPublisher Publisher { get; set; } = new();
    public string Trust { get; set; } = "third-party";
    public List<string> RiskLabels { get; set; } = new();
    public List<ExtensionMarketVersionEntry> Versions { get; set; } = new();
}

internal sealed class ExtensionMarketDescriptor
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "";
    public string SourceAvailability { get; set; } = "open-source";
    public string? Homepage { get; set; }
}

internal sealed class ExtensionMarketPublisher
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Homepage { get; set; }
}

internal sealed class ExtensionMarketVersionEntry
{
    public ExtensionMarketRelease Release { get; set; } = new();
    public string Status { get; set; } = "available";
}

internal sealed class ExtensionMarketRelease
{
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public ExtensionMarketCompatibility Compatibility { get; set; } = new();
    public ExtensionMarketDownloads Downloads { get; set; } = new();
}

internal sealed class ExtensionMarketCompatibility
{
    public string MinPackingProofVersion { get; set; } = "";
}

internal sealed class ExtensionMarketDownloads
{
    public ExtensionMarketDownload? Gitee { get; set; }
    public ExtensionMarketDownload? Github { get; set; }
    public ExtensionMarketDownload? Primary { get; set; }
    public ExtensionMarketDownload? Mirror { get; set; }

    internal IReadOnlyList<ExtensionMarketDownload> InPreferredOrder()
    {
        IEnumerable<ExtensionMarketDownload?> values = Primary != null
            ? new[] { Primary, Mirror }
            : new[] { Gitee, Github };
        return values
            .Where(value => value != null)
            .Cast<ExtensionMarketDownload>()
            .OrderBy(value => string.Equals(value.Provider, "gitee", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
    }
}

internal sealed class ExtensionMarketDownload
{
    public string Provider { get; set; } = "";
    public string Url { get; set; } = "";
}

internal sealed class ExtensionCatalogSignature
{
    public int SchemaVersion { get; set; }
    public string Algorithm { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string CatalogSha256 { get; set; } = "";
    public string Signature { get; set; } = "";
}

internal sealed record ExtensionMarketSession(
    ExtensionMarketCatalog Catalog,
    bool IsCached,
    IReadOnlyList<string> RegistryBases);

internal sealed record ExtensionPackageProgress(string Message, long Received = 0, long Total = 0);
