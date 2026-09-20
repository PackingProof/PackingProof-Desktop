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
    private const double PreferredHeight = 700;
    private const double WorkWidth = 1920;
    private const double WorkHeight = 1040;
    private const double MinWidth = 920;
    private const double MinHeight = 560;

    [Fact]
    public void Calculate_LandscapeVideo_KeepsWindowHeightAndWidensToMatchAspect()
    {
        // 16:9 保持窗口高度（不再被压成又宽又扁的一条），宽度按比例推出来
        PlaybackWindowSize? size = PlaybackWindowFitPolicy.Calculate(
            1920, 1080, ChromeWidth, ChromeHeight, PreferredHeight, WorkWidth, WorkHeight, MinWidth, MinHeight);

        Assert.NotNull(size);
        Assert.Equal(PreferredHeight, size!.Value.Height, 1);
        Assert.Equal(ChromeWidth + (PreferredHeight - ChromeHeight) * (1920 / 1080.0), size.Value.Width, 1);
    }

    [Fact]
    public void Calculate_PortraitVideo_KeepsHeightAndRespectsMinimumWidth()
    {
        // 手机竖屏录像（9:16）：按比例只需要 659 宽，低于窗口下限 —— 宽度顶下限，
        // 高度保持不动（上一次的写法会把窗口拉到接近满屏高，进度条都跑到屏幕外）
        PlaybackWindowSize? size = PlaybackWindowFitPolicy.Calculate(
            1080, 1920, ChromeWidth, ChromeHeight, PreferredHeight, WorkWidth, WorkHeight, MinWidth, MinHeight);

        Assert.NotNull(size);
        Assert.Equal(MinWidth, size!.Value.Width, 1);
        Assert.Equal(PreferredHeight, size.Value.Height, 1);
        Assert.True(size.Value.Height + 200 <= WorkHeight, "竖屏也不能把窗口顶到贴近屏幕高度");
    }

    [Fact]
    public void Calculate_UltraWideVideo_ClampsWidthAndRecomputesHeight()
    {
        // 32:9：按高度推出来的宽度超出工作区，先把宽度夹住，再按比例回算高度
        PlaybackWindowSize? size = PlaybackWindowFitPolicy.Calculate(
            5120, 1440, ChromeWidth, ChromeHeight, PreferredHeight, WorkWidth, WorkHeight, MinWidth, MinHeight);

        Assert.NotNull(size);
        Assert.Equal(WorkWidth, size!.Value.Width, 1);
        Assert.Equal(ChromeHeight + (WorkWidth - ChromeWidth) / (5120 / 1440.0), size.Value.Height, 1);
        Assert.InRange(size.Value.Height, MinHeight, PreferredHeight);
    }

    [Fact]
    public void Calculate_UnknownVideoSize_ReturnsNull()
    {
        Assert.Null(PlaybackWindowFitPolicy.Calculate(
            0, 0, ChromeWidth, ChromeHeight, PreferredHeight, WorkWidth, WorkHeight, MinWidth, MinHeight));
    }

    [Fact]
    public void Calculate_DefaultWindowWidth_IsKeptForLandscape()
    {
        PlaybackWindowSize? size = PlaybackWindowFitPolicy.Calculate(
            1280, 720, ChromeWidth, ChromeHeight, PreferredHeight, 1280, 720, MinWidth, MinHeight);

        Assert.NotNull(size);
        // 工作区比按高度推出来的宽度还窄：结果必须落在工作区内，高度按比例回算且不超过期望高度
        Assert.True(size!.Value.Width <= 1280);
        Assert.True(size.Value.Height <= 720);
    }
}
