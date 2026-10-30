using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;
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
    private bool _playbackShutdownStarted;
    /// <summary>当前这段录像的显示尺寸是否已经由解析得到（用来决定还要不要用播放器报的尺寸兜底）。</summary>
    private bool _currentVideoSizeKnown;
    private string _lastProbedPath = "";
    private (int Width, int Height)? _lastProbedSize;

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

    /// <summary>
    /// 关闭回放窗口时先断开 WPF 与 LibVLC 的连接，再把会等待原生线程退出的
    /// Stop/Dispose 放到后台。MediaPlayer.Stop 是同步阻塞调用，留在 UI 线程上时
    /// 一旦视频输出线程需要回 UI 线程，就会出现点击关闭后窗口无响应。
    /// </summary>
    private void BeginPlaybackShutdown()
    {
        if (_playbackShutdownStarted)
            return;

        _playbackShutdownStarted = true;
        _pendingVideoLoad = null;
        _videoLoadRequestVersion++;
        _timer.Stop();
        _searchTimer.Stop();
        _toastTimer?.Stop();
        CancelAwaitingFirstFrame();

        MediaPlayer? player = _mediaPlayer;
        PlaybackMediaSource? source = _currentMediaSource;
        LibVLC? libVLC = _libVLC;
        _mediaPlayer = null;
        _currentMediaSource = null;
        _libVLC = null;

        if (player != null)
            DetachPlaybackMediaPlayerEvents(player);

        try
        {
            PlayerView.MediaPlayer = null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Playback", $"关闭时断开播放视图失败：{ex.Message}");
        }

        RuntimeLog.Info("Playback", "Shutdown started; native player release moved off the UI thread");
        if (player != null || source != null || libVLC != null)
        {
            _ = Task.Run(() =>
                DisposePlaybackResourcesOnBackground(player, source, libVLC));
        }
    }

    private void DetachPlaybackMediaPlayerEvents(MediaPlayer player)
    {
        player.LengthChanged -= MediaPlayer_LengthChanged;
        player.TimeChanged -= MediaPlayer_TimeChanged;
        player.EndReached -= MediaPlayer_EndReached;
        player.EncounteredError -= MediaPlayer_EncounteredError;
        player.Vout -= MediaPlayer_Vout;
        player.Paused -= MediaPlayer_Paused;
    }

    /// <summary>
    /// 原生播放器释放可能阻塞到全部 VLC 线程结束，只允许在后台线程执行。
    /// 每一步单独兜底，前一步异常不能导致后续媒体流或 LibVLC 实例泄漏。
    /// </summary>
    private static void DisposePlaybackResourcesOnBackground(
        MediaPlayer? player,
        PlaybackMediaSource? source,
        LibVLC? libVLC)
    {
        try
        {
            player?.Stop();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Playback", $"关闭时停止播放器失败：{ex.Message}");
        }

        try
        {
            source?.Dispose();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Playback", $"关闭时释放媒体流失败：{ex.Message}");
        }

        try
        {
            player?.Dispose();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Playback", $"关闭时释放播放器失败：{ex.Message}");
        }

        try
        {
            libVLC?.Dispose();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Playback", $"关闭时释放 LibVLC 失败：{ex.Message}");
        }

        RuntimeLog.Info("Playback", "Shutdown completed; native player resources released");
    }

    /// <summary>
    /// 右键“删除录像”：删掉本机录像文件，再把记录标记为删除。
    /// 记录本身保留（列表里不再显示、删除日志留档），备份位置上的归档副本
    /// 按“程序只上传不删除”的既有约定不在这里删。
    /// </summary>
    private async void DeleteVideo_Click(object sender, RoutedEventArgs e)
    {
        VideoItem? item = GetContextMenuItem(sender);
        if (item == null) return;

        // ContextMenu 的 Click 在菜单关闭前触发；先让本次输入返回，再打开模态确认框，
        // 避免在菜单的鼠标捕获/嵌套消息循环里再弹窗。
        await Dispatcher.Yield(DispatcherPriority.Input);
        if (_isClosing) return;

        if (_db == null)
        {
            AppDialog.Error(
                this,
                AppLanguage.Get("录像数据库不可用，无法删除"),
                AppLanguage.Get("删除录像"));
            return;
        }

        string deleteTitle = AppLanguage.Get("删除录像");
        if (!item.CanDelete)
        {
            AppDialog.Warning(
                this,
                string.IsNullOrWhiteSpace(item.DeleteBlockedHint)
                    ? AppLanguage.Get("这条录像不能删除")
                    : item.DeleteBlockedHint,
                deleteTitle);
            return;
        }

        string orderText = string.IsNullOrWhiteSpace(item.OrderId)
            ? AppLanguage.Get("未记录订单号")
            : item.OrderId;
        string timeText = item.StartTime == default
            ? AppLanguage.Get("未知")
            : item.StartTime.ToString("yyyy-MM-dd HH:mm:ss");
        string archiveWarning = AppLanguage.Get(
            item.HasArchiveCopy
                ? "本机录像文件会被删除，已备份到网络位置的归档副本会保留"
                : "本机录像文件会被删除");
        string message = string.Join(
            Environment.NewLine,
            AppLanguage.Get("确定删除这条录像吗？"),
            "",
            AppLanguage.Format("单号: {0}", orderText),
            AppLanguage.Format("时间: {0}", timeText),
            "",
            archiveWarning,
            AppLanguage.Get("删除后列表里不再显示这条记录"));
        if (!AppDialog.Confirm(
                this,
                message,
                deleteTitle,
                AppDialogSeverity.Warning,
                confirmText: AppLanguage.Get("删除"),
                cancelText: AppLanguage.Get("取消"),
                isDangerous: true))
        {
            return;
        }

        VideoDatabase database = _db;
        _deleteInProgress = true;
        try
        {
            if (!await _playerSemaphore.WaitAsync(TimeSpan.FromSeconds(3)))
            {
                AppDialog.Warning(
                    this,
                    AppLanguage.Get("播放器正在切换视频，请稍后再试"),
                    deleteTitle);
                return;
            }

            try
            {
                if (_isClosing)
                    return;

                // 正在播放的这条先停下来：LibVLC 还占着文件句柄时，Windows 上删不掉文件。
                // Stop/Dispose 都放到后台执行，避免原生播放器回调把整个窗口卡住。
                if (ReferenceEquals(VideoList.SelectedItem, item))
                    await StopAndDisposePlaybackForDeletionAsync();

                if (_isClosing)
                    return;

                var service = new RecordingDeleteService(database);
                RecordingDeleteResult result = await service.DeleteRecordingAsync(item.RecordId);
                if (!result.Deleted)
                {
                    AppDialog.Warning(this, result.Message, deleteTitle);
                    return;
                }

                if (ReferenceEquals(VideoList.SelectedItem, item))
                    VideoList.SelectedItem = null;
                ShowPlaybackToast(AppLanguage.Get("已删除该录像"));
                RequestVideoLoad();
            }
            finally
            {
                _playerSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("Playback", "删除录像失败", ex);
            AppDialog.Error(
                this,
                AppLanguage.Format("录像删除失败：{0}", ex.Message),
                deleteTitle);
        }
        finally
        {
            _deleteInProgress = false;
            if (_closeAfterDelete)
            {
                _closeAfterDelete = false;
                _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
            }
        }
    }

    /// <summary>
    /// 删除正在播放的录像前，先彻底放开 LibVLC 对文件的占用。
    /// Stop/Media.Dispose 都可能等待原生事件，必须离开 UI 线程执行；删除入口会先持有
    /// _playerSemaphore，保证这里不会与起播、切歌同时改同一个播放器。
    /// </summary>
    private async Task StopAndDisposePlaybackForDeletionAsync()
    {
        MediaPlayer? player = _mediaPlayer;
        PlaybackMediaSource? source = _currentMediaSource;
        _currentMediaSource = null;
        CancelAwaitingFirstFrame();
        _timer.Stop();
        UpdatePlayState(false);

        if (player != null)
            DetachPlaybackMediaPlayerEvents(player);

        try
        {
            await Task.Run(() =>
            {
                try
                {
                    player?.Stop();
                }
                finally
                {
                    source?.Dispose();
                }
            });
        }
        finally
        {
            if (player != null)
            {
                player.LengthChanged += MediaPlayer_LengthChanged;
                player.TimeChanged += MediaPlayer_TimeChanged;
                player.EndReached += MediaPlayer_EndReached;
                player.EncounteredError += MediaPlayer_EncounteredError;
                player.Vout += MediaPlayer_Vout;
                player.Paused += MediaPlayer_Paused;
            }
        }
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

        // 这段录像的尺寸已经解析出来了就以此为准：播放器刚切过去时 Size() 可能还是上一段的尺寸
        // （现场就是"点了横屏录像，窗口还按竖屏算"）。
        if (_currentVideoSizeKnown)
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

            // 开窗时算的就是列表第一条，用户接着点它时不用再解析一次。
            _lastProbedPath = _initialProbedPath;
            _lastProbedSize = size;
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
            VideoItem item = CreateVideoItem(
                record,
                _computerName,
                _currentSourceDeviceNames,
                _canDeleteRecords);
            if (item.IsUnavailable || string.IsNullOrWhiteSpace(item.FullPath))
                continue;

            (int Width, int Height)? size = ProbeVideoSize(item.FullPath);
            if (size != null)
            {
                _initialProbedPath = item.FullPath;
                return size;
            }
        }

        return null;
    }

    private string _initialProbedPath = "";

    /// <summary>
    /// 用 libvlc 解析文件头拿分辨率。只读本地文件，不建视频输出，也不影响正在播放的媒体。
    /// 顺带把 LibVLC 实例建好留给播放器复用（LibVLC 实例本身是进程级共享的，第二个几乎不花时间）。
    /// </summary>
    private (int Width, int Height)? ProbeVideoSize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        // UNC 走 StreamMediaInput 时 LibVLC 的 Parse 会直接跳过；不要为拿尺寸额外等 2 秒。
        if (PlaybackMediaSource.IsUncPath(path))
            return null;

        try
        {
            Core.Initialize();
            LibVLC? libVlc = _libVLC ??= new LibVLC("--avcodec-hw=any");

            using PlaybackMediaSource source = PlaybackMediaSource.Create(libVlc, path);
            Media media = source.Media;
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
                    return PlaybackWindowFitPolicy.ResolveDisplaySize(width, height, track.Data.Video.Orientation);
            }
        }
        catch
        {
            // 远端文件可能在探测期间断开；尺寸探测失败不应影响后续起播错误处理。
        }

        return null;
    }

    /// <summary>
    /// 点开某段录像时（起播之前）按它的分辨率把窗口调好 —— 不依赖播放器的视频输出状态，
    /// 所以不会出现"播放器还报着上一段尺寸"的问题。解析结果带缓存，同一段只解析一次。
    /// </summary>
    private async Task<bool> FitWindowToVideoBeforePlaybackAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        _currentVideoSizeKnown = false;
        (int Width, int Height)? size;
        if (string.Equals(_lastProbedPath, path, StringComparison.OrdinalIgnoreCase) && _lastProbedSize != null)
        {
            size = _lastProbedSize;
        }
        else
        {
            size = await Task.Run(() => ProbeVideoSize(path));
            _lastProbedPath = path;
            _lastProbedSize = size;
        }

        if (size is null)
            return false;

        if (ApplyWindowFit(size.Value.Width, size.Value.Height))
        {
            _fittedVideoWidth = size.Value.Width;
            _fittedVideoHeight = size.Value.Height;
        }

        _currentVideoSizeKnown = true;
        return true;
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
