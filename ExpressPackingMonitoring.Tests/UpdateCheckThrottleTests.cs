using ExpressPackingMonitoring.UpdateCore;
using System;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 后台自动检查更新的节流：现场反馈"打开了预览版更新，12 小时内都收不到预览版"，
/// 根因是缓存只看版本号与时间、不看渠道。渠道必须参与判定。
/// </summary>
public sealed class UpdateCheckThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 15, 0, 0, TimeSpan.Zero);

    private static bool ShouldSkip(
        string stateVersion = "0.0.76",
        string currentVersion = "0.0.76",
        bool stateAllowPrerelease = false,
        bool allowPrerelease = false,
        double checkedHoursAgo = 1)
    {
        return UpdateCheckThrottle.ShouldSkip(
            stateVersion,
            Now.AddHours(-checkedHoursAgo).ToString("O"),
            currentVersion,
            stateAllowPrerelease,
            allowPrerelease,
            Now,
            TimeSpan.FromHours(12));
    }

    [Fact]
    public void SameVersionSameChannelWithinWindow_Skips()
    {
        Assert.True(ShouldSkip());
    }

    /// <summary>用户刚打开预览版开关：上一次正式渠道的"已是最新"不能挡住这次检查。</summary>
    [Fact]
    public void ChannelTurnedOn_DoesNotSkip()
    {
        Assert.False(ShouldSkip(stateAllowPrerelease: false, allowPrerelease: true));
    }

    /// <summary>关掉预览版也一样：渠道变了就重新查。</summary>
    [Fact]
    public void ChannelTurnedOff_DoesNotSkip()
    {
        Assert.False(ShouldSkip(stateAllowPrerelease: true, allowPrerelease: false));
    }

    [Fact]
    public void VersionChanged_DoesNotSkip()
    {
        Assert.False(ShouldSkip(stateVersion: "0.0.75"));
    }

    [Fact]
    public void WindowElapsed_DoesNotSkip()
    {
        Assert.False(ShouldSkip(checkedHoursAgo: 13));
    }

    [Fact]
    public void MissingOrBrokenState_DoesNotSkip()
    {
        Assert.False(UpdateCheckThrottle.ShouldSkip(
            null,
            null,
            "0.0.76",
            stateAllowPrerelease: false,
            allowPrerelease: false,
            Now,
            TimeSpan.FromHours(12)));
        Assert.False(UpdateCheckThrottle.ShouldSkip(
            "0.0.76",
            "not-a-date",
            "0.0.76",
            stateAllowPrerelease: false,
            allowPrerelease: false,
            Now,
            TimeSpan.FromHours(12)));
    }

    /// <summary>时钟被改到未来：宁可多查一次，也不要因为"未来时间"永远跳过。</summary>
    [Fact]
    public void FutureTimestamp_DoesNotSkip()
    {
        Assert.False(UpdateCheckThrottle.ShouldSkip(
            "0.0.76",
            Now.AddHours(3).ToString("O"),
            "0.0.76",
            stateAllowPrerelease: false,
            allowPrerelease: false,
            Now,
            TimeSpan.FromHours(12)));
    }

    /// <summary>带 v 前缀与构建后缀的版本号要按同一版本处理。</summary>
    [Fact]
    public void VersionSuffixAndPrefix_AreIgnored()
    {
        Assert.True(UpdateCheckThrottle.ShouldSkip(
            "v0.0.76",
            Now.AddHours(-1).ToString("O"),
            "0.0.76-3-gabc1234",
            stateAllowPrerelease: false,
            allowPrerelease: false,
            Now,
            TimeSpan.FromHours(12)));
    }
}
