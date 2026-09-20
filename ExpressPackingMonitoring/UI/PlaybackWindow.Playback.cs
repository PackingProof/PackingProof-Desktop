using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Data;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace ExpressPackingMonitoring.UI;

/// <summary>
/// 回放的"起播时序"与"窗口尺寸"：两件事都只有播放窗口自己知道，所以放在同一个分部里。
///
/// 起播时序：媒体用 <c>:start-paused</c> 打开，等 libvlc 把首帧解出来、视频输出建好
/// （Vout/Paused 事件，或超时兜底）再放开播放 —— 否则时钟立刻开始走，画面还没上屏的前几帧
/// 就被丢掉了（现场反馈"点播放后开头少几帧"）。
///
/// 窗口尺寸：按录像宽高比把视频区撑满，窗口只补固定占位，避免上下黑边；开窗时先用列表第一条
/// （列表按开始时间倒序，就是最新那条）的分辨率算，之后切到别的录像若分辨率不同再按实际尺寸校正。
/// </summary>
public partial class PlaybackWindow
{
    /// <summary>首帧就绪前的兜底时长：某些格式可能不发 Vout/Paused，别把播放卡住。</summary>
    private static readonly TimeSpan FirstFrameFallback = TimeSpan.FromSeconds(1.5);

    private bool _pendingStartUnpause;
    private DispatcherTimer? _startUnpauseFallback;
    private int _fittedVideoWidth;
    private int _fittedVideoHeight;
    private bool _initialWindowFitDone;
    private bool _initialWindowFitRequested;

    private void EnsureStartUnpauseFallbackTimer()
    {
        if (_startUnpauseFallback != null)
            return;

        _startUnpauseFallback = new DispatcherTimer { Interval = FirstFrameFallback };
        _startUnpauseFallback.Tick += (_, _) =>
        {
            _startUnpauseFallback.Stop();
            TryStartPlaybackAfterFirstFrame();
        };
    }

    /// <summary>PlaySelectedVideo 起播前调用：标记等待首帧，并挂上兜底计时。</summary>
    private void BeginAwaitingFirstFrame()
    {
        _awaitingFirstFrame = true;
        _pendingStartUnpause = true;
        EnsureStartUnpauseFallbackTimer();
        _startUnpauseFallback!.Stop();
        _startUnpauseFallback.Start();
    }

    /// <summary>出错、停止、关窗时取消"等首帧"状态，别让兜底计时把已经停掉的播放又放开。</summary>
    private void CancelAwaitingFirstFrame()
    {
        _pendingStartUnpause = false;
        _startUnpauseFallback?.Stop();
    }

    private void MediaPlayer_Vout(object? sender, EventArgs e) => TryStartPlaybackAfterFirstFrame();

    private void MediaPlayer_Paused(object? sender, EventArgs e) => TryStartPlaybackAfterFirstFrame();

    /// <summary>
    /// 首帧（视频输出）就绪：放开播放、揭掉封面，并按这段录像的真实分辨率校正窗口尺寸。
    /// 可能从 libvlc 的线程打过来，也可能由兜底计时打过来，所以统一回到 UI 线程。
    /// </summary>
    private void TryStartPlaybackAfterFirstFrame()
    {
        bool shouldUnpause = _pendingStartUnpause;
        _pendingStartUnpause = false;
        _startUnpauseFallback?.Stop();

        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded || _mediaPlayer == null)
                return;

            if (shouldUnpause)
            {
                _mediaPlayer.SetPause(false);
                _timer.Start();
                UpdatePlayState(true);
            }

