using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 录制开始/停止提示音的时机守卫。停止收尾窗口内录制仍在继续，提示必须在触发瞬间入队；
/// 开始提示也不能等到音频初始化、预录回灌和首帧确认之后才响。
/// </summary>
public sealed class RecordingStopAnnouncementTests
{
    [Fact]
    public void StartAnnouncementBeepsBeforeSpeech()
    {
        string recording = ReadViewModelSource("MainViewModel.Recording.cs");
        string announcement = ExtractMethod(
            recording,
            "private void AnnounceStartTriggered",
            "private async Task StopWithManualAnnouncementAsync");

        int beep = announcement.IndexOf("PlayShortBeep();", StringComparison.Ordinal);
        int speech = announcement.IndexOf("Speak(DefaultSpeechCatalog.StartRecording", StringComparison.Ordinal);

        Assert.True(beep >= 0, "开始触发必须先播放短提示音");
        Assert.True(speech > beep, "短提示音必须在开始语音之前播放");
    }

    [Fact]
    public void StartAnnouncementRunsBeforeExpensiveRecordingStartup()
    {
        string recording = ReadViewModelSource("MainViewModel.Recording.cs");
        string start = ExtractMethod(
            recording,
            "private async Task InternalStartRecordingAsync",
            "private void BackgroundFFmpegRecordingLoop");

        int ffmpegGuard = start.IndexOf("if (string.IsNullOrEmpty(ffmpegPath))", StringComparison.Ordinal);
        int announce = start.IndexOf("AnnounceStartTriggered();", StringComparison.Ordinal);
        int audioPreparation = start.IndexOf("PrepareDirectAudioPipe", StringComparison.Ordinal);
        int preRecordFlush = start.IndexOf("FlushPreRecordFrames(", StringComparison.Ordinal);
        int audioInitialization = start.IndexOf("StartAudioRecording(", StringComparison.Ordinal);

        Assert.True(ffmpegGuard >= 0, "启动前必须保留 FFmpeg 可用性检查");
        Assert.True(announce > ffmpegGuard, "必要预检失败时不能误报开始录制");
        Assert.True(audioPreparation > announce, "开始提示必须早于实时音频管道准备");
        Assert.True(preRecordFlush > announce, "开始提示必须早于预录帧回灌");
        Assert.True(audioInitialization > announce, "开始提示必须早于麦克风初始化");
        Assert.DoesNotContain("_speechService?.PlayShortBeep();", start, StringComparison.Ordinal);
        Assert.DoesNotContain("Speak(DefaultSpeechCatalog.StartRecording", start, StringComparison.Ordinal);
    }

    [Fact]
    public void StopAnnouncementBeepsBeforeSpeech()
    {
        string recording = ReadViewModelSource("MainViewModel.Recording.cs");
        string announcement = ExtractMethod(
            recording,
            "private void AnnounceStopTriggered",
            "private async Task StopWithManualAnnouncementAsync");

        int beep = announcement.IndexOf("PlayShortBeep();", StringComparison.Ordinal);
        int speech = announcement.IndexOf("Speak(DefaultSpeechCatalog.StopRecording", StringComparison.Ordinal);

        Assert.True(beep >= 0, "停止触发必须先播放短提示音");
        Assert.True(speech > beep, "短提示音必须在停止语音之前播放");
    }

    [Fact]
    public void ManualStopAnnouncesAtTriggerBeforeWaitingForPostRoll()
    {
        string recording = ReadViewModelSource("MainViewModel.Recording.cs");
        string stop = ExtractMethod(
            recording,
            "private async Task StopWithManualAnnouncementAsync",
            "private async Task WaitForManualPostRollAsync");
        string postRoll = ExtractMethod(
            recording,
            "private async Task WaitForManualPostRollAsync",
            "private async Task InternalStopRecordingAsync");

        int announce = stop.IndexOf("AnnounceStopTriggered();", StringComparison.Ordinal);
        int waitCall = stop.IndexOf("await WaitForManualPostRollAsync();", StringComparison.Ordinal);
        int beepWindow = postRoll.IndexOf("long started = Stopwatch.GetTimestamp();", StringComparison.Ordinal);

        Assert.True(announce >= 0, "手动收尾必须播报停止语音");
        Assert.True(waitCall > announce, "停止提示必须在收尾等待之前触发");
        Assert.True(beepWindow >= 0, "收尾等待仍必须保留实际计时起点");
    }

