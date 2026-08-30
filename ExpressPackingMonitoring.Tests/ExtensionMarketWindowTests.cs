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
            itemStyle.Elements(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Background"
                && (string?)setter.Attribute("Value") == "{DynamicResource ControlBackground}");
        Assert.Contains(
            itemStyle.Elements(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "BorderBrush"
                && (string?)setter.Attribute("Value") == "{DynamicResource BorderDefault}");
        Assert.Contains(
            itemStyle.Descendants(Presentation + "Border"),
            border => (string?)border.Attribute("CornerRadius") == "8");
        Assert.Contains(
            itemStyle.Descendants(Presentation + "Trigger"),
            trigger => (string?)trigger.Attribute("Property") == "IsMouseOver"
                && trigger.Descendants(Presentation + "Setter").Any(
                    setter => (string?)setter.Attribute("Value") == "{DynamicResource ControlBackgroundHover}"));
        Assert.Contains(
            itemStyle.Descendants(Presentation + "Trigger"),
            trigger => (string?)trigger.Attribute("Property") == "IsSelected"
                && trigger.Descendants(Presentation + "Setter").Any(
                    setter => (string?)setter.Attribute("Value") == "{DynamicResource BadgePackBg}")
                && trigger.Descendants(Presentation + "Setter").Any(
                    setter => (string?)setter.Attribute("Value") == "{DynamicResource AccentBlue}"));

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
        Assert.DoesNotContain(
            list.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding Badge}");

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

        XElement separator = Assert.Single(
            document.Descendants(Presentation + "Border"),
            element => (string?)element.Attribute(Xaml + "Name") == "DetailsSeparator");
        Assert.Equal("1", (string?)separator.Attribute("Height"));
        Assert.Equal("{DynamicResource BorderDefault}", (string?)separator.Attribute("Background"));

        XElement sourceTag = Assert.Single(
            document.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute(Xaml + "Name") == "SourceTagText");
        XElement typeTag = Assert.Single(
            document.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute(Xaml + "Name") == "TypeTagText");
        Assert.Equal("开源", (string?)sourceTag.Attribute("Text"));
        Assert.Equal("脚本", (string?)typeTag.Attribute("Text"));
        Assert.Equal("{DynamicResource AccentBlue}", (string?)typeTag.Attribute("Foreground"));
        XElement tagBorderStyle = Assert.Single(
            document.Descendants(Presentation + "Style"),
            element => (string?)element.Attribute(Xaml + "Key") == "MarketTagBorderStyle");
        Assert.Contains(
            tagBorderStyle.Descendants(Presentation + "Trigger"),
            trigger => (string?)trigger.Attribute("Value") == "闭源"
                && trigger.Descendants(Presentation + "Setter").Any(
                    setter => (string?)setter.Attribute("Value") == "{DynamicResource AccentOrange}"));
        XElement sourceTagTextStyle = Assert.Single(
            document.Descendants(Presentation + "Style"),
            element => (string?)element.Attribute(Xaml + "Key") == "MarketSourceTagTextStyle");
        Assert.Contains(
            sourceTagTextStyle.Descendants(Presentation + "Trigger"),
            trigger => (string?)trigger.Attribute("Property") == "Tag"
                && (string?)trigger.Attribute("Value") == "闭源");

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

    [Fact]
    public void ExternalAdapterRemovalPromptsBeforeTerminatingRunningProgram()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "ExtensionMarketWindow.xaml.cs"));

        Assert.Contains("ExtensionProcessManager.FindRunningProcesses", source, StringComparison.Ordinal);
        Assert.Contains("终止并删除", source, StringComparison.Ordinal);
        Assert.Contains("请手动退出程序后重试", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalAdapterInstallOpensExtractedDirectoryWithoutLaunchingPayload()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "ExtensionMarketWindow.xaml.cs"));

        Assert.Equal(
            2,
            source.Split("OpenInstalledExtensionDirectory(result.Record);", StringSplitOptions.None).Length - 1);
        Assert.Contains("if (record.Type != \"external-adapter\") return;", source, StringComparison.Ordinal);
        Assert.Contains("OpenInstalledExtensionLocation(record);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ProcessStartInfo(\"explorer.exe\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InstalledUserscriptCanLocateManagedSourceFile()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "ExtensionMarketWindow.xaml.cs"));

        Assert.Contains(
            "OpenFolderButton.Visibility = installed != null ? Visibility.Visible : Visibility.Collapsed;",
            source,
            StringComparison.Ordinal);
        Assert.Contains("OpenInstalledExtensionLocation(installed);", source, StringComparison.Ordinal);
        Assert.Contains("GetInstalledLocationPath(record)", source, StringComparison.Ordinal);
        Assert.Contains("WindowsShellFileLocator.Locate(locationPath)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalPackageInstallRefreshesBothCatalogItemAndSelectedDetails()
    {
        string source = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "ExtensionMarketWindow.xaml.cs"));
        int localInstallStart = source.IndexOf("private async void InstallLocal_Click", StringComparison.Ordinal);
        int nextMethod = source.IndexOf("private void ImportLegacyUserscript", localInstallStart, StringComparison.Ordinal);
        string localInstall = source[localInstallStart..nextMethod];

        Assert.Contains("RefreshInstalledState(result.Record.Id);", localInstall, StringComparison.Ordinal);
        Assert.Contains("RefreshDisplayItems();", source, StringComparison.Ordinal);
        Assert.Contains("UpdateActionState(selected);", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(selected.Item.Id, extensionId", source, StringComparison.Ordinal);
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
