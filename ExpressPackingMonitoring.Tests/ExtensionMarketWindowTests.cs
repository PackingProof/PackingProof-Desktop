using ExpressPackingMonitoring.Services.Extensions;
using ExpressPackingMonitoring.UI;
using System.Xml.Linq;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionMarketWindowTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";
    [Fact]
    public void OtherVersionSelectionExcludesLatestAndUnavailableVersions()
    {
        var latest = new ExtensionMarketRelease { Version = "2.0.0" };
        var history = new ExtensionMarketRelease { Version = "1.5.0" };
        var withdrawn = new ExtensionMarketRelease { Version = "1.0.0" };
        var details = new ExtensionMarketDetails
        {
            Versions =
            [
                new ExtensionMarketVersionEntry { Release = latest, Status = "available" },
                new ExtensionMarketVersionEntry { Release = history, Status = "available" },
                new ExtensionMarketVersionEntry { Release = withdrawn, Status = "withdrawn" }
            ]
        };

        ExtensionMarketRelease selected = Assert.Single(
            ExtensionMarketWindow.GetOtherAvailableReleases(details, latest));

        Assert.Same(history, selected);
    }

    [Fact]
    public void CatalogItemShowsAuthorOnLeftAndInstallStatusOnRight()
    {
        var item = new ExtensionMarketDisplayItem(
            new ExtensionMarketCatalogItem
            {
                LatestVersion = "2.0.0",
                Publisher = new ExtensionMarketPublisher { DisplayName = "PackingProof" }
            },
            new InstalledExtensionRecord { Version = "1.5.0" });

        Assert.Equal("PackingProof", item.AuthorText);
        Assert.Equal("待更新", item.StatusText);

        item.UpdateInstalled(new InstalledExtensionRecord { Version = "2.0.0" });
        Assert.Equal("已安装", item.StatusText);

        item.UpdateInstalled(null);
        Assert.Equal("未安装", item.StatusText);

        item.SetDownloading(true);
        Assert.Equal("下载中", item.StatusText);

        item.UpdateInstalled(new InstalledExtensionRecord { Version = "2.0.0" });
        Assert.Equal("下载中", item.StatusText);

        item.SetDownloading(false);
        Assert.Equal("已安装", item.StatusText);
    }

    [Theory]
    [InlineData("open-source", "userscript", "开源", "脚本")]
    [InlineData("closed-source", "userscript", "闭源", "脚本")]
    [InlineData("open-source", "external-adapter", "开源", "适配器")]
    [InlineData("closed-source", "external-adapter", "闭源", "适配器")]
    public void CatalogItemMapsSourceAndTypeLabels(
        string sourceAvailability,
        string type,
        string expectedSource,
        string expectedType)
    {
        var item = new ExtensionMarketDisplayItem(
            new ExtensionMarketCatalogItem
            {
                SourceAvailability = sourceAvailability,
                Type = type
            },
            null);

        Assert.Equal(expectedSource, item.SourceLabel);
        Assert.Equal(expectedType, item.TypeLabel);
    }

    [Theory]
    [InlineData("https://example.com/project", true)]
    [InlineData("http://example.com/author", true)]
    [InlineData("file:///C:/secret.txt", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void ExternalLinksOnlyAllowHttpAndHttps(string value, bool expected)
    {
        Assert.Equal(expected, ExtensionMarketWindow.TryCreateExternalUri(value) != null);
    }
    [Theory]
    [InlineData(37748736, 72561459, "36.0 MB / 69.2 MB · 52%")]
    [InlineData(2048, 0, "2.0 KB")]
    [InlineData(0, 0, "")]
    public void DownloadProgressFormatsSizeAndPercentage(long received, long total, string expected)
    {
        Assert.Equal(expected, ExtensionMarketWindow.FormatDownloadProgress(received, total));
    }

    [Theory]
    [InlineData(false, "市场目录签名验证通过，下载时优先使用 Gitee")]
    [InlineData(true, "网络市场暂不可用，正在显示最近一次已验签缓存")]
    public void MarketReadyStatusReflectsActiveCatalogSource(bool isCached, string expected)
    {
        Assert.Equal(expected, ExtensionMarketWindow.GetMarketReadyStatus(isCached));
    }
}
