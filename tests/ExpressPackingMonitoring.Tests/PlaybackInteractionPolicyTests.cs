using ExpressPackingMonitoring.UI;
using System.Windows.Input;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class PlaybackInteractionPolicyTests
{
    [Theory]
    [InlineData(Key.Space, ModifierKeys.None, (int)PlaybackShortcutAction.TogglePlay)]
    [InlineData(Key.Left, ModifierKeys.None, (int)PlaybackShortcutAction.SeekBackward)]
    [InlineData(Key.Right, ModifierKeys.None, (int)PlaybackShortcutAction.SeekForward)]
    [InlineData(Key.S, ModifierKeys.Control, (int)PlaybackShortcutAction.CaptureScreenshot)]
    [InlineData(Key.S, ModifierKeys.None, (int)PlaybackShortcutAction.None)]
    [InlineData(Key.Left, ModifierKeys.Control, (int)PlaybackShortcutAction.None)]
    public void ResolveShortcut_UsesUnmodifiedPlaybackKeys(
        Key key,
        ModifierKeys modifiers,
        int expected)
    {
        Assert.Equal(
            (PlaybackShortcutAction)expected,
            PlaybackInteractionPolicy.ResolveShortcut(
                key,
                modifiers,
                preserveFocusedControlInput: false));
    }

    [Fact]
    public void ResolveShortcut_PreservesFocusedControlInput()
    {
        Assert.Equal(
            PlaybackShortcutAction.None,
            PlaybackInteractionPolicy.ResolveShortcut(
                Key.Space,
                ModifierKeys.None,
                preserveFocusedControlInput: true));
        Assert.Equal(
            PlaybackShortcutAction.CaptureScreenshot,
            PlaybackInteractionPolicy.ResolveShortcut(
                Key.S,
                ModifierKeys.Control,
                preserveFocusedControlInput: true));
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    public void ShouldResumeAfterScreenshot_OnlyRestoresAnActivePlayback(
        bool wasPlaying,
        bool isClosing,
        bool hasLoadedMedia,
        bool expected)
    {
        Assert.Equal(
            expected,
            PlaybackInteractionPolicy.ShouldResumeAfterScreenshot(
                wasPlaying,
                isClosing,
                hasLoadedMedia));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    public void ShouldUnpauseAfterFirstFrame_DefersWhileSecondaryActionRuns(
        bool pendingStartUnpause,
        bool secondaryActionPause,
        bool deferAutoPlayUntilExplicitPlay,
        bool expected)
    {
        Assert.Equal(
            expected,
            PlaybackInteractionPolicy.ShouldUnpauseAfterFirstFrame(
                pendingStartUnpause,
                secondaryActionPause,
                deferAutoPlayUntilExplicitPlay));
    }

    [Fact]
    public void SecondaryActionState_RemainsDeferredUntilExplicitPlayback()
    {
        var state = new PlaybackSecondaryActionState();

        state.Begin();
        Assert.True(state.IsActive);
        Assert.True(state.DeferAutoPlayUntilExplicitPlay);

        state.End();
        Assert.False(state.IsActive);
        Assert.True(state.DeferAutoPlayUntilExplicitPlay);

        state.AllowExplicitPlayback();
        Assert.False(state.DeferAutoPlayUntilExplicitPlay);
    }

    [Fact]
    public void QueuedFirstFrameCallback_DoesNotResumeAfterExportDialogCloses()
    {
        var state = new PlaybackSecondaryActionState();

        state.Begin();
        state.End();

        Assert.False(state.IsActive);
        Assert.True(state.DeferAutoPlayUntilExplicitPlay);
        Assert.False(PlaybackInteractionPolicy.ShouldUnpauseAfterFirstFrame(
            pendingStartUnpause: true,
            secondaryActionPause: state.IsActive,
            deferAutoPlayUntilExplicitPlay: state.DeferAutoPlayUntilExplicitPlay));
    }

    [Fact]
    public void SecondaryActionState_HandlesOverlappingActionsAndNewSelection()
    {
        var state = new PlaybackSecondaryActionState();

        state.Begin();
        state.Begin();
        state.BeginVideoSelection();
        Assert.True(state.IsActive);
        Assert.True(state.DeferAutoPlayUntilExplicitPlay);

        state.End();
        Assert.True(state.IsActive);
        state.End();
        Assert.False(state.IsActive);
        Assert.True(state.DeferAutoPlayUntilExplicitPlay);

        state.BeginVideoSelection();
        Assert.False(state.DeferAutoPlayUntilExplicitPlay);
    }

    [Theory]
    [InlineData(0, 5, 120, 5)]
    [InlineData(118, 5, 120, 120)]
    [InlineData(2, -5, 120, 0)]
    [InlineData(60, 5, 0, 0)]
    public void ClampSeekTarget_StaysInsideVideo(
        double current,
        double delta,
        double duration,
        double expected)
    {
        Assert.Equal(
            expected,
            PlaybackInteractionPolicy.ClampSeekTarget(current, delta, duration));
    }

    [Fact]
    public void ScreenshotPath_UsesSafeOrderNameAndTransientHandoff()
    {
        string fileName = PlaybackInteractionPolicy.BuildFileName(
            "AB:CD/EF",
            new DateTime(2026, 11, 17, 20, 31, 48, 123));

        Assert.Equal("AB_CD_EF_20261117_203148_123.png", fileName);
        Assert.Equal(
            Path.Combine(@"C:\Temp", "PackingProof Screenshot Preview"),
            PlaybackInteractionPolicy.ResolveTemporaryDirectory(@"C:\Temp"));
    }

    [Theory]
    [InlineData(640, 360, 479, 427, 240)]
    [InlineData(1920, 1080, 479, 427, 240)]
    [InlineData(1080, 1920, 380, 135, 240)]
    [InlineData(100, 100, 380, 100, 100)]
    public void ScreenshotDialogSize_FitsPreviewWithoutChangingButtons(
        int pixelWidth,
        int pixelHeight,
        double expectedWindowWidth,
        double expectedPreviewWidth,
        double expectedPreviewHeight)
    {
        PlaybackScreenshotDialogSize size =
            PlaybackInteractionPolicy.CalculateScreenshotDialogSize(
                pixelWidth,
                pixelHeight);

        Assert.Equal(expectedWindowWidth, size.WindowWidth);
        Assert.Equal(expectedPreviewWidth, size.PreviewWidth);
        Assert.Equal(expectedPreviewHeight, size.PreviewHeight);
    }

    [Fact]
    public void WebClipLaunchUrl_ContainsCurrentVideoAndPreservesAccessKey()
    {
        string url = WebClipLaunchUrlBuilder.Build(
            "http://127.0.0.1:5280/?key=abc",
            42,
            "AB CD&1",
            "录像 01.mp4",
            12.5,
            "h265");

        Assert.StartsWith(
            "http://127.0.0.1:5280/?key=abc&action=clip&videoId=42",
            url,
            StringComparison.Ordinal);
        Assert.Contains("orderId=AB%20CD%261", url, StringComparison.Ordinal);
        Assert.Contains(
            "fileName=%E5%BD%95%E5%83%8F%2001.mp4",
            url,
            StringComparison.Ordinal);
        Assert.Contains("durationSec=12.5", url, StringComparison.Ordinal);
        Assert.EndsWith("videoCodec=h265", url, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupHostLocalPlayback_WiresClipButtonToLoopbackWebPlayback()
    {
        string source = ReadProjectFile(
            Path.Combine("Workstations", "PrintWorkstationWindow.xaml.cs"));

        Assert.Contains(
            "webClipUrlProvider: BuildLocalWebClipUrlAsync",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "string accessUrl = _host.LocalPlaybackUrl;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "WebClipLaunchUrlBuilder.Build(",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackControlBar_ExposesCompactOriginalFrameScreenshot()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "PlaybackWindow.xaml"));
        string screenshotSource = ReadProjectFile(
            Path.Combine("UI", "PlaybackWindow.Screenshot.cs"));
        string controlsSource = ReadProjectFile(
            Path.Combine("UI", "PlaybackWindow.Controls.cs"));
        string dialogXaml = ReadProjectFile(
            Path.Combine("UI", "PlaybackScreenshotDialog.xaml"));
        string dialogSource = ReadProjectFile(
            Path.Combine("UI", "PlaybackScreenshotDialog.xaml.cs"));
        string mediaViewModel = ReadProjectFile(
            Path.Combine("ViewModels", "MainViewModel.Media.cs"));
        string webIndex = ReadProjectFile(Path.Combine("Web", "index.html"));

        Assert.Contains("x:Name=\"BtnScreenshot\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource PrimaryButtonStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ScreenshotText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BtnWebClip\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Data=\"{StaticResource FluentCutIcon}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Data=\"{StaticResource FluentPhotoCameraIcon}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ToolTip=\"{loc:Localized 截图（Ctrl+S）}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "TakeSnapshot(0, nativeHandoffPath, 0, 0)",
            screenshotSource,
            StringComparison.Ordinal);
        Assert.Contains("new PlaybackScreenshotDialog", screenshotSource, StringComparison.Ordinal);
        Assert.Contains("_mediaPlayer?.SetPause(true);", screenshotSource, StringComparison.Ordinal);
        Assert.Contains("RestorePlaybackAfterScreenshot(wasPlaying);", screenshotSource, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Copy(sourcePath", screenshotSource, StringComparison.Ordinal);
        Assert.Contains("File.Move(", screenshotSource, StringComparison.Ordinal);
        Assert.Contains("LoadScreenshotThumbnail(nativeHandoffPath)", screenshotSource, StringComparison.Ordinal);
        Assert.Contains("LoadScreenshot(nativeHandoffPath)", screenshotSource, StringComparison.Ordinal);
        Assert.Contains("DeleteTemporaryScreenshot(temporaryPath)", screenshotSource, StringComparison.Ordinal);
        Assert.Contains(
            "HandlePlaybackShortcut(e);",
            ReadProjectFile(Path.Combine("UI", "PlaybackWindow.xaml.cs")),
            StringComparison.Ordinal);
        Assert.Contains("ScreenshotText.Visibility = textVisibility;", controlsSource, StringComparison.Ordinal);
        Assert.Contains("BtnScreenshot.Style = buttonStyle;", controlsSource, StringComparison.Ordinal);
        Assert.Contains("WebClipText.Visibility = textVisibility;", controlsSource, StringComparison.Ordinal);
        Assert.Contains("BtnWebClip.Style = buttonStyle;", controlsSource, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PreviewImage\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PreviewFrame\"", dialogXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("请选择复制到剪贴板或另存为", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource PrimaryButtonStyle}\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource SecondaryButtonStyle}\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"{loc:Localized 复制到剪贴板}\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("Width = size.WindowWidth;", dialogSource, StringComparison.Ordinal);
        Assert.Contains("PreviewFrame.Width = size.PreviewWidth;", dialogSource, StringComparison.Ordinal);
        Assert.Contains(
            "$" + "\"127.0.0.1:{Config.WebServerPort}\"",
            mediaViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "webClipUrlProvider: WebServerModePolicy.ProvidesWebPlayback(webServerMode)",
            mediaViewModel,
            StringComparison.Ordinal);
        Assert.Contains("function initClipDeepLink()", webIndex, StringComparison.Ordinal);
        Assert.Contains("params.get('action')!=='clip'", webIndex, StringComparison.Ordinal);
        Assert.Contains("id=\"clipSuccessPanel\"", webIndex, StringComparison.Ordinal);
        Assert.Contains("id=\"clipResultDownloadBtn\"", webIndex, StringComparison.Ordinal);
        Assert.Contains("id=\"clipResultDismissBtn\"", webIndex, StringComparison.Ordinal);
        Assert.Contains("r.status===429", webIndex, StringComparison.Ordinal);
        Assert.Contains("closeClipDialog(true);if(taskId)fetch(", webIndex, StringComparison.Ordinal);
    }

    private static string ReadProjectFile(string relativePath)
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring",
            relativePath);
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "ExpressPackingMonitoring repository root was not found.");
    }
}
