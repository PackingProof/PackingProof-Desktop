using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class StatisticsWindowTests
{
    private static readonly DateTime Now = new(2026, 7, 23, 15, 30, 0);

    [Theory]
    [InlineData("Last7", "2026-07-17", "2026-07-23")]
    [InlineData("Last30", "2026-06-24", "2026-07-23")]
    [InlineData("LastYear", "2025-07-24", "2026-07-23")]
    [InlineData("Month", "2026-07-01", "2026-07-23")]
    [InlineData("All", "2024-07-23", "2026-07-23")]
    public void GetPresetRange_ReturnsInclusiveDateRange(string tag, string expectedStart, string expectedEnd)
    {
        (DateTime start, DateTime end) = StatisticsWindow.GetPresetRange(tag, Now);

        Assert.Equal(DateTime.Parse(expectedStart), start);
        Assert.Equal(DateTime.Parse(expectedEnd), end);
    }

    [Fact]
    public void GetPresetRange_UsesMondayAsStartOfWeek()
    {
        (DateTime start, DateTime end) = StatisticsWindow.GetPresetRange("Week", Now);

        Assert.Equal(new DateTime(2026, 7, 20), start);
        Assert.Equal(new DateTime(2026, 7, 23), end);
    }

    [Fact]
    public void GetPresetRange_LastYearKeepsInclusiveRollingYearAcrossLeapDay()
    {
        (DateTime start, DateTime end) = StatisticsWindow.GetPresetRange(
            "LastYear",
            new DateTime(2024, 2, 29, 12, 0, 0));

        Assert.Equal(new DateTime(2023, 3, 1), start);
        Assert.Equal(new DateTime(2024, 2, 29), end);
    }

    [Fact]
    public void MetricButtons_RenderCachedDataWithoutStartingAnotherQuery()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "StatisticsWindow.xaml"));

        Assert.Equal(3, CountOccurrences(xaml, "Click=\"OnMetricChanged\""));
        Assert.DoesNotContain("Click=\"OnQueryFilterChanged\"", xaml);
        Assert.Contains("SelectionChanged=\"OnQueryFilterChanged\"", xaml);
        Assert.Contains("SelectedDateChanged=\"OnQueryFilterChanged\"", xaml);
    }

    [Fact]
    public void ChartLayout_UsesDynamicHeightImmediateTooltipsAndSeparateAxisLabels()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "StatisticsWindow.xaml"));

        Assert.Contains("Content=\"最近1年\" Tag=\"LastYear\"", xaml);
        Assert.Contains("Content=\"按年\" Tag=\"year\"", xaml);
        Assert.Contains("x:Name=\"ChartPlot\"", xaml);
        Assert.Contains("<RowDefinition Height=\"*\"/>", xaml);
        Assert.DoesNotContain("<RowDefinition Height=\"320\"/>", xaml);
        Assert.Contains("ToolTipService.InitialShowDelay=\"0\"", xaml);
        Assert.Contains("ToolTipService.BetweenShowDelay=\"0\"", xaml);
        Assert.Contains("ScaleY=\"{Binding BarRatio, Mode=OneWay}\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding XAxisLabels, Mode=OneWay}\"", xaml);
    }

    [Fact]
    public void BuildAxisLabels_SamplesReadableTicksAndKeepsBothEnds()
    {
        List<ChartItem> items = Enumerable.Range(1, 100)
            .Select(index => new ChartItem
            {
                DateLabel = $"日期{index}",
                DateSub = $"说明{index}"
            })
            .ToList();

        IReadOnlyList<ChartAxisLabel> wide = StatisticsWindow.BuildAxisLabels(items, 1200);
        IReadOnlyList<ChartAxisLabel> narrow = StatisticsWindow.BuildAxisLabels(items, 330);

        Assert.Equal(8, wide.Count);
        Assert.Equal("日期1", wide[0].DateLabel);
        Assert.Equal("日期100", wide[^1].DateLabel);
        Assert.Equal(3, narrow.Count);
        Assert.Equal("日期1", narrow[0].DateLabel);
        Assert.Equal("日期100", narrow[^1].DateLabel);
    }

    [Theory]
    [InlineData("2026-07-31", "day", "07-31", "周五", "2026年7月31日 周五")]
    [InlineData("2026-W30", "week", "2026", "第30周", "2026年第30周")]
    [InlineData("2026-07", "month", "2026", "07月", "2026年7月")]
    [InlineData("2026", "year", "2026年", "", "2026年")]
    public void FormatChartDate_UsesReadableLabelsForEveryAggregation(
        string value,
        string groupMode,
        string expectedLabel,
        string expectedSubLabel,
        string expectedFullLabel)
    {
        (string label, string subLabel, string fullLabel) = StatisticsWindow.FormatChartDate(value, groupMode);

        Assert.Equal(expectedLabel, label);
        Assert.Equal(expectedSubLabel, subLabel);
        Assert.Equal(expectedFullLabel, fullLabel);
    }

    private static int CountOccurrences(string value, string expected)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(expected, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += expected.Length;
        }
        return count;
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        foreach (string startPath in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? directory = new(startPath);
            while (directory != null)
            {
                string candidate = Path.Combine([directory.FullName, .. relativeParts]);
                if (File.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException(
            $"无法定位仓库文件：{Path.Combine(relativeParts)}");
    }
}
