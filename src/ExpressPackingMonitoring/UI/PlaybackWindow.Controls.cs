using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LibVLCSharp.Shared;

namespace ExpressPackingMonitoring.UI;

/// <summary>
/// 回放控制条与播放状态：播放切换、定位文件、进度拖动、时间标签和窄窗口收缩。
/// 从主窗口代码分出来，避免继续增大已经超过规模预算的回放窗口文件。
/// </summary>
public partial class PlaybackWindow
{
    private void BtnTogglePlay_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null)
            return;

        if (_isPlaying)
        {
            _mediaPlayer.Pause();
            _timer.Stop();
            UpdatePlayState(false);
            return;
        }

        // 播完（Ended）/ 停止（Stopped）/ 出错（Error）之后媒体已经不在可播放状态，
        // SetPause(false) 不会让它重新开始 —— 现场表现就是"第一次播完再点播放没反应"。
        // 这几种情况要从头重播这一段。
        if (_mediaPlayer.Media == null
            || _mediaPlayer.State is VLCState.Ended or VLCState.Stopped or VLCState.Error)
        {
            if (VideoList.SelectedItem is VideoItem video && !video.IsUnavailable)
                PlaySelectedVideo(video);
            return;
        }

        _mediaPlayer.SetPause(false);
        _timer.Start();
        UpdatePlayState(true);
    }

    private void BtnLocateFile_Click(object sender, RoutedEventArgs e)
    {
        if (VideoList.SelectedItem is not VideoItem video
            || video.IsUnavailable
            || string.IsNullOrWhiteSpace(video.FullPath))
        {
            AppDialog.Warning(this, "请先选择一个可用视频", "定位文件");
            return;
        }

        try
        {
            FileLocationResult result = WindowsShellFileLocator.Locate(video.FullPath);
            if (result == FileLocationResult.OpenedFolder)
            {
                AppDialog.Information(
                    this,
                    "已打开文件所在文件夹，但系统未能自动选中录像文件",
                    "定位文件");
            }
            else if (result != FileLocationResult.Selected)
            {
                AppDialog.Error(this, "录像文件不存在或路径无效", "定位失败");
            }
        }
        catch (Exception ex)
        {
            AppDialog.Error(this, $"无法打开文件管理器：{ex.Message}", "定位失败");
        }
    }

    private void UpdatePlayState(bool isPlaying)
    {
        _isPlaying = isPlaying;
        PlayStateIcon.Data = (Geometry)FindResource(
            isPlaying ? "FluentPauseIcon" : "FluentPlayIcon");
        PlayStateText.Text = isPlaying ? "暂停" : "播放";
        BtnTogglePlay.ToolTip = AppLanguage.Get(
            isPlaying ? "暂停（空格）" : "播放（空格）");
    }

    private void MediaPlayer_LengthChanged(
        object? sender,
        MediaPlayerLengthChangedEventArgs e)
    {
        _currentMediaLengthMs = e.Length;
        _ = Dispatcher.BeginInvoke(() => SetTimelineMaximum(e.Length / 1000.0));
    }

    private void MediaPlayer_TimeChanged(
        object? sender,
        MediaPlayerTimeChangedEventArgs e)
    {
        if (_isDragging || _mediaPlayer == null)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!this.IsLoaded) return;
            // 等首帧期间 libvlc 也会报时间变化，这时不能揭掉封面，否则会先看到一块黑底。
            if (!_pendingStartUnpause)
                RevealPlaybackSurfaceAfterFirstFrame();
            SetTimelineValue(e.Time / 1000.0);
            UpdateTimeLabel(e.Time, _currentMediaLengthMs);
        });
    }

    private void MediaPlayer_EndReached(object? sender, EventArgs e)
    {
        CancelAwaitingFirstFrame();
        _ = Dispatcher.BeginInvoke(() =>
        {
            _timer.Stop();
            UpdatePlayState(false);
            SetTimelineValue(0);
        });
    }

    private void MediaPlayer_EncounteredError(object? sender, EventArgs e)
    {
        CancelAwaitingFirstFrame();
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isPreparingPlayback)
                return;

            ShowPlaybackCover(GetPlaybackFailureMessage());
            _timer.Stop();
            UpdatePlayState(false);
            RuntimeLog.Warn(
                "Playback",
                "Media player reported a decoding error");
        });
    }

    private void ShowPlaybackCover(string message)
    {
        _awaitingFirstFrame = false;
        PlaybackCoverText.Text = message;
        PlaybackCoverIcon.Visibility = Visibility.Visible;
        PlaybackPreparationProgress.Visibility = Visibility.Collapsed;
        PlayerView.Visibility = Visibility.Hidden;
        PlaybackCover.Visibility = Visibility.Visible;
    }

    private void RevealPlaybackSurfaceAfterFirstFrame()
    {
        if (!_awaitingFirstFrame)
            return;

        _awaitingFirstFrame = false;
        PlayerView.Visibility = Visibility.Visible;
        PlaybackCover.Visibility = Visibility.Collapsed;
        _screenshotSourceReady = true;
        UpdateScreenshotButtonState();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_isDragging || _mediaPlayer?.Media == null)
            return;

        UpdateTimeLabel(_mediaPlayer.Time, _currentMediaLengthMs);
    }

    private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isDragging = true;
        // 记下拖动前的状态：暂停时拖动，拖完必须还是暂停（现场反馈：拖完自动变播放了）
        _wasPlayingBeforeScrub = _isPlaying && _mediaPlayer?.IsPlaying == true;
        if (_wasPlayingBeforeScrub)
            _mediaPlayer?.Pause();
    }

    private void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_mediaPlayer == null)
            return;

        _isDragging = false;
        SeekTo(TimelineSlider.Value);
        if (_wasPlayingBeforeScrub)
        {
            _mediaPlayer.SetPause(false);
            _timer.Start();
            UpdatePlayState(true);
        }
        else
        {
            // 暂停状态拖动：保持暂停，只把时间标签停在拖动后的位置
            _timer.Stop();
            UpdatePlayState(false);
        }

        _wasPlayingBeforeScrub = false;
    }

    private void TimelineSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressTimelineValueChanged)
            return;

        if (_isDragging)
        {
            UpdateTimeLabel((long)(e.NewValue * 1000), _currentMediaLengthMs);
            return;
        }

        SeekTo(e.NewValue);
    }

    private void SetTimelineMaximum(double maximum)
    {
        _suppressTimelineValueChanged = true;
        try
        {
            TimelineSlider.Maximum = Math.Max(0, maximum);
            if (TimelineSlider.Value > TimelineSlider.Maximum)
                TimelineSlider.Value = TimelineSlider.Maximum;
        }
        finally
        {
            _suppressTimelineValueChanged = false;
        }
    }

    private void SetTimelineValue(double value)
    {
        _suppressTimelineValueChanged = true;
        try
        {
            TimelineSlider.Value = Math.Clamp(
                value,
                TimelineSlider.Minimum,
                TimelineSlider.Maximum);
        }
        finally
        {
            _suppressTimelineValueChanged = false;
        }
    }

    private void SeekTo(double seconds)
    {
        if (_mediaPlayer?.Media == null)
            return;

        long targetMs = (long)Math.Round(Math.Max(0, seconds) * 1000);
        if (_currentMediaLengthMs > 0)
            targetMs = Math.Min(targetMs, _currentMediaLengthMs);

        _mediaPlayer.Time = targetMs;
        UpdateTimeLabel(targetMs, _currentMediaLengthMs);
    }

    private void UpdateTimeLabel(long currentMs, long lengthMs)
    {
        TimeLabel.Text = PlaybackTimeLabelFormatter.FormatRange(currentMs, lengthMs);
    }

    /// <summary>
    /// 控制条窄的时候按钮只留图标（图标 + 文字的两个按钮太占宽度，会把进度条挤扁）；
    /// 同时把图标与文字之间的间距、时间标签的最小宽度一起收掉，窄窗口里也留得下进度条。
    /// </summary>
    private void PlaybackControlBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool iconOnly = PlaybackControlBarPolicy.UseIconOnlyButtons(e.NewSize.Width);
        Visibility textVisibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;
        PlayStateText.Visibility = textVisibility;
        ScreenshotText.Visibility = textVisibility;
        LocateFileText.Visibility = textVisibility;
        PlayStateIcon.Margin = iconOnly ? new Thickness(0) : new Thickness(0, 0, 6, 0);
        ScreenshotIcon.Margin = iconOnly ? new Thickness(0) : new Thickness(0, 0, 6, 0);
        LocateFileIcon.Margin = iconOnly ? new Thickness(0) : new Thickness(0, 0, 6, 0);
        TimeLabel.MinWidth = iconOnly ? 0 : 110;
        // 只隐藏文字是不够的：基础按钮样式带 96px 最小宽度，按钮还是那么宽。
        // 换成纯图标样式（去最小宽度、收内边距、方形宽度），宽回来再换回主按钮样式。
        Style buttonStyle = (Style)FindResource(
            iconOnly ? "IconOnlyButtonStyle" : "PrimaryButtonStyle");
        if (!ReferenceEquals(BtnTogglePlay.Style, buttonStyle))
        {
            BtnTogglePlay.Style = buttonStyle;
            BtnScreenshot.Style = buttonStyle;
            BtnLocateFile.Style = buttonStyle;
        }
    }

    private void UpdateLocateButtonState(VideoItem? video = null)
    {
        var current = video ?? VideoList.SelectedItem as VideoItem;
        BtnLocateFile.IsEnabled = current != null && !current.IsUnavailable;
    }
}
