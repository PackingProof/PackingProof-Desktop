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
    [InlineData(600, 100, 380, 250, false)]
    [InlineData(480, 100, 380, 250, false)]
    [InlineData(479, 100, 380, 250, false)]
    [InlineData(478, 100, 380, 250, true)]
    [InlineData(450, 100, 380, 250, true)]
    [InlineData(350, 100, 380, 250, true)]
    [InlineData(348, 100, 380, 250, false)]
    public void ShouldHideDataButton_OnlyWhenOnlyTodayStillOverflows(
        double availableContentWidth,
        double onlyTodayWidth,
        double buttonsAllWidth,
        double buttonsWithoutDataWidth,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.ShouldHideDataButton(
                availableContentWidth,
                onlyTodayWidth,
                buttonsAllWidth,
                buttonsWithoutDataWidth));
    }
}
