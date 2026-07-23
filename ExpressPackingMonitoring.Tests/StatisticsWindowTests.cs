using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class StatisticsWindowTests
{
    private static readonly DateTime Now = new(2026, 7, 23, 15, 30, 0);

    [Theory]
    [InlineData("Last7", "2026-07-17", "2026-07-23")]
    [InlineData("Last30", "2026-06-24", "2026-07-23")]
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
    public void MetricButtons_RenderCachedDataWithoutStartingAnotherQuery()
    {
        string xaml = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "ExpressPackingMonitoring", "UI", "StatisticsWindow.xaml"));

        Assert.Equal(3, CountOccurrences(xaml, "Click=\"OnMetricChanged\""));
        Assert.DoesNotContain("Click=\"OnQueryFilterChanged\"", xaml);
        Assert.Contains("SelectionChanged=\"OnQueryFilterChanged\"", xaml);
        Assert.Contains("SelectedDateChanged=\"OnQueryFilterChanged\"", xaml);
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
}
