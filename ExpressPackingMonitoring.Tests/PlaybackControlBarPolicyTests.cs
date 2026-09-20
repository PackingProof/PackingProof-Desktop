using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class PlaybackControlBarPolicyTests
{
    /// <summary>窗口窄了按钮只留图标，把宽度让给进度条；宽了就恢复正常文字。</summary>
    [Theory]
    [InlineData(1000, false)]
    [InlineData(700, false)]
    [InlineData(619, true)]
    [InlineData(420, true)]
    [InlineData(0, false)]
    public void UseIconOnlyButtons_OnlyWhenControlBarIsNarrow(double width, bool expected)
    {
        Assert.Equal(expected, PlaybackControlBarPolicy.UseIconOnlyButtons(width));
    }

    /// <summary>时间只显示分:秒，分钟不封顶（2 小时 5 分 → 125:00）。</summary>
    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(1000, "00:01")]
    [InlineData(65_000, "01:05")]
    [InlineData(3_599_000, "59:59")]
    [InlineData(3_600_000, "60:00")]
    [InlineData(7_530_000, "125:30")]
    [InlineData(-500, "00:00")]
    public void Format_UsesMinutesAndSecondsWithoutHourRollover(long milliseconds, string expected)
    {
        Assert.Equal(expected, PlaybackTimeLabelFormatter.Format(milliseconds));
    }

    [Fact]
    public void FormatRange_JoinsCurrentAndTotal()
    {
        Assert.Equal("01:05 / 02:00", PlaybackTimeLabelFormatter.FormatRange(65_000, 120_000));
    }
}
