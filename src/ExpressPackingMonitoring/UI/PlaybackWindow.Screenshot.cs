using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using Microsoft.Win32;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace ExpressPackingMonitoring.UI;

public partial class PlaybackWindow
{
    private static readonly TimeSpan ScreenshotCompletionTimeout = TimeSpan.FromSeconds(5);
    private const int ScreenshotPreviewPixelWidth = 440;
    private bool _screenshotSourceReady;
    private bool _isSavingScreenshot;
    private bool _closeAfterScreenshot;
    private string _pendingScreenshotPath = "";
    private TaskCompletionSource<bool>? _pendingScreenshotCompletion;

    private void BtnScreenshot_Click(object sender, RoutedEventArgs e) =>
        _ = CaptureScreenshotAsync();

    private void HandlePlaybackShortcut(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        PlaybackShortcutAction action = PlaybackInteractionPolicy.ResolveShortcut(
            key,
            Keyboard.Modifiers,
            ShouldPreserveFocusedControlInput(e.OriginalSource as DependencyObject));

        switch (action)
        {
            case PlaybackShortcutAction.TogglePlay:
                if (!BtnTogglePlay.IsEnabled)
                    return;
                BtnTogglePlay_Click(BtnTogglePlay, new RoutedEventArgs());
                break;
            case PlaybackShortcutAction.SeekBackward:
            case PlaybackShortcutAction.SeekForward:
                if (!TimelineSlider.IsEnabled || _mediaPlayer?.Media == null || _currentMediaLengthMs <= 0)
                    return;
                double delta = action == PlaybackShortcutAction.SeekBackward
                    ? -PlaybackInteractionPolicy.SeekStepSeconds
                    : PlaybackInteractionPolicy.SeekStepSeconds;
                double target = PlaybackInteractionPolicy.ClampSeekTarget(
                    TimelineSlider.Value,
                    delta,
                    _currentMediaLengthMs / 1000.0);
                SetTimelineValue(target);
                SeekTo(target);
                break;
            case PlaybackShortcutAction.CaptureScreenshot:
                if (!BtnScreenshot.IsEnabled)
                    return;
                _ = CaptureScreenshotAsync();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// 输入框、下拉框和进度条自己需要空格/方向键。按钮获得焦点时也保留系统默认的
    /// 空格激活行为，避免同一个按键既触发按钮又触发播放器。
    /// </summary>
    private bool ShouldPreserveFocusedControlInput(DependencyObject? source)
    {
        if (FilterPopup.IsOpen)
            return true;

        DependencyObject? current = Keyboard.FocusedElement as DependencyObject ?? source;
        while (current != null)
        {
            if (current is TextBoxBase
                or ComboBox
                or DatePicker
                or Slider
                or ButtonBase)
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private async Task CaptureScreenshotAsync()
    {
        if (_isSavingScreenshot || !_screenshotSourceReady)
            return;

        bool wasPlaying = _isPlaying;
        _isSavingScreenshot = true;
        UpdateScreenshotButtonState();
        if (wasPlaying)
        {
            _mediaPlayer?.SetPause(true);
            _timer.Stop();
            UpdatePlayState(false);
        }

        string nativeHandoffPath = "";
        string orderId = (VideoList.SelectedItem as VideoItem)?.OrderId ?? "";
        DateTime capturedAt = DateTime.Now;
        try
        {
            if (!await _playerSemaphore.WaitAsync(TimeSpan.FromSeconds(2)))
            {
                ShowPlaybackToast(AppLanguage.Get("播放器正在切换视频，请稍后再试"));
                return;
            }

            try
            {
                VlcMediaPlayer? player = _mediaPlayer;
                if (_isClosing || !_screenshotSourceReady || player?.Media == null)
                    return;

                nativeHandoffPath = await Task.Run(() =>
                {
                    string directory = PlaybackInteractionPolicy.ResolveTemporaryDirectory(
                        Path.GetTempPath());
                    Directory.CreateDirectory(directory);
                    return Path.Combine(
                        directory,
                        PlaybackInteractionPolicy.BuildFileName(orderId, capturedAt));
                });

                var completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingScreenshotPath = Path.GetFullPath(nativeHandoffPath);
                _pendingScreenshotCompletion = completion;

                bool requested = await Task.Run(
                    () => player.TakeSnapshot(0, nativeHandoffPath, 0, 0));
                if (!requested)
                    throw new InvalidOperationException(AppLanguage.Get("未能保存当前画面"));

                try
                {
                    await completion.Task.WaitAsync(ScreenshotCompletionTimeout);
                }
                catch (TimeoutException)
                {
                    bool fileCreated = await Task.Run(() => File.Exists(nativeHandoffPath));
                    if (!fileCreated)
                        throw;
                }
            }
            finally
            {
                _playerSemaphore.Release();
            }

            if (_isClosing)
                return;

            BitmapSource preview = await Task.Run(
                () => LoadScreenshotThumbnail(nativeHandoffPath));
            var dialog = new PlaybackScreenshotDialog(preview)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            _ = dialog.ShowDialog();

            switch (dialog.Destination)
            {
                case PlaybackScreenshotDestination.CopyToClipboard:
                    BitmapSource screenshot = await Task.Run(
                        () => LoadScreenshot(nativeHandoffPath));
                    await ClipboardHelper.CopyImageAsync(screenshot);
                    ShowPlaybackToast(AppLanguage.Get("截图已复制到剪贴板"));
                    break;
                case PlaybackScreenshotDestination.SaveAs:
                    if (await SaveScreenshotAsync(
                            nativeHandoffPath,
                            orderId,
                            capturedAt))
                    {
                        nativeHandoffPath = "";
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("Playback", "截图流程失败", ex);
            ShowPlaybackToast(AppLanguage.Format("截图失败：{0}", ex.Message));
        }
        finally
        {
            _pendingScreenshotCompletion = null;
            _pendingScreenshotPath = "";
            _isSavingScreenshot = false;
            UpdateScreenshotButtonState();
            RestorePlaybackAfterScreenshot(wasPlaying);
            if (!string.IsNullOrWhiteSpace(nativeHandoffPath))
            {
                string temporaryPath = nativeHandoffPath;
                _ = Task.Run(() => DeleteTemporaryScreenshot(temporaryPath));
            }
            if (_closeAfterScreenshot)
            {
                _closeAfterScreenshot = false;
                _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
            }
        }
    }

    private async Task<bool> SaveScreenshotAsync(
        string sourcePath,
        string orderId,
        DateTime capturedAt)
    {
        string picturesDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.MyPictures);
        var saveDialog = new SaveFileDialog
        {
            Title = AppLanguage.Get("保存截图"),
            Filter = AppLanguage.Get("PNG 图片 (*.png)|*.png"),
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = string.IsNullOrWhiteSpace(picturesDirectory)
                ? ""
                : picturesDirectory,
            FileName = PlaybackInteractionPolicy.BuildFileName(orderId, capturedAt)
        };
        if (saveDialog.ShowDialog(this) != true)
            return false;

        await Task.Run(() => File.Move(
            sourcePath,
            saveDialog.FileName,
            overwrite: true));
        ShowPlaybackToast(
            AppLanguage.Format("截图已保存：{0}", Path.GetFileName(saveDialog.FileName)));
        return true;
    }

    private void RestorePlaybackAfterScreenshot(bool wasPlaying)
    {
        bool canResume = _mediaPlayer?.Media != null
            && _mediaPlayer.State is not VLCState.Ended
                and not VLCState.Stopped
                and not VLCState.Error;
        if (!PlaybackInteractionPolicy.ShouldResumeAfterScreenshot(
                wasPlaying,
                _isClosing,
                canResume))
        {
            return;
        }

        _mediaPlayer!.SetPause(false);
        _timer.Start();
        UpdatePlayState(true);
    }

    private static BitmapSource LoadScreenshot(string path)
    {
        using FileStream stream = File.OpenRead(path);
        BitmapFrame frame = BitmapFrame.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (!frame.CanFreeze)
            throw new InvalidOperationException("截图预览无法跨线程加载");
        frame.Freeze();
        return frame;
    }

    private static BitmapSource LoadScreenshotThumbnail(string path)
    {
        using FileStream stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = ScreenshotPreviewPixelWidth;
        image.StreamSource = stream;
        image.EndInit();
        if (!image.CanFreeze)
            throw new InvalidOperationException("截图缩略图无法跨线程加载");
        image.Freeze();
        return image;
    }

    private static void DeleteTemporaryScreenshot(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Playback", $"清理截图临时文件失败：{ex.Message}");
        }
    }

    private void MediaPlayer_SnapshotTaken(
        object? sender,
        MediaPlayerSnapshotTakenEventArgs e)
    {
        TaskCompletionSource<bool>? completion = _pendingScreenshotCompletion;
        if (completion == null || string.IsNullOrWhiteSpace(e.Filename))
            return;

        string completedPath;
        try
        {
            completedPath = Path.GetFullPath(e.Filename);
        }
        catch
        {
            return;
        }

        if (string.Equals(
                completedPath,
                _pendingScreenshotPath,
                StringComparison.OrdinalIgnoreCase))
        {
            completion.TrySetResult(true);
        }
    }

    private void UpdateScreenshotButtonState()
    {
        BtnScreenshot.IsEnabled = _screenshotSourceReady
            && !_isSavingScreenshot
            && !_isClosing
            && _mediaPlayer?.Media != null;
    }
}
