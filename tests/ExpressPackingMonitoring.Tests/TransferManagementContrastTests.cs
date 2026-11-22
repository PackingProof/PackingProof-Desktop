using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class TransferManagementContrastTests
{
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("LightTheme.xaml")]
    [InlineData("DarkTheme.xaml")]
    public void TransferTextPairsMeetWcagAaContrast(string themeFile)
    {
        string project = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        XDocument colors = XDocument.Load(Path.Combine(project, "Themes", "ColorTokens.xaml"));
        XDocument theme = XDocument.Load(Path.Combine(project, "Themes", themeFile));

        var pairs = new[]
        {
            ("TextPrimary", "PanelBackground"),
            ("TextPrimary", "ControlBackground"),
            ("TextPrimary", "ActionHoverBackground"),
            ("TextSecondaryStrong", "PanelBackground"),
            ("TextSecondaryStrong", "ControlBackground"),
            ("TextSecondaryStrong", "TransferFailureSurface"),
            ("TransferAttentionText", "TransferAttentionSurface"),
            ("TransferInfoText", "ControlBackground")
        };
        foreach ((string foregroundKey, string backgroundKey) in pairs)
        {
            string foreground = ResolveColor(foregroundKey, theme, colors);
            string background = ResolveColor(backgroundKey, theme, colors);
            double ratio = ContrastRatio(foreground, background);
            Assert.True(
                ratio >= 4.5,
                $"{themeFile} {foregroundKey}/{backgroundKey} contrast={ratio:F2}, expected >= 4.50");
        }
    }

    [Fact]
    public void TransferWindowTextDoesNotRelyOnInheritedForeground()
    {
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "TransferManagementWindow.xaml"));
        string[] names =
        [
            "Text=\"{Binding DisplayName}\"",
            "Text=\"{Binding ActivityText}\"",
            "Text=\"{Binding TodayReceivedCountText}\"",
            "Text=\"{Binding FooterText}\""
        ];
        foreach (string name in names)
        {
            int offset = xaml.IndexOf(name, StringComparison.Ordinal);
            Assert.True(offset >= 0, $"missing text binding: {name}");
            int end = xaml.IndexOf("/>", offset, StringComparison.Ordinal);
            Assert.True(end > offset, $"unterminated text block: {name}");
            Assert.Contains(
                "Foreground=\"{DynamicResource",
                xaml[offset..end],
                StringComparison.Ordinal);
        }
    }

    private static string ResolveColor(
        string key,
        XDocument theme,
        XDocument colors)
    {
        XElement element = FindKey(theme, key)
            ?? throw new InvalidDataException($"Missing resource {key}");
        string value = element.Attribute("Color")?.Value ?? "";
        if (value.StartsWith("{DynamicResource ", StringComparison.Ordinal)
            || value.StartsWith("{StaticResource ", StringComparison.Ordinal))
        {
            int start = value.IndexOf(' ') + 1;
            int end = value.IndexOf('}', start);
            string colorKey = value[start..end].Trim();
            XElement color = FindKey(colors, colorKey)
                ?? throw new InvalidDataException($"Missing color {colorKey}");
            value = color.Value;
        }
        return value.Trim().TrimStart('#');
    }

    private static XElement? FindKey(XDocument document, string key) =>
        document.Descendants()
            .FirstOrDefault(element =>
                string.Equals(
                    element.Attribute(Xaml + "Key")?.Value,
                    key,
                    StringComparison.Ordinal));

    private static double ContrastRatio(string first, string second)
    {
        double firstLuminance = RelativeLuminance(first);
        double secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05)
            / (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double RelativeLuminance(string argb)
    {
        string rgb = argb.Length == 8 ? argb[2..] : argb;
        double[] channels = Enumerable.Range(0, 3)
            .Select(index => int.Parse(
                rgb.Substring(index * 2, 2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture) / 255d)
            .Select(value => value <= 0.03928
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4))
            .ToArray();
        return 0.2126 * channels[0]
            + 0.7152 * channels[1]
            + 0.0722 * channels[2];
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到仓库根目录");
    }
}
