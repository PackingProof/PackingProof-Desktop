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
    public void CatalogListUsesThemeColorsRoundedItemsAndNoHorizontalScrollbar()
    {
        XDocument document = XDocument.Load(FindRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "ExtensionMarketWindow.xaml"));
        XElement list = Assert.Single(
            document.Descendants(Presentation + "ListBox"),
            element => (string?)element.Attribute(Xaml + "Name") == "CatalogList");

        Assert.Equal("{DynamicResource TextPrimary}", (string?)list.Attribute("Foreground"));
        Assert.Equal("Disabled", (string?)list.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));

        XElement itemStyle = Assert.Single(
            list.Descendants(Presentation + "Style"),
            element => (string?)element.Attribute("TargetType") == "ListBoxItem");
        Assert.Contains(
            itemStyle.Descendants(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "HorizontalContentAlignment"
                && (string?)setter.Attribute("Value") == "Stretch");
        Assert.Contains(
            itemStyle.Descendants(Presentation + "Border"),
            border => (string?)border.Attribute("CornerRadius") == "8");

        XElement name = Assert.Single(
            list.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding Name}");
        Assert.Equal("{DynamicResource TextPrimary}", (string?)name.Attribute("Foreground"));
        Assert.Equal("CharacterEllipsis", (string?)name.Attribute("TextTrimming"));
        Assert.Contains(
            document.Descendants(Presentation + "ColumnDefinition"),
            column => (string?)column.Attribute("Width") == "360");

        Assert.Contains(
            list.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding AuthorText}"
                && (string?)element.Attribute("Grid.Column") == null);
        Assert.Contains(
            list.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding StatusText}"
                && (string?)element.Attribute("Grid.Column") == "1");
        XElement summary = Assert.Single(
            list.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding Summary}");
        Assert.Equal("NoWrap", (string?)summary.Attribute("TextWrapping"));
        Assert.Equal("CharacterEllipsis", (string?)summary.Attribute("TextTrimming"));
        XElement badge = Assert.Single(
            list.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding Badge}");
        Assert.Equal("1", (string?)badge.Attribute("Grid.Column"));
        Assert.Equal("Right", (string?)badge.Attribute("HorizontalAlignment"));

        XElement status = Assert.Single(
            list.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding StatusText}");
        Assert.Equal(
            "{StaticResource MarketInstallStatusTextStyle}",
            (string?)status.Attribute("Style"));
        XElement statusStyle = Assert.Single(
            document.Descendants(Presentation + "Style"),
            element => (string?)element.Attribute(Xaml + "Key") == "MarketInstallStatusTextStyle");
        Assert.Contains(
            statusStyle.Descendants(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Foreground"
                && (string?)setter.Attribute("Value") == "{DynamicResource TextMuted}");
        Assert.Contains(
            statusStyle.Descendants(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Value") == "{DynamicResource AccentGreen}");
        Assert.Contains(
            statusStyle.Descendants(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Value") == "{DynamicResource AccentOrange}");
        Assert.Contains(
            statusStyle.Descendants(Presentation + "Trigger"),
            trigger => (string?)trigger.Attribute("Value") == "下载中"
                && trigger.Descendants(Presentation + "Setter").Any(
                    setter => (string?)setter.Attribute("Value") == "{DynamicResource AccentBlue}"));
    }

    [Fact]
    public void DownloadProgressUsesDedicatedThemeAwareFooter()
    {
        XDocument document = XDocument.Load(FindRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "ExtensionMarketWindow.xaml"));
        XElement panel = Assert.Single(
            document.Descendants(Presentation + "Grid"),
            element => (string?)element.Attribute(Xaml + "Name") == "DownloadProgressPanel");
        XElement progress = Assert.Single(
            panel.Descendants(Presentation + "ProgressBar"),
            element => (string?)element.Attribute(Xaml + "Name") == "DownloadProgress");

        Assert.Equal("Collapsed", (string?)panel.Attribute("Visibility"));
        Assert.Equal("{DynamicResource ControlBackground}", (string?)progress.Attribute("Background"));
        Assert.Equal("{DynamicResource AccentBlue}", (string?)progress.Attribute("Foreground"));
        Assert.Contains(
            panel.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute(Xaml + "Name") == "DownloadStatusText");
        Assert.Contains(
            panel.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute(Xaml + "Name") == "DownloadProgressText"
                && (string?)element.Attribute("Grid.Column") == "1");

        XElement footer = Assert.IsType<XElement>(panel.Parent);
        XElement close = Assert.Single(
            footer.Elements(Presentation + "Button"),
            element => (string?)element.Attribute("Content") == "关闭");
        Assert.Equal("1", (string?)close.Attribute("Grid.Column"));
    }

    [Fact]
    public void DetailsKeepVersionsBesidePublisherAndReuseDeleteButtonStyle()
    {
        XDocument document = XDocument.Load(FindRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "ExtensionMarketWindow.xaml"));
        XElement publisher = Assert.Single(
            document.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute(Xaml + "Name") == "PublisherText");
        XElement versionsRow = Assert.IsType<XElement>(publisher.Parent);

        Assert.Equal(Presentation + "WrapPanel", versionsRow.Name);
        Assert.Contains(
            versionsRow.Elements(Presentation + "TextBlock"),
            element => (string?)element.Attribute(Xaml + "Name") == "LatestVersionText");
        Assert.Contains(
            versionsRow.Elements(Presentation + "TextBlock"),
            element => (string?)element.Attribute(Xaml + "Name") == "InstalledVersionText");
        Assert.All(
            versionsRow.Elements(Presentation + "TextBlock"),
            element => Assert.Equal(
                "{StaticResource MarketMetadataTextStyle}",
                (string?)element.Attribute("Style")));

        XElement otherVersions = Assert.Single(
            document.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute(Xaml + "Name") == "InstallOtherVersionButton");
        Assert.Equal("False", (string?)otherVersions.Attribute("IsEnabled"));

        XElement remove = Assert.Single(
            document.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute(Xaml + "Name") == "RemoveButton");
        Assert.Equal("{StaticResource DeleteButtonStyle}", (string?)remove.Attribute("Style"));

        XDocument buttonTheme = XDocument.Load(FindRepositoryFile(
            "ExpressPackingMonitoring",
            "Themes",
            "ButtonTheme.xaml"));
        XElement deleteStyle = Assert.Single(
            buttonTheme.Descendants(Presentation + "Style"),
            element => (string?)element.Attribute(Xaml + "Key") == "DeleteButtonStyle");
        Assert.Contains(
            deleteStyle.Descendants(Presentation + "Trigger"),
            trigger => (string?)trigger.Attribute("Property") == "IsMouseOver"
                && (string?)trigger.Attribute("Value") == "True");

        XDocument settings = XDocument.Load(FindRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml"));
        XElement userscriptDeleteStyle = Assert.Single(
            settings.Descendants(Presentation + "Style"),
            element => (string?)element.Attribute(Xaml + "Key") == "DeleteUserscriptButtonStyle");
        Assert.Equal(
            "{StaticResource DeleteButtonStyle}",
            (string?)userscriptDeleteStyle.Attribute("BasedOn"));
    }

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

    private static string FindRepositoryFile(params string[] parts)
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(startPath);
            while (directory != null)
            {
                string candidate = Path.Combine([directory.FullName, .. parts]);
                if (File.Exists(candidate))
                    return candidate;

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"找不到文件：{Path.Combine(parts)}");
    }
}
