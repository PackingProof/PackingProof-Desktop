using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 回放窗口尺寸：视频区要贴住录像宽高比，否则 LibVLC 会在视频区里加黑边（现场反馈"上下有黑边"）。
/// </summary>
public sealed class PlaybackWindowFitPolicyTests
{
    private const double ChromeWidth = 350;   // 左侧列表 + 边距
    private const double ChromeHeight = 150;  // 标题 + 顶部栏 + 控制条
    private const double PreferredWidth = 1100;
    private const double PreferredHeight = 700;
    private const double WorkWidth = 1920;
    private const double WorkHeight = 1040;
    private const double MinWidth = 920;
    private const double MinHeight = 560;

    [Fact]
    public void Calculate_LandscapeVideo_MatchesAspectWithoutExceedingWorkArea()
    {
        PlaybackWindowSize? size = PlaybackWindowFitPolicy.Calculate(
            1920, 1080, ChromeWidth, ChromeHeight, PreferredWidth, PreferredHeight, WorkWidth, WorkHeight, MinWidth, MinHeight);

        Assert.NotNull(size);
        Assert.Equal(PreferredWidth, size!.Value.Width, 1);
        // 视频区宽 = 1100 - 350 = 750；16:9 对应高 421.9，加回固定占位
        Assert.Equal(ChromeHeight + 750 / (1920 / 1080.0), size.Value.Height, 1);
        Assert.True(size.Value.Height < 700, "按 16:9 算出来的窗口应该比带黑边的默认高度矮");
    }

    [Fact]
    public void Calculate_PortraitVideo_DoesNotGrowWindowHeight()
    {
        // 手机竖屏录像（9:16）：按比例要 1483 高，但窗口不许比现在更高 ——
        // 上一次的写法会顶到接近满屏高，窗口位置没跟着上移，下半截连进度条跑到屏幕外。
        PlaybackWindowSize? size = PlaybackWindowFitPolicy.Calculate(
            1080, 1920, ChromeWidth, ChromeHeight, PreferredWidth, PreferredHeight, WorkWidth, WorkHeight, MinWidth, MinHeight);

        Assert.NotNull(size);
        Assert.Equal(PreferredWidth, size!.Value.Width, 1);
        Assert.Equal(PreferredHeight, size.Value.Height, 1);
        Assert.True(size.Value.Height + 200 <= WorkHeight, "竖屏也不能把窗口顶到贴近屏幕高度");
    }

    [Fact]
    public void Calculate_UltraWideVideo_StaysAtMinimumHeightWithoutGrowingWidth()
    {
        PlaybackWindowSize? size = PlaybackWindowFitPolicy.Calculate(
            3440, 1440, ChromeWidth, ChromeHeight, PreferredWidth, PreferredHeight, WorkWidth, WorkHeight, MinWidth, MinHeight);

        Assert.NotNull(size);
        Assert.Equal(PreferredWidth, size!.Value.Width, 1);
        Assert.Equal(MinHeight, size.Value.Height, 1);
    }

    [Fact]
    public void Calculate_UnknownVideoSize_ReturnsNull()
    {
        Assert.Null(PlaybackWindowFitPolicy.Calculate(
            0, 0, ChromeWidth, ChromeHeight, PreferredWidth, PreferredHeight, WorkWidth, WorkHeight, MinWidth, MinHeight));
    }

    [Fact]
    public void Calculate_DefaultWindowWidth_IsKeptForLandscape()
    {
        PlaybackWindowSize? size = PlaybackWindowFitPolicy.Calculate(
            1280, 720, ChromeWidth, ChromeHeight, PreferredWidth, PreferredHeight, 1280, 720, MinWidth, MinHeight);

        Assert.NotNull(size);
        // 工作区比期望窗口还小：结果必须落在工作区内，且不能主动变高
        Assert.True(size!.Value.Width <= 1280);
        Assert.True(size.Value.Height <= 720);
    }
}
