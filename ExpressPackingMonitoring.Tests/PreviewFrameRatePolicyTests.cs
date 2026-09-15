using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 预览发布节奏：默认按摄像头帧率满帧跑（用户会拿 OBS 对比，限流一眼就看出卡）；
/// 只有程序在后台且长时间没人操作时才逐级降帧。
/// </summary>
public sealed class PreviewFrameRatePolicyTests
{
    /// <summary>程序窗口在前台时一律满帧，哪怕很久没碰鼠标。</summary>
    [Fact]
    public void FocusedWindowKeepsFullRate()
    {
        Assert.Null(PreviewFrameRatePolicy.ResolveInterval(TimeSpan.Zero, appWindowFocused: true));
        Assert.Null(PreviewFrameRatePolicy.ResolveInterval(TimeSpan.FromHours(2), appWindowFocused: true));
    }

    /// <summary>后台但刚操作过也满帧：用户可能正盯着小窗看。</summary>
    [Fact]
    public void RecentActivityKeepsFullRate()
    {
        Assert.Null(PreviewFrameRatePolicy.ResolveInterval(TimeSpan.Zero, appWindowFocused: false));
        Assert.Null(PreviewFrameRatePolicy.ResolveInterval(
            PreviewFrameRatePolicy.ReducedAfter - TimeSpan.FromSeconds(1),
            appWindowFocused: false));
    }

    [Fact]
    public void IdleFirstStepDropsToTwelveFps()
    {
        Assert.Equal(
            PreviewFrameRatePolicy.ReducedInterval,
            PreviewFrameRatePolicy.ResolveInterval(PreviewFrameRatePolicy.ReducedAfter, appWindowFocused: false));
        Assert.Equal(
            PreviewFrameRatePolicy.ReducedInterval,
            PreviewFrameRatePolicy.ResolveInterval(
                PreviewFrameRatePolicy.LowAfter - TimeSpan.FromSeconds(1),
                appWindowFocused: false));
    }

    [Fact]
    public void LongerIdleDropsOneMoreStep()
    {
        Assert.Equal(
            PreviewFrameRatePolicy.LowInterval,
            PreviewFrameRatePolicy.ResolveInterval(PreviewFrameRatePolicy.LowAfter, appWindowFocused: false));
        Assert.Equal(
            PreviewFrameRatePolicy.LowInterval,
            PreviewFrameRatePolicy.ResolveInterval(TimeSpan.FromHours(3), appWindowFocused: false));
    }

    /// <summary>三级关系不能颠倒：满帧 > 12fps > 4fps，阈值递增。</summary>
    [Fact]
    public void StepsAreMonotonic()
    {
        Assert.True(PreviewFrameRatePolicy.ReducedAfter < PreviewFrameRatePolicy.LowAfter);
        Assert.True(PreviewFrameRatePolicy.LowInterval > PreviewFrameRatePolicy.ReducedInterval);
        Assert.True(PreviewFrameRatePolicy.ReducedInterval <= TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// 满帧意味着"不额外限流"，所以调用点必须同时接受 null；
    /// 焦点状态由 Application.Activated/Deactivated 维护，采集线程只读标记。
    /// </summary>
    [Fact]
    public void CameraPipelineHandlesFullRateAndFocusSignal()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Camera.cs"));

        Assert.Contains(
            "PreviewFrameRatePolicy.ResolveInterval(DateTime.Now - _lastActivityTime, _isAppWindowFocused)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("application.Activated +=", source, StringComparison.Ordinal);
        Assert.Contains("application.Deactivated +=", source, StringComparison.Ordinal);
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
