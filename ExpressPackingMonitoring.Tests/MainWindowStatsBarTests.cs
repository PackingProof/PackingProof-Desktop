using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class MainWindowStatsBarTests
{
    [Theory]
    [InlineData(800, 100, 120, 150, 16, 380, 176, 0)]
    [InlineData(781, 100, 120, 150, 16, 380, 176, 0)]
    [InlineData(780, 100, 120, 150, 16, 380, 176, 1)]
    [InlineData(577, 100, 120, 150, 16, 380, 176, 1)]
    [InlineData(576, 100, 120, 150, 16, 380, 176, 2)]
    [InlineData(411, 100, 120, 150, 16, 380, 176, 2)]
    [InlineData(410, 100, 120, 150, 16, 380, 176, 3)]
    [InlineData(275, 100, 120, 150, 16, 380, 176, 3)]
    [InlineData(274, 100, 120, 150, 16, 380, 176, 4)]
    [InlineData(212, 100, 120, 150, 16, 380, 176, 4)]
    public void ResolveBottomBarLayout_PicksFirstFittingLayout(
        double availableContentWidth,
        double todayWidth,
        double averageWidth,
        double totalWidth,
        double gap,
        double buttonsTextWidth,
        double buttonsIconWidth,
        int expected)
    {
        Assert.Equal(
            (MainWindow.BottomBarLayout)expected,
            MainWindow.ResolveBottomBarLayout(
                availableContentWidth,
                todayWidth,
                averageWidth,
                totalWidth,
                gap,
                buttonsTextWidth,
                buttonsIconWidth));
    }

    [Theory]
    [InlineData(0, 1, 1, 0)]
    [InlineData(1, 1, 1, 1)]
    [InlineData(2, 1, 0, 1)]
    [InlineData(3, 0, 0, 1)]
    [InlineData(4, 0, 0, 2)]
    public void ResolveBottomBarVisibility_MapsEachLayout(
        int layout,
        int expectedAverageVisible,
        int expectedTotalVisible,
        int expectedButtons)
    {
        (bool averageVisible, bool totalVisible, MainWindow.ActionButtonLayout buttons) =
            MainWindow.ResolveBottomBarVisibility((MainWindow.BottomBarLayout)layout);

        Assert.Equal(expectedAverageVisible == 1, averageVisible);
        Assert.Equal(expectedTotalVisible == 1, totalVisible);
        Assert.Equal((MainWindow.ActionButtonLayout)expectedButtons, buttons);
    }

    [Theory]
    [InlineData(800, 130, 160, 52, 16, 20, 320, 0)]
    [InlineData(646, 130, 160, 52, 16, 20, 320, 0)]
    [InlineData(645, 130, 160, 52, 16, 20, 320, 0)]
    [InlineData(644, 130, 160, 52, 16, 20, 320, 1)]
    [InlineData(568, 130, 160, 52, 16, 20, 320, 1)]
    [InlineData(567, 130, 160, 52, 16, 20, 320, 1)]
    [InlineData(566, 130, 160, 52, 16, 20, 320, 2)]
    [InlineData(460, 130, 160, 52, 16, 20, 320, 2)]
    [InlineData(459, 130, 160, 52, 16, 20, 320, 2)]
    public void ResolveTopBarCompactState_PrefersKeepingRecordButtonText(
        double availableWidth,
        double modeTextWidth,
        double recordTextWidth,
        double iconWidth,
        double modeRightMargin,
        double columnGap,
        double minimumScanWidth,
        int expected)
    {
        Assert.Equal(
            (MainWindow.TopBarCompactState)expected,
            MainWindow.ResolveTopBarCompactState(
                availableWidth,
                modeTextWidth,
                recordTextWidth,
                iconWidth,
                modeRightMargin,
                columnGap,
                minimumScanWidth));
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
        Assert.DoesNotContain("IsCompactLayout", xaml, StringComparison.Ordinal);
        Assert.Contains("IsModeButtonCompact", xaml, StringComparison.Ordinal);
        Assert.Contains("IsRecordButtonCompact", xaml, StringComparison.Ordinal);
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
