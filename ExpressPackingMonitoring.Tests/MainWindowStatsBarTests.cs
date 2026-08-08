using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class MainWindowStatsBarTests
{
    [Theory]
    [InlineData(500, 80, 120, 150, 16, 0)]
    [InlineData(382, 80, 120, 150, 16, 0)]
    [InlineData(300, 80, 120, 150, 16, 1)]
    [InlineData(216, 80, 120, 150, 16, 1)]
    [InlineData(215, 80, 120, 150, 16, 1)]
    [InlineData(214, 80, 120, 150, 16, 2)]
    [InlineData(10, 0, 0, 0, 0, 0)]
    public void ResolveStatsVisibility_PicksFirstFittingLayout(
        double availableWidth,
        double todayWidth,
        double averageWidth,
        double totalWidth,
        double gap,
        int expected)
    {
        Assert.Equal(
            (MainWindow.StatsBarVisibility)expected,
            MainWindow.ResolveStatsVisibility(
                availableWidth,
                todayWidth,
                averageWidth,
                totalWidth,
                gap));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(2, 0, 0)]
    public void ResolveGroupVisibility_MapsWithoutTotalToHiddenTotal(
        int visibility,
        int expectedAverageVisible,
        int expectedTotalVisible)
    {
        (bool averageVisible, bool totalVisible) =
            MainWindow.ResolveGroupVisibility((MainWindow.StatsBarVisibility)visibility);

        Assert.Equal(expectedAverageVisible == 1, averageVisible);
        Assert.Equal(expectedTotalVisible == 1, totalVisible);
    }

    [Theory]
    [InlineData(600, 100, 380, 176, 114, 0)]
    [InlineData(479, 100, 380, 176, 114, 0)]
    [InlineData(478, 100, 380, 176, 114, 1)]
    [InlineData(300, 100, 380, 176, 114, 1)]
    [InlineData(275, 100, 380, 176, 114, 1)]
    [InlineData(274, 100, 380, 176, 114, 2)]
    [InlineData(213, 100, 380, 176, 114, 2)]
    [InlineData(212, 100, 380, 176, 114, 2)]
    public void ResolveActionButtonLayout_HidesTextThenDataButton(
        double availableContentWidth,
        double onlyTodayWidth,
        double buttonsTextWidth,
        double buttonsIconWidth,
        double buttonsIconWithoutDataWidth,
        int expected)
    {
        Assert.Equal(
            (MainWindow.ActionButtonLayout)expected,
            MainWindow.ResolveActionButtonLayout(
                availableContentWidth,
                onlyTodayWidth,
                buttonsTextWidth,
                buttonsIconWidth,
                buttonsIconWithoutDataWidth));
    }

    [Fact]
    public void TopActionButtonWidths_UseStyleSettersSoCompactTriggerCanOverride()
    {
        string xaml = System.IO.File.ReadAllText(
            System.IO.Path.Combine(
                FindRepositoryPath("ExpressPackingMonitoring"),
                "UI",
                "MainWindow.xaml"));

        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"130\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Width\" Value=\"160\"/>", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MinWidth=\"130\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Width=\"160\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FontSize=\"22\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FontWeight=\"Bold\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"22\"/>", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Setter Property=\"FontSize\" Value=\"20\"/>", xaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryPath(params string[] parts)
    {
        System.IO.DirectoryInfo? directory = new(System.AppContext.BaseDirectory);
        while (directory != null)
        {
            string path = System.IO.Path.Combine([directory.FullName, .. parts]);
            if (System.IO.File.Exists(path) || System.IO.Directory.Exists(path))
                return path;
            directory = directory.Parent;
        }

        throw new System.IO.FileNotFoundException(
            string.Join(System.IO.Path.DirectorySeparatorChar, parts));
    }
}
