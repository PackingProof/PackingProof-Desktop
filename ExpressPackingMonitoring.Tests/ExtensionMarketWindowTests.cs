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
