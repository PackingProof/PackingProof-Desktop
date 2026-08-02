using System.Text.RegularExpressions;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class UiColorTokenTests
{
    [Fact]
    public void WpfColors_AreDeclaredOnlyInColorTokens()
    {
        string root = FindRepositoryRoot();
        string project = Path.Combine(root, "ExpressPackingMonitoring");
        string tokenPath = Path.Combine(project, "Themes", "ColorTokens.xaml");
        string appXaml = File.ReadAllText(Path.Combine(project, "App.xaml"));

        Assert.True(File.Exists(tokenPath));
        Assert.True(
            appXaml.IndexOf("Themes/ColorTokens.xaml", StringComparison.Ordinal) <
            appXaml.IndexOf("Themes/LightTheme.xaml", StringComparison.Ordinal));

        string[] xamlFiles = Directory.GetFiles(project, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, tokenPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string[] codeFiles = Directory.GetFiles(Path.Combine(project, "UI"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(project, "Workstations"), "*.cs", SearchOption.AllDirectories))
            .Append(Path.Combine(project, "Helpers", "BarcodeHelper.cs"))
            .ToArray();
        var forbidden = new Regex(
            "#[0-9A-Fa-f]{3,8}\\b|" +
            "(?:Background|Foreground|BorderBrush|Fill|Stroke|Color)=\"(?:White|Black|Transparent|Gray|Red|Green|Blue|Orange|Yellow|Purple|Cyan)\"|" +
            "Setter\\s+Property=\"(?:Background|Foreground|BorderBrush|Fill|Stroke)\"\\s+Value=\"(?:White|Black|Transparent|Gray|Red|Green|Blue|Orange|Yellow|Purple|Cyan)\"|" +
            "(?:Brushes\\.[A-Za-z]+|Colors\\.[A-Za-z]+|ColorConverter\\.ConvertFromString)",
            RegexOptions.CultureInvariant);

        string[] violations = xamlFiles.Concat(codeFiles)
            .SelectMany(path => forbidden.Matches(File.ReadAllText(path))
                .Select(match => $"{Path.GetRelativePath(project, path)}: {match.Value}"))
            .ToArray();

        Assert.True(violations.Length == 0, "Hard-coded WPF colors: " + string.Join(" | ", violations));
    }

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("找不到解决方案根目录");
    }
}