            RevealPlaybackSurfaceAfterFirstFrame();
            FitWindowToPlayingVideo();
        });
    }

    /// <summary>用正在播放的这段录像的真实分辨率校正窗口尺寸；分辨率与上次一致就不动窗口。</summary>
    private void FitWindowToPlayingVideo()
    {
        if (_mediaPlayer == null)
            return;

        // 最大化时宽高由系统决定，这里不插手（也不记进"已按这个分辨率调过"）
        if (WindowState == WindowState.Maximized)
            return;

        uint width = 0;
        uint height = 0;
        if (!_mediaPlayer.Size(0, ref width, ref height)
            || width == 0
            || height == 0)
        {
            return;
        }

        if (_fittedVideoWidth == width && _fittedVideoHeight == height)
            return;

        if (ApplyWindowFit((int)width, (int)height))
        {
            _fittedVideoWidth = (int)width;
            _fittedVideoHeight = (int)height;
        }
    }

    /// <summary>
    /// 在窗口**显示之前**按"列表第一条能播的录像"把尺寸定好：调用方 await 完这个方法再 Show()，
    /// 这样用户看到的就是最终尺寸，不会先看到默认大小再跳一下（现场反馈"应该开窗就调好"）。
    ///
    /// 列表按开始时间倒序，第一条就是最新那条；缺文件/已归档的跳过，取前几条里第一条能在本机播的。
    /// 顺带把 libvlc 初始化掉，之后点播放不用再等它。
    /// </summary>
    internal async Task PrepareInitialWindowSizeAsync()
    {
        if (_initialWindowFitDone || _initialWindowFitRequested)
            return;

        _initialWindowFitRequested = true;
        try
        {
            (int Width, int Height)? size = await Task.Run(ProbeNewestPlayableVideoSize);
            if (size is null || _isClosing)
            {
                _initialWindowFitRequested = false;
                return;
            }

            if (ApplyWindowFit(size.Value.Width, size.Value.Height))
            {
                _fittedVideoWidth = size.Value.Width;
                _fittedVideoHeight = size.Value.Height;
            }

            _initialWindowFitDone = true;
        }
        catch (Exception ex)
        {
            _initialWindowFitRequested = false;
            RuntimeLog.Warn("Playback", $"读取录像分辨率失败（窗口尺寸保持默认）：{ex.Message}");
        }
    }

    /// <summary>后台线程：查最新几条录像 → 取第一条能在本机播的 → 解析出分辨率。</summary>
    private (int Width, int Height)? ProbeNewestPlayableVideoSize()
    {
        if (_db == null)
            return null;

        PagedVideoResult page = _db.QueryVideosPaged(
            null,
            null,
            null,
            page: 1,
            pageSize: 5,
            includeDeleted: ShouldIncludeDeletedVideos(_showDeletedVideos, keyword: null),
            searchMode: VideoSearchMode.ExactOrderIdentifiers);
        foreach (VideoRecord record in page.Records)
        {
            VideoItem item = CreateVideoItem(record, _computerName, _currentSourceDeviceNames);
            if (item.IsUnavailable || string.IsNullOrWhiteSpace(item.FullPath))
                continue;

            (int Width, int Height)? size = ProbeVideoSize(item.FullPath);
            if (size != null)
                return size;
        }

        return null;
    }

    /// <summary>
    /// 用 libvlc 解析文件头拿分辨率。只读本地文件，不建视频输出，也不影响正在播放的媒体。
    /// 顺带把 LibVLC 实例建好留给播放器复用（LibVLC 实例本身是进程级共享的，第二个几乎不花时间）。
    /// </summary>
    private (int Width, int Height)? ProbeVideoSize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        Core.Initialize();
        LibVLC? libVlc = _libVLC ??= new LibVLC("--avcodec-hw=any");

        using var media = new Media(libVlc, new Uri(path));
        MediaParsedStatus status = media.Parse(MediaParseOptions.ParseLocal, timeout: 2000)
            .GetAwaiter()
            .GetResult();
        if (status != MediaParsedStatus.Done && media.Tracks.Length == 0)
            return null;

        foreach (MediaTrack track in media.Tracks)
        {
            if (track.TrackType != TrackType.Video)
                continue;

            uint width = track.Data.Video.Width;
            uint height = track.Data.Video.Height;
            if (width > 0 && height > 0)
                return ((int)width, (int)height);
        }

        return null;
    }

    /// <summary>按录像宽高比设置窗口尺寸；算不出来（尺寸未知/工作区异常）返回 false，窗口保持原样。</summary>
    private bool ApplyWindowFit(int videoWidth, int videoHeight)
    {
        double chromeWidth = Math.Max(0, ActualWidth - PlayerView.ActualWidth);
        double chromeHeight = Math.Max(0, ActualHeight - PlayerView.ActualHeight);
        // 以高度为主：保持当前/默认高度，宽度按录像宽高比推出来。
        double preferredHeight = ActualHeight > 0 ? ActualHeight : Height;

        PlaybackWindowSize? size = PlaybackWindowFitPolicy.Calculate(
            videoWidth,
            videoHeight,
            chromeWidth,
            chromeHeight,
            preferredHeight,
            SystemParameters.WorkArea.Width,
            SystemParameters.WorkArea.Height,
            MinWidth,
            MinHeight);
        if (size is null)
            return false;

        Width = size.Value.Width;
        Height = size.Value.Height;
        return true;
    }
}
