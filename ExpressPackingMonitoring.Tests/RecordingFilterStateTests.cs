using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 回放筛选的状态与徽章规则。
/// 徽章是店员判断"当前看到的是不是全部录像"的唯一线索，
/// 少显示一个就会让人以为没筛选。
/// </summary>
public sealed class RecordingFilterStateTests
{
    [Fact]
    public void NoFilters_ProducesNoBadges()
    {
        var state = new RecordingFilterState();

        Assert.False(state.HasAnyFilter);
        Assert.Equal(0, state.ActiveCount);
        Assert.Empty(state.BuildBadges());
    }

    /// <summary>徽章顺序固定为日期、来源、类型，与筛选面板排列一致。</summary>
    [Fact]
    public void BadgesFollowPanelOrder()
    {
        var state = new RecordingFilterState
        {
            StartDate = new DateTime(2026, 9, 1),
            EndDate = new DateTime(2026, 9, 13),
            SourceName = "打包台 A",
            Mode = "退货"
        };

        IReadOnlyList<RecordingFilterBadge> badges = state.BuildBadges();

        Assert.Equal(3, badges.Count);
        Assert.Equal(RecordingFilterState.DateKey, badges[0].Key);
        Assert.Equal(RecordingFilterState.SourceKey, badges[1].Key);
        Assert.Equal(RecordingFilterState.ModeKey, badges[2].Key);
        Assert.Equal(3, state.ActiveCount);
    }

    /// <summary>
    /// 只选了一头时要说明是"某日起"或"截至某日"。
    /// 写成区间会让人误以为另一头也被限制了。
    /// </summary>
    [Fact]
    public void OpenEndedDateRange_IsDescribedExplicitly()
    {
        Assert.Equal("2026-09-01 起",
            RecordingFilterState.FormatDateRange(new DateTime(2026, 9, 1), null));
        Assert.Equal("截至 2026-09-13",
            RecordingFilterState.FormatDateRange(null, new DateTime(2026, 9, 13)));
    }

    /// <summary>同一天不重复显示两遍。</summary>
    [Fact]
    public void SingleDayRange_ShowsOneDate()
    {
        var day = new DateTime(2026, 9, 13);

        Assert.Equal("2026-09-13", RecordingFilterState.FormatDateRange(day, day));
    }

    [Fact]
    public void FullRange_ShowsBothEnds()
    {
        Assert.Equal("2026-09-01 至 2026-09-13",
            RecordingFilterState.FormatDateRange(new DateTime(2026, 9, 1), new DateTime(2026, 9, 13)));
    }

    /// <summary>起止日期填反时交换，而不是查出空列表让人以为没有录像。</summary>
    [Fact]
    public void InvertedDateRange_IsSwapped()
    {
        var state = new RecordingFilterState
        {
            StartDate = new DateTime(2026, 9, 13),
            EndDate = new DateTime(2026, 9, 1)
        };

        state.NormalizeDateRange();

        Assert.Equal(new DateTime(2026, 9, 1), state.StartDate);
        Assert.Equal(new DateTime(2026, 9, 13), state.EndDate);
    }

    /// <summary>点徽章上的叉只清这一项，其它筛选要保留。</summary>
    [Fact]
    public void ClearingOneBadge_KeepsOtherFilters()
    {
        var state = new RecordingFilterState
        {
            StartDate = new DateTime(2026, 9, 1),
            SourceName = "打包台 A",
            SourceId = "dev-1",
            Mode = "退货"
        };

        state.Clear(RecordingFilterState.SourceKey);

        Assert.False(state.HasSourceFilter);
        Assert.Equal("", state.SourceId);
        Assert.True(state.HasDateFilter);
        Assert.True(state.HasModeFilter);
        Assert.Equal(2, state.ActiveCount);
    }

    [Fact]
    public void ClearAll_RemovesEveryFilter()
    {
        var state = new RecordingFilterState
        {
            StartDate = new DateTime(2026, 9, 1),
            EndDate = new DateTime(2026, 9, 13),
            SourceName = "打包台 A",
            SourceId = "dev-1",
            Mode = "发货"
        };

        state.ClearAll();

        Assert.False(state.HasAnyFilter);
        Assert.Empty(state.BuildBadges());
    }

    /// <summary>业务类型徽章显示中文，不能把库里的 shipping/return 直接抛给店员。</summary>
    [Theory]
    [InlineData("return", "退货")]
    [InlineData("shipping", "发货")]
    [InlineData("退货", "退货")]
    public void ModeBadge_ShowsChineseText(string mode, string expected)
    {
        var state = new RecordingFilterState { Mode = mode };

        RecordingFilterBadge badge = Assert.Single(state.BuildBadges());
        Assert.Equal(expected, badge.Text);
    }

    /// <summary>无法识别的类型值按"不筛选"处理，不能当成某一种类型。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("all")]
    [InlineData("unknown")]
    public void UnrecognizedMode_IsTreatedAsNoFilter(string mode)
    {
        var state = new RecordingFilterState { Mode = mode };

        Assert.False(state.HasModeFilter);
        Assert.Empty(state.BuildBadges());
    }

    /// <summary>
    /// 历史数据里发货可能存成 shipping、发货、空串或 NULL，
    /// 命中判断要与 SQL 的口径一致。
    /// </summary>
    [Theory]
    [InlineData("发货", "shipping", true)]
    [InlineData("shipping", "shipping", true)]
    [InlineData("", "shipping", true)]
    [InlineData(null, "shipping", true)]
    [InlineData("退货", "shipping", false)]
    [InlineData("退货", "return", true)]
    [InlineData("return", "return", true)]
    [InlineData("发货", "return", false)]
    [InlineData("退货", "", true)]
    public void ModeMatching_FollowsSqlSemantics(string? recordMode, string filter, bool expected)
    {
        Assert.Equal(expected, RecordingModeFilter.Matches(recordMode, filter));
    }

    /// <summary>没选日期时两个日历都只能选到今天，未来的日子不该出现。</summary>
    [Fact]
    public void DatePickerLimits_NeverAllowFutureDates()
    {
        var today = new DateTime(2026, 9, 13);

        (DateTime? startMax, DateTime? endMin, DateTime endMax) =
            RecordingFilterState.BuildDatePickerLimits(null, null, today);

        Assert.Equal(today, startMax);
        Assert.Equal(today, endMax);
        Assert.Null(endMin);
    }

    /// <summary>选了开始日期后，结束日期不能再选到它之前。</summary>
    [Fact]
    public void DatePickerLimits_EndCannotPrecedeStart()
    {
        var today = new DateTime(2026, 9, 13);

        (DateTime? _, DateTime? endMin, DateTime endMax) =
            RecordingFilterState.BuildDatePickerLimits(new DateTime(2026, 9, 5), null, today);

        Assert.Equal(new DateTime(2026, 9, 5), endMin);
        Assert.Equal(today, endMax);
    }

    /// <summary>选了结束日期后，开始日期的上限跟着收到结束日期。</summary>
    [Fact]
    public void DatePickerLimits_StartCannotExceedEnd()
    {
        var today = new DateTime(2026, 9, 13);

        (DateTime? startMax, DateTime? _, DateTime _) =
            RecordingFilterState.BuildDatePickerLimits(null, new DateTime(2026, 9, 5), today);

        Assert.Equal(new DateTime(2026, 9, 5), startMax);
    }
}
