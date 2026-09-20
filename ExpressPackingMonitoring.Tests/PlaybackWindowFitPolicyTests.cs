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
    private const double WorkWidth = 1920;
    private const double WorkHeight = 1040;
    private const double MinWidth = 920;
    private const double MinHeight = 560;

    [Fact]
    public void Calculate_LandscapeVideo_MatchesAspectWithoutExceedingWorkArea()
    {
        PlaybackWindowSize? size = PlaybackWindowFitPolicy.Calculate(
            1920, 1080, ChromeWidth, ChromeHeight, 1100, WorkWidth, WorkHeight, MinWidth, MinHeight);

        Assert.NotNull(size);
        Assert.Equal(1100, size!.Value.Width, 1);
        // 视频区宽 = 1100 - 350 = 750；16:9 对应高 421.9，加回固定占位
        Assert.Equal(ChromeHeight + 750 / (1920 / 1080.0), size.Value.Height, 1);
        Assert.True(size.Value.Height < 700, "按 16:9 算出来的窗口应该比带黑边的默认高度矮");
    }

    [Fact]
    public void Calculate_PortraitVideo_ShrinksWidthAndKeepsWindowInsideWorkArea()
    {
        // 手机竖屏录像：按期望宽度算高度会顶到工作区，这时按工作区高度回算宽度
        PlaybackWindowSize? size = PlaybackWindowFitPolicy.Calculate(
            1080, 1920, ChromeWidth, ChromeHeight, 1100, WorkWidth, WorkHeight, MinWidth, MinHeight);

        Assert.NotNull(size);
        Assert.Equal(WorkHeight, size!.Value.Height, 1);
        Assert.InRange(size.Value.Width, MinWidth, WorkWidth);
        // 宽度被窗口下限顶住时允许留一点黑边，但绝不能把窗口放到屏幕外
        Assert.True(size.Value.Width <= WorkWidth && size.Value.Height <= WorkHeight);
    }

    [Fact]
    public void Calculate_UltraWideVideo_RespectsMinimumHeight()
    {
        PlaybackWindowSize? size = PlaybackWindowFitPolicy.Calculate(
            3440, 1440, ChromeWidth, ChromeHeight, 1100, WorkWidth, WorkHeight, MinWidth, MinHeight);

        Assert.NotNull(size);
        Assert.Equal(MinHeight, size!.Value.Height, 1);
        Assert.True(size.Value.Width >= MinWidth);
        Assert.Equal(ChromeWidth + (MinHeight - ChromeHeight) * (3440 / 1440.0), size.Value.Width, 1);
    }

    [Fact]
    public void Calculate_UnknownVideoSize_ReturnsNull()
    {
        Assert.Null(PlaybackWindowFitPolicy.Calculate(
            0, 0, ChromeWidth, ChromeHeight, 1100, WorkWidth, WorkHeight, MinWidth, MinHeight));
    }

    [Fact]
    public void Calculate_DefaultWindowWidth_IsKeptForLandscape()
    {
        PlaybackWindowSize? size = PlaybackWindowFitPolicy.Calculate(
            1280, 720, ChromeWidth, ChromeHeight, 1100, 1280, 720, 920, 560);

        Assert.NotNull(size);
        // 工作区比期望窗口还小：结果必须落在工作区内
        Assert.True(size!.Value.Width <= 1280);
        Assert.True(size.Value.Height <= 720);
    }
}
