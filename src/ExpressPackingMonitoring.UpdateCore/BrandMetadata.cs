using System.Reflection;
using System.Globalization;
using System.Text.Json;

namespace ExpressPackingMonitoring.UpdateCore;

/// <summary>
/// 构建时写入程序集元数据的当前软件配置。运行时不读取可被用户篡改的额外配置文件。
/// </summary>
public static class BrandMetadata
{
    private static readonly IReadOnlyDictionary<string, string> Values =
        typeof(BrandMetadata).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Key.StartsWith("Brand.", StringComparison.Ordinal))
            .GroupBy(attribute => attribute.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value ?? "",
                StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, BrandLocalizationEntry> LocalizedValues =
        LoadLocalizedValues();
    private static readonly Lazy<ProductFeaturesDocument> ProductFeaturesDocument =
        new(LoadProductFeatures, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string ProfileId => Get("Brand.ProfileId");
    public static string ProductName => GetLocalized("Brand.ProductName");
    public static string LegacyProductName => Get("Brand.LegacyProductName");
    public static string WindowTitle => GetLocalized("Brand.WindowTitle");
    public static string DisplayName => GetLocalized("Brand.DisplayName");
    public static string Description => GetLocalized("Brand.Description");
    public static string ArtifactPrefix => Get("Brand.ArtifactPrefix");
    public static string WebsiteUrl => Get("Brand.WebsiteUrl");
    public static string SupportUrl => Get("Brand.SupportUrl");
    public static string UpdatesUrl => Get("Brand.UpdatesUrl");
    public static string PrivacyPolicyUrl => Get("Brand.PrivacyPolicyUrl");
    public static string LicenseUrl => Get("Brand.LicenseUrl");
    public static string SupportEmail => Get("Brand.SupportEmail");
    public static string RepositorySlug => Get("Brand.RepositorySlug");
    public static string UpdateGiteeCheckUrl => Get("Brand.UpdateGiteeCheckUrl");
    public static string UpdateGithubCheckUrl => Get("Brand.UpdateGithubCheckUrl");
    public static string PatchDownloadBaseUrl => Get("Brand.PatchDownloadBaseUrl");
    public static string MobileRepositoryUrl => GetOptional("Brand.MobileRepositoryUrl");
    public static string MobileReleasesUrl => GetOptional("Brand.MobileReleasesUrl");
    public static string MobileLatestReleaseApiUrl => GetOptional("Brand.MobileLatestReleaseApiUrl");
    public static string MobileReleasesApiUrl => GetOptional("Brand.MobileReleasesApiUrl");
    public static ProductFeatures ProductFeatures => ProductFeaturesDocument.Value.Features;
    public static int ProductCapabilitySchemaVersion =>
        ProductFeaturesDocument.Value.SchemaVersion;
    public static string ProductFeaturesSha256 =>
        ProductFeaturesDocument.Value.Sha256;
    public static ProductCapabilityIdentity CapabilityIdentity =>
        new(
            ProfileId,
            ProductCapabilitySchemaVersion,
            ProductFeaturesSha256);
    public static string ExtensionGiteeRegistryBase => Get("Brand.ExtensionGiteeRegistryBase");
    public static string ExtensionGithubRegistryBase => Get("Brand.ExtensionGithubRegistryBase");
    public static string ExtensionMarketPublicKeyId => Get("Brand.ExtensionMarketPublicKeyId");
    public static string ExtensionMarketPublicKeySpki => Get("Brand.ExtensionMarketPublicKeySpki");
    public static string CompatibilityId => Get("Brand.CompatibilityId");
    public static string DataDirectoryName => Get("Brand.DataDirectoryName");
    public static string InstanceNamePrefix => Get("Brand.InstanceNamePrefix");
    public static string InstallDirectoryName => Get("Brand.InstallDirectoryName");
    public static string AppUserModelId => Get("Brand.AppUserModelId");
    public static string SetupAppId => Get("Brand.SetupAppId");
    public static string StoreIdentityName => Get("Brand.StoreIdentityName");
    public static string StorePublisher => Get("Brand.StorePublisher");
    public static string StorePublisherDisplayName => Get("Brand.StorePublisherDisplayName");
    public static string StoreShortName => Get("Brand.StoreShortName");
    public static string StoreStartupTaskId => Get("Brand.StoreStartupTaskId");
    public static string PackageEditionMarker => Get("Brand.PackageEditionMarker");
    public static int WebServerPort => GetPort("Brand.WebServerPort", 5280);
    public static int DiscoveryPort => GetPort("Brand.DiscoveryPort", 5281);
    public static string MacBundleId => Get("Brand.MacBundleId");
    private static int GetPort(string key, int fallback) =>
        int.TryParse(Get(key), out int port) && port is > 0 and <= 65535
            ? port
            : fallback;

    private static bool GetBoolean(string key, bool fallback) =>
        bool.TryParse(Get(key), out bool value) ? value : fallback;

    private static string Get(string key) =>
        Values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"程序集缺少软件元数据：{key}");

    private static string GetOptional(string key) =>
        Values.TryGetValue(key, out string? value) ? value : "";

    private static string GetLocalized(string key)
    {
        CultureInfo culture = CultureInfo.CurrentUICulture;
        string fallbackCulture = culture.TwoLetterISOLanguageName switch
        {
            "zh" => "zh-Hans",
            "ja" => "ja-JP",
            _ => "en-US"
        };
        BrandLocalizationEntry? entry = null;
        foreach (string candidate in new[] { culture.Name, fallbackCulture, culture.TwoLetterISOLanguageName }
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (LocalizedValues.TryGetValue(candidate, out entry)) break;
        }

        string? localized = key switch
        {
            "Brand.ProductName" => entry?.ProductName,
            "Brand.DisplayName" => entry?.DisplayName,
            "Brand.WindowTitle" => entry?.WindowTitle,
            "Brand.Description" => entry?.Description,
            _ => null
        };
        return string.IsNullOrWhiteSpace(localized) ? Get(key) : localized;
    }

    private static IReadOnlyDictionary<string, BrandLocalizationEntry> LoadLocalizedValues()
    {
        const string resourceName = "ExpressPackingMonitoring.UpdateCore.BrandLocalizations.json";
        using Stream? stream = typeof(BrandMetadata).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return new Dictionary<string, BrandLocalizationEntry>(StringComparer.OrdinalIgnoreCase);
        }
        using JsonDocument document = JsonDocument.Parse(stream);
        var result = new Dictionary<string, BrandLocalizationEntry>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetProperty(document.RootElement, "locales", out JsonElement locales)
            || locales.ValueKind != JsonValueKind.Object)
        {
            return result;
        }
        foreach (JsonProperty locale in locales.EnumerateObject())
        {
            if (locale.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            result[locale.Name] = new BrandLocalizationEntry
            {
                ProductName = ReadString(locale.Value, "productName"),
                DisplayName = ReadString(locale.Value, "displayName"),
                WindowTitle = ReadString(locale.Value, "windowTitle"),
                Description = ReadString(locale.Value, "description")
            };
        }
        return result;
    }

    private static ProductFeaturesDocument LoadProductFeatures()
    {
        const string resourceName = "ExpressPackingMonitoring.UpdateCore.BrandFeatures.json";
        using Stream? stream = typeof(BrandMetadata).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new InvalidOperationException(
                $"程序集缺少软件能力配置：{resourceName}");
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        ReadOnlySpan<byte> json = memory.ToArray();
        ProductFeaturesDocument document = ProductFeaturesJson.ParseDocument(json);
        ProductFeatureValidator.ThrowIfInvalid(
            document.Features,
            new ProductFeatureValidationContext(MobileReleasesUrl));
        return document;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private sealed class BrandLocalizationEntry
    {
        public string? ProductName { get; init; }
        public string? DisplayName { get; init; }
        public string? WindowTitle { get; init; }
        public string? Description { get; init; }
    }
}
