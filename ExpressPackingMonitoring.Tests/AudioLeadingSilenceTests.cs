using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 音频起点补静音：原来只按标称预录时长补，漏掉了麦克风设备解析与建文件本身的耗时，
/// 整条音轨会晚 0.1~0.25 秒。改成按"距时间轴起点已经过去多久"计算。
/// </summary>
public sealed class AudioLeadingSilenceTests
{
    [Fact]
    public void SilenceCoversPreRecordAndDeviceStartup()
    {
        var timelineStart = new DateTime(2026, 9, 16, 12, 0, 0);

        // 预录 5 秒，设备启动又用了 0.2 秒：补 5.2 秒
        double seconds = MainViewModel.CalculateLeadingSilenceSeconds(
            timelineStart,
            timelineStart.AddSeconds(5.2));

        Assert.Equal(5.2, seconds, precision: 3);
    }

    /// <summary>没有预录时也要补上设备启动那一段，否则音轨整体提前。</summary>
    [Fact]
    public void SilenceStillCoversStartupWithoutPreRecord()
    {
        var timelineStart = new DateTime(2026, 9, 16, 12, 0, 0);

        double seconds = MainViewModel.CalculateLeadingSilenceSeconds(
            timelineStart,
            timelineStart.AddMilliseconds(180));

        Assert.Equal(0.18, seconds, precision: 3);
    }

    [Fact]
    public void SilenceIsZeroBeforeAnyRecordingStarted()
    {
        Assert.Equal(
            0,
            MainViewModel.CalculateLeadingSilenceSeconds(DateTime.MinValue, DateTime.Now));
    }

    [Fact]
    public void NegativeOrHugeElapsedIsClamped()
    {
        var timelineStart = new DateTime(2026, 9, 16, 12, 0, 0);

        Assert.Equal(
            0,
            MainViewModel.CalculateLeadingSilenceSeconds(timelineStart, timelineStart.AddSeconds(-3)));
        Assert.Equal(
            MainViewModel.MaxLeadingSilenceSeconds,
            MainViewModel.CalculateLeadingSilenceSeconds(timelineStart, timelineStart.AddMinutes(30)));
        Assert.Equal(
            0,
            MainViewModel.CalculateLeadingSilenceSeconds(timelineStart, timelineStart.AddSeconds(1), maxSeconds: 0));
    }

    /// <summary>上限必须容得下 5 秒预录加启动耗时，不能像原来那样卡在 5 秒。</summary>
    [Fact]
    public void MaxSilenceAllowsFiveSecondPreRecord()
    {
        Assert.True(MainViewModel.MaxLeadingSilenceSeconds >= 5.5);
    }
}
