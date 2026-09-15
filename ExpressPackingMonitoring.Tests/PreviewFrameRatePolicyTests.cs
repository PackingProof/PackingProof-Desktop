using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 预览发布节奏：有人操作时按 12fps，长时间没人碰鼠标/键盘/扫码时降到 4fps。
/// 降帧只影响给人看的预览，录像管线仍按录制帧率走。
/// </summary>
public sealed class PreviewFrameRatePolicyTests
{
    [Fact]
    public void RecentActivityKeepsFullPreviewRate()
    {
        Assert.Equal(PreviewFrameRatePolicy.ActiveInterval, PreviewFrameRatePolicy.ResolveInterval(TimeSpan.Zero));
        Assert.Equal(
            PreviewFrameRatePolicy.ActiveInterval,
            PreviewFrameRatePolicy.ResolveInterval(PreviewFrameRatePolicy.IdleAfter - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void IdlePreviewDropsToQuarterRate()
    {
        Assert.Equal(
            PreviewFrameRatePolicy.IdleInterval,
            PreviewFrameRatePolicy.ResolveInterval(PreviewFrameRatePolicy.IdleAfter));
        Assert.Equal(
            PreviewFrameRatePolicy.IdleInterval,
            PreviewFrameRatePolicy.ResolveInterval(TimeSpan.FromMinutes(30)));
    }

    /// <summary>空闲阈值与两档间隔的关系不能颠倒，否则降帧要么不生效要么一直在降。</summary>
    [Fact]
    public void IdleRateIsSlowerThanActiveRate()
    {
        Assert.True(PreviewFrameRatePolicy.IdleInterval > PreviewFrameRatePolicy.ActiveInterval);
        Assert.True(PreviewFrameRatePolicy.IdleAfter >= TimeSpan.FromSeconds(10));
        Assert.True(PreviewFrameRatePolicy.ActiveInterval <= TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// 摄像头休眠后主界面收不到鼠标事件，只有小窗在收；小窗上的活动必须走同一条空闲时间，
    /// 所以预览降帧与休眠唤醒共用 ViewModel 的 _lastActivityTime。
    /// </summary>
    [Fact]
    public void PreviewRateUsesSharedUserActivityClock()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Camera.cs"));

        Assert.Contains("PreviewFrameRatePolicy.ResolveInterval(DateTime.Now - _lastActivityTime)", source, StringComparison.Ordinal);
        // 写死的 12fps 常量必须已经删掉，否则两道限流会互相打架。
        Assert.DoesNotContain("PreviewFrameInterval = TimeSpan", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }
}
