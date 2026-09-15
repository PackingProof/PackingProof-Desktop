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
    /// 采集门限与处理循环都要跟档位：以前空闲写死 15fps 采集 / 24fps 处理，
    /// 60fps 摄像头下预览被压在 15fps，怎么改预览间隔都还是"卡"。
    /// </summary>
    [Fact]
    public void TargetFpsFollowsTierAndKeepsRecordingFullSpeed()
    {
        // 满帧档位（前台/刚操作）
        Assert.Equal(60, PreviewFrameRatePolicy.ResolveTargetFps(60, interval: null, isRecording: false));
        // 12fps 档位
        Assert.Equal(12, PreviewFrameRatePolicy.ResolveTargetFps(60, PreviewFrameRatePolicy.ReducedInterval, isRecording: false));
        // 4fps 档位
        Assert.Equal(4, PreviewFrameRatePolicy.ResolveTargetFps(60, PreviewFrameRatePolicy.LowInterval, isRecording: false));
        // 录制时始终跟摄像头，录像不受预览档位影响
        Assert.Equal(60, PreviewFrameRatePolicy.ResolveTargetFps(60, PreviewFrameRatePolicy.LowInterval, isRecording: true));
        // 摄像头帧率还没测出来时用兜底值
        Assert.Equal(
            PreviewFrameRatePolicy.FallbackCameraFps,
            PreviewFrameRatePolicy.ResolveTargetFps(0, interval: null, isRecording: false));
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

    /// <summary>
    /// 降档必须提示一次：用户看到画面变卡只会以为软件出问题，
    /// 说清楚"长时间没人操作才降的"才不会被当成 bug；恢复满帧不打扰。
    /// </summary>
    [Fact]
    public void ReducedTierIsAnnouncedOnce()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Camera.cs"));

        int notifyIndex = source.IndexOf("private void NotifyPreviewRateTierIfChanged", StringComparison.Ordinal);
        Assert.True(notifyIndex >= 0, "未找到降档提示逻辑");

        string notify = source[notifyIndex..];
        int methodEnd = notify.IndexOf("\n        }", StringComparison.Ordinal);
        notify = methodEnd >= 0 ? notify[..methodEnd] : notify;

        Assert.Contains("ShowToast", notify, StringComparison.Ordinal);
        Assert.Contains("FullRateFpsMarker", notify, StringComparison.Ordinal);
        Assert.Contains("PreviewFrameRatePolicy.FullRateFpsMarker", source, StringComparison.Ordinal);
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