    [Fact]
    public void ManualStopAndStopCommandUseTheSameStopTiming()
    {
        string recording = ReadViewModelSource("MainViewModel.Recording.cs");
        string stopTiming = ExtractMethod(
            recording,
            "private async Task StopWithManualAnnouncementAsync",
            "private async Task WaitForManualPostRollAsync");

        int announce = stopTiming.IndexOf("AnnounceStopTriggered();", StringComparison.Ordinal);
        int waitPostRoll = stopTiming.IndexOf("await WaitForManualPostRollAsync();", StringComparison.Ordinal);
        int stop = stopTiming.IndexOf("await InternalStopRecordingAsync(playCompletionBeep: false);", StringComparison.Ordinal);
        Assert.True(announce >= 0 && waitPostRoll > announce && stop > waitPostRoll, "共用停止时序必须先提示、再收尾、最后停录");

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

    [Fact]
    public void DeferredStopPathsDoNotPlayTheCompletionBeepTwice()
    {
        string recording = ReadViewModelSource("MainViewModel.Recording.cs");
        string scanner = ReadViewModelSource("MainViewModel.Scanner.cs");
        string manualStop = ExtractMethod(
            recording,
            "private async Task StopWithManualAnnouncementAsync",
            "private async Task WaitForManualPostRollAsync");
        string sameCodePostRoll = ExtractMethod(
            scanner,
            "private async Task CompleteSameCodePostRollAsync",
            "private void LogBarcodeRecordingComparison");

        Assert.Contains(
            "InternalStopRecordingAsync(playCompletionBeep: false);",
            manualStop,
            StringComparison.Ordinal);
        Assert.Contains(
            "InternalStopRecordingAsync(playCompletionBeep: false);",
            sameCodePostRoll,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SameCodePostRollResumesSpeechAfterDeferredStop()
    {
        string postRoll = ExtractMethod(
            ReadViewModelSource("MainViewModel.Scanner.cs"),
            "private async Task CompleteSameCodePostRollAsync",
            "private void LogBarcodeRecordingComparison");

        int stop = postRoll.IndexOf(
            "await InternalStopRecordingAsync(playCompletionBeep: false);",
            StringComparison.Ordinal);
        int resume = postRoll.IndexOf("ResumeSpeechWhenCameraIdle();", StringComparison.Ordinal);

        Assert.True(stop >= 0, "收尾结束后必须真正停录");
        Assert.True(resume > stop, "收尾停录后必须把语音生成从暂停状态恢复");
    }

    [Fact]
    public void BeepIsNotTriggeredByBarcodeRecognitionAlone()
    {
        string scanner = ReadViewModelSource("MainViewModel.Scanner.cs");
        string recording = ReadViewModelSource("MainViewModel.Recording.cs");
        string start = ExtractMethod(
            recording,
            "private async Task InternalStartRecordingAsync",
            "private void BackgroundFFmpegRecordingLoop");
        string stop = ExtractMethod(
            recording,
            "private async Task InternalStopRecordingAsync",
            "private string ResolveBestStoragePath");

        Assert.DoesNotContain(
            "BarcodeRecognized += _ => _speechService?.PlayShortBeep()",
            scanner,
            StringComparison.Ordinal);
        Assert.Contains("AnnounceStartTriggered();", start, StringComparison.Ordinal);
        Assert.Contains("_speechService?.PlayShortBeep();", stop, StringComparison.Ordinal);
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
                return Path.Combine(directory.FullName, "src", "ExpressPackingMonitoring", "ViewModels");
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到 ViewModels 目录");
    }
}
