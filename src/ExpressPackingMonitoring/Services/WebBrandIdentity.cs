using System.Text.Json;
using ExpressPackingMonitoring.UpdateCore;

namespace ExpressPackingMonitoring.Services;

internal static class WebBrandIdentity
{
    private static bool UsesDefaultCompatibilityIdentity =>
        string.Equals(
            BrandMetadata.CompatibilityId,
            "PackingProof",
            StringComparison.OrdinalIgnoreCase);

    internal static string FirewallRuleName =>
        UsesDefaultCompatibilityIdentity
            ? "快递打包监控 Web服务"
            : BrandMetadata.ArtifactPrefix + " Web服务";

    internal static string UdpFirewallRuleName =>
        UsesDefaultCompatibilityIdentity
            ? "快递打包监控 设备发现"
            : BrandMetadata.ArtifactPrefix + " 设备发现";

    internal static string BuildBrandingJson(bool mobileAppEnabled) => JsonSerializer.Serialize(new
    {
        legacyProductName = BrandMetadata.LegacyProductName,
        productName = BrandMetadata.ProductName,
        windowTitle = BrandMetadata.WindowTitle,
        displayName = BrandMetadata.DisplayName,
        websiteUrl = BrandMetadata.WebsiteUrl,
        mobileReleasesUrl = mobileAppEnabled
            ? BrandMetadata.MobileReleasesUrl
            : ""
    });
}
