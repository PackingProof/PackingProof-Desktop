using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 停止语音的时机守卫。收尾窗口内录制仍在继续，停止语音必须在触发瞬间入队，
/// 否则要等到收尾结束才响；手动停止、停止指令与扫码同码停录必须共用同一段播报逻辑。
/// </summary>
public sealed class RecordingStopAnnouncementTests
{
    [Fact]
    public void ManualStopAnnouncesBeforeWaitingForPostRoll()
    {
        string recording = ReadViewModelSource("MainViewModel.Recording.cs");
        string postRoll = ExtractMethod(
            recording,
            "private async Task<bool> WaitForManualPostRollAsync",
            "private async Task InternalStopRecordingAsync");

        int announce = postRoll.IndexOf("AnnounceStopTriggered();", StringComparison.Ordinal);
        int wait = postRoll.IndexOf("long started = Stopwatch.GetTimestamp();", StringComparison.Ordinal);

        Assert.True(announce >= 0, "手动收尾必须播报停止语音");
        Assert.True(wait > announce, "停止语音必须在收尾等待之前入队");
    }

    [Fact]
    public void ManualStopAndStopCommandUseTheSameStopTiming()
    {
        string recording = ReadViewModelSource("MainViewModel.Recording.cs");
        string stopTiming = ExtractMethod(
            recording,
            "private async Task StopWithManualAnnouncementAsync",
            "private async Task<bool> WaitForManualPostRollAsync");

        int waitPostRoll = stopTiming.IndexOf("await WaitForManualPostRollAsync();", StringComparison.Ordinal);
        int stop = stopTiming.IndexOf("await InternalStopRecordingAsync();", StringComparison.Ordinal);
        Assert.True(waitPostRoll >= 0 && stop > waitPostRoll, "共用停止时序必须先收尾再停录");

        Assert.Contains(
            "await StopWithManualAnnouncementAsync();",
            ReadViewModelSource("MainViewModel.Scanner.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "await StopWithManualAnnouncementAsync();",
            ReadViewModelSource("MainViewModel.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SameCodePostRollSharesTheStopAnnouncement()
    {
        Assert.Contains(
            "AnnounceStopTriggered();",
            ReadViewModelSource("MainViewModel.Scanner.cs"),
            StringComparison.Ordinal);
    }

    private static string ExtractMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到 {startMarker}");

        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"未找到 {endMarker}");

        return source[start..end];
    }

    private static string ReadViewModelSource(string fileName) =>
        File.ReadAllText(Path.Combine(FindViewModelDirectory(), fileName));

    private static string FindViewModelDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return Path.Combine(directory.FullName, "ExpressPackingMonitoring", "ViewModels");
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到 ViewModels 目录");
    }
}
