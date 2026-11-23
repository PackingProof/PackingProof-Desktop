using System.IO;
using ExpressPackingMonitoring.UpdateCore;
using System.Windows.Input;

namespace ExpressPackingMonitoring.UI;

internal enum PlaybackShortcutAction
{
    None,
    TogglePlay,
    SeekBackward,
    SeekForward,
    CaptureScreenshot
}

internal readonly record struct PlaybackScreenshotDialogSize(
    double WindowWidth,
    double PreviewWidth,
    double PreviewHeight);

internal sealed class PlaybackSecondaryActionState
{
    private int _activeActionCount;

    internal bool IsActive => _activeActionCount > 0;

    internal bool DeferAutoPlayUntilExplicitPlay { get; private set; }

    internal void Begin()
    {
        _activeActionCount++;
        DeferAutoPlayUntilExplicitPlay = true;
    }

    internal void End()
    {
        if (_activeActionCount > 0)
            _activeActionCount--;
    }

    internal void BeginVideoSelection()
    {
        if (!IsActive)
            DeferAutoPlayUntilExplicitPlay = false;
    }

    internal void AllowExplicitPlayback()
    {
        DeferAutoPlayUntilExplicitPlay = false;
    }
}

internal static class PlaybackInteractionPolicy
{
    internal const double SeekStepSeconds = 5;
    private const double ScreenshotDialogMinimumWindowWidth = 380;
    private const double ScreenshotDialogHorizontalChrome = 52;
    private const double ScreenshotPreviewMaximumWidth = 440;
    private const double ScreenshotPreviewMaximumHeight = 240;

    internal static string ResolveTemporaryDirectory(string? temporaryDirectory)
    {
        string root = string.IsNullOrWhiteSpace(temporaryDirectory)
            ? Path.GetTempPath()
            : temporaryDirectory;
        return Path.Combine(root, BrandMetadata.ArtifactPrefix + " Screenshot Preview");
    }

    internal static string BuildFileName(string? orderId, DateTime capturedAt)
    {
        string label = (orderId ?? "").Trim();
        if (label.Length == 0)
            label = "screenshot";

        foreach (char invalid in Path.GetInvalidFileNameChars())
            label = label.Replace(invalid, '_');
        label = label.Trim('.', ' ');
        if (label.Length == 0)
            label = "screenshot";
        if (label.Length > 80)
            label = label[..80];

        return $"{label}_{capturedAt:yyyyMMdd_HHmmss_fff}.png";
    }

    internal static double ClampSeekTarget(double currentSeconds, double deltaSeconds, double durationSeconds)
    {
        double upper = Math.Max(0, durationSeconds);
        return Math.Clamp(currentSeconds + deltaSeconds, 0, upper);
    }

    internal static PlaybackShortcutAction ResolveShortcut(
        Key key,
        ModifierKeys modifiers,
        bool preserveFocusedControlInput)
    {
        if (key == Key.Space && modifiers == ModifierKeys.None)
            return preserveFocusedControlInput
                ? PlaybackShortcutAction.None
                : PlaybackShortcutAction.TogglePlay;
        if (key == Key.Left && modifiers == ModifierKeys.None)
            return preserveFocusedControlInput
                ? PlaybackShortcutAction.None
                : PlaybackShortcutAction.SeekBackward;
        if (key == Key.Right && modifiers == ModifierKeys.None)
            return preserveFocusedControlInput
                ? PlaybackShortcutAction.None
                : PlaybackShortcutAction.SeekForward;
        if (key == Key.S && modifiers == ModifierKeys.Control)
            return PlaybackShortcutAction.CaptureScreenshot;

        return PlaybackShortcutAction.None;
    }

    internal static bool ShouldResumeAfterScreenshot(
        bool wasPlaying,
        bool isClosing,
        bool hasLoadedMedia) =>
        wasPlaying && !isClosing && hasLoadedMedia;

    internal static bool ShouldUnpauseAfterFirstFrame(
        bool pendingStartUnpause,
        bool secondaryActionPause,
        bool deferAutoPlayUntilExplicitPlay) =>
        pendingStartUnpause
        && !secondaryActionPause
        && !deferAutoPlayUntilExplicitPlay;

    internal static PlaybackScreenshotDialogSize CalculateScreenshotDialogSize(
        int pixelWidth,
        int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
            return new PlaybackScreenshotDialogSize(
                ScreenshotDialogMinimumWindowWidth,
                ScreenshotPreviewMaximumWidth,
                ScreenshotPreviewMaximumHeight);

        double scale = Math.Min(
            1.0,
            Math.Min(
                ScreenshotPreviewMaximumWidth / pixelWidth,
                ScreenshotPreviewMaximumHeight / pixelHeight));
        double previewWidth = Math.Max(1, Math.Round(pixelWidth * scale));
        double previewHeight = Math.Max(1, Math.Round(pixelHeight * scale));
        return new PlaybackScreenshotDialogSize(
            Math.Max(
                ScreenshotDialogMinimumWindowWidth,
                previewWidth + ScreenshotDialogHorizontalChrome),
            previewWidth,
            previewHeight);
    }
}
