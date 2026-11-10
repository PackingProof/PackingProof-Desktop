using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ExpressPackingMonitoring.UI;

public partial class PlaybackWindow
{
    private async void PlaySelectedVideo(VideoItem video)
    {
        int requestVersion = Interlocked.Increment(
            ref _playbackRequestVersion);
        CancelPlaybackPreparation();
        var preparationCts = new CancellationTokenSource();
        _playbackPreparationCts = preparationCts;
        _isPreparingPlayback = true;
        _lastPlaybackFallback = null;
        if (_mediaPlayer?.IsPlaying == true)
            _mediaPlayer.Pause();
        _timer.Stop();
        CancelAwaitingFirstFrame();
        UpdatePlayState(false);
        BtnTogglePlay.IsEnabled = false;
        TimelineSlider.IsEnabled = false;
        ShowPlaybackPreparing(AppLanguage.Get("正在准备视频..."));
        PlaybackMediaSource? mediaSource = null;

        try
        {
            if (!await EnsurePlayerReadyAsync())
                return;
            if (!IsPlaybackRequestCurrent(requestVersion, video))
                return;

            PlaybackPreparationResult preparation =
                await PreparePlaybackPathAsync(video, preparationCts.Token);
            if (!IsPlaybackRequestCurrent(requestVersion, video))
                return;
            if (preparation.State == PlaybackPreparationState.Unavailable)
            {
                ShowPlaybackCover(AppLanguage.Get("录像暂时无法播放，请稍后重试"));
                TimeLabel.Text = PlaybackTimeLabelFormatter.FormatRange(0, 0);
                return;
            }

            _lastPlaybackFallback =
                preparation.State == PlaybackPreparationState.FallbackSource
                    ? preparation
                    : null;
            string playbackPath = preparation.FilePath;
            video.FullPath = playbackPath;
            video.File = new FileInfo(playbackPath);

            try
            {
                await _playerSemaphore.WaitAsync(preparationCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                if (!IsPlaybackRequestCurrent(requestVersion, video))
                    return;

                ShowPlaybackCover(AppLanguage.Get("正在准备视频..."));
                _currentMediaLengthMs = 0;
                SetTimelineMaximum(0);
                SetTimelineValue(0);
                TimeLabel.Text = AppLanguage.Get("正在切换视频...");

                await FitWindowToVideoBeforePlaybackAsync(playbackPath);
                if (!IsPlaybackRequestCurrent(requestVersion, video))
                    return;

                await Task.Run(() =>
                {
                    _mediaPlayer?.Stop();
                }, preparationCts.Token);
                if (!IsPlaybackRequestCurrent(requestVersion, video))
                    return;

                DisposeCurrentMediaSource();
                mediaSource = await Task.Run(
                    () => PlaybackMediaSource.Create(
                        _libVLC!,
                        playbackPath),
                    preparationCts.Token);
                mediaSource.Media.AddOption(":file-caching=300");
                mediaSource.Media.AddOption(":start-paused");

                _isPreparingPlayback = false;
                BeginAwaitingFirstFrame();
                if (!_mediaPlayer!.Play(mediaSource.Media))
                    throw new InvalidOperationException("播放器未能启动该文件");
                _currentMediaSource = mediaSource;
                mediaSource = null;
                BtnTogglePlay.IsEnabled = true;
                TimelineSlider.IsEnabled = true;
            }
            finally
            {
                _playerSemaphore.Release();
            }
        }
        catch (OperationCanceledException)
            when (preparationCts.IsCancellationRequested)
        {
            // 用户已选择另一条录像或关闭窗口。
        }
        catch (Exception ex)
        {
            mediaSource?.Dispose();
            DisposeCurrentMediaSource();
            CancelAwaitingFirstFrame();
            ShowPlaybackCover(GetPlaybackFailureMessage());
            UpdatePlayState(false);
            if (!_isClosing)
            {
                RuntimeLog.Warn(
                    "Playback",
                    $"Playback preparation or start failed file={Path.GetFileName(video.FullPath)}, error={ex.Message}");
            }
        }
        finally
        {
            bool isCurrent = IsPlaybackRequestCurrent(
                requestVersion,
                video);
            if (isCurrent)
                _isPreparingPlayback = false;
            if (ReferenceEquals(_playbackPreparationCts, preparationCts))
                _playbackPreparationCts = null;
            preparationCts.Dispose();
        }
    }

    private async Task<PlaybackPreparationResult> PreparePlaybackPathAsync(
        VideoItem video,
        CancellationToken cancellationToken)
    {
        Func<
            VideoItem,
            string,
            CancellationToken,
            Task<MkvConversionResult>>? prepareMkvForPlayback =
                _prepareMkvForPlayback;
        (string SourcePath, bool IsMkv, bool CanConvertLocally) plan =
            await Task.Run(() =>
            {
                string sourcePath =
                    PlaybackPreparationPolicy.ResolveSourcePath(video.FullPath);
                bool isMkv = sourcePath.EndsWith(
                    ".mkv",
                    StringComparison.OrdinalIgnoreCase);
                return (
                    sourcePath,
                    isMkv,
                    isMkv
                    && prepareMkvForPlayback != null
                    && PlaybackPreparationPolicy.CanConvertOnThisMachine(
                        sourcePath));
            }, cancellationToken);
        Func<CancellationToken, Task<MkvConversionResult>>? converter = null;
        if (plan.CanConvertLocally)
        {
            ShowPlaybackPreparing(
                AppLanguage.Get("正在转码，完成后会自动播放"));
            converter = token => prepareMkvForPlayback!(
                video,
                plan.SourcePath,
                token);
        }
        else if (plan.IsMkv)
        {
            ShowPlaybackPreparing(
                AppLanguage.Get("主机正在准备兼容版本，完成后会自动播放"));
        }

        return await Task.Run(
            () => PlaybackPreparationPolicy.PrepareAsync(
                plan.SourcePath,
                converter,
                RemotePlaybackPreparationTimeout,
                cancellationToken),
            cancellationToken);
    }

    private bool IsPlaybackRequestCurrent(
        int requestVersion,
        VideoItem video) =>
        !_isClosing
        && requestVersion == Volatile.Read(ref _playbackRequestVersion)
        && ReferenceEquals(VideoList.SelectedItem, video);

    private void CancelPlaybackPreparation()
    {
        CancellationTokenSource? preparation = _playbackPreparationCts;
        _playbackPreparationCts = null;
        try
        {
            preparation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void AbandonPlaybackPreparation()
    {
        Interlocked.Increment(ref _playbackRequestVersion);
        CancelPlaybackPreparation();
        _isPreparingPlayback = false;
    }

    private string GetPlaybackFailureMessage()
    {
        if (_lastPlaybackFallback is { ConversionFailed: true })
        {
            return AppLanguage.Get(
                "录像未生成兼容 MP4，原始录像已保留");
        }
        if (_lastPlaybackFallback is { TimedOut: true })
        {
            return AppLanguage.Get(
                "录像仍在准备中，请稍后重新选择");
        }
        return AppLanguage.Get(
            "录像暂时无法播放，请稍后重试");
    }

    private void ShowPlaybackPreparing(string message)
    {
        ShowPlaybackCover(message);
        PlaybackCoverIcon.Visibility = Visibility.Collapsed;
        PlaybackPreparationProgress.Visibility = Visibility.Visible;
        TimeLabel.Text = message;
    }
}
