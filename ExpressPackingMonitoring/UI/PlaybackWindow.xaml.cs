using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace ExpressPackingMonitoring.UI
{
    public class VideoItem
    {
        public string DisplayName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string OrderId { get; set; } = "";
        public string Mode { get; set; } = "";
        public string Duration { get; set; } = "";
        public string FileSize { get; set; } = "";
        public string StopReason { get; set; } = "";
        public string VideoCodec { get; set; } = "";
        public string VideoEncoder { get; set; } = "";
        public string SourceDisplay { get; set; } = "";
        public bool IsStoredOnHost { get; set; }
        public bool IsMissing { get; set; }
        public bool IsDeleted { get; set; }
        public bool IsArchiveWarning { get; set; }
        public string ArchiveStatusText { get; set; } = "";
        public string DeleteReason { get; set; } = "";
        public DateTime? DeletedAt { get; set; }
        public FileInfo? File { get; set; }

        public string EncoderDisplay
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(VideoEncoder))
                    return EncodingHelper.GetEncoderLabel(VideoEncoder);
                if (!string.IsNullOrWhiteSpace(VideoCodec))
                    return EncodingHelper.GetCodecLabel(VideoCodec);
                return "";
            }
        }

        public string StatusText
        {
            get
            {
                if (IsDeleted)
                {
                    string reason = string.IsNullOrEmpty(DeleteReason) ? "已删除" : DeleteReason;
                    string time = DeletedAt?.ToString("MM-dd HH:mm") ?? "";
                    return $"已清理 ({reason} {time})";
                }

                if (IsStoredOnHost)
                    return "已保存到主机";
                if (IsArchiveWarning)
                    return ArchiveStatusText;
                return IsMissing ? "文件已丢失" : "";
            }
        }

        public bool IsUnavailable => IsDeleted || IsMissing || IsStoredOnHost;
    }

    public partial class PlaybackWindow : Window
    {
        private readonly string _folderPath;
        private readonly VideoDatabase? _db;
        private readonly bool _showDeletedVideos;
        private bool _hideUnavailable = true;
        private readonly VideoFolderImportService? _videoImportService;
        private readonly Action<string>? _saveImportFolder;
        private readonly Action? _videosImported;
        private string _lastImportFolder;
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _searchTimer;
        private readonly string[] _videoExtensions = [".mp4", ".mkv"];
        private const int PageSize = 50;
        private LibVLC? _libVLC;
        private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;
        private List<VideoItem> _allVideos = new();
        private bool _isDragging;
        private bool _isPlaying;
        private bool _isLoadingVideos;
        private bool _isClosing;
        private bool _videoLoadLoopRunning;
        private bool _playerInitializationFailed;
        private bool _playerInitializing;
        private bool _awaitingFirstFrame;
        private int _currentPage = 1;
        private int _totalVideos;
        private int _videoLoadRequestVersion;
        private VideoLoadRequest? _pendingVideoLoad;
        private long _currentMediaLengthMs;
        private readonly SemaphoreSlim _playerSemaphore = new SemaphoreSlim(1, 1);

        public PlaybackWindow(string folderPath, VideoDatabase? db = null, bool showDeletedVideos = true)
            : this(folderPath, db, showDeletedVideos, null)
        {
        }

        internal PlaybackWindow(
            string folderPath,
            VideoDatabase? db,
            bool showDeletedVideos,
            VideoFolderImportService? videoImportService,
            string lastImportFolder = "",
            Action<string>? saveImportFolder = null,
            Action? videosImported = null)
        {
            InitializeComponent();
            _folderPath = folderPath;
            _db = db;
            _showDeletedVideos = showDeletedVideos;
            _videoImportService = videoImportService;
            _lastImportFolder = lastImportFolder ?? "";
            _saveImportFolder = saveImportFolder;
            _videosImported = videosImported;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += Timer_Tick;
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _searchTimer.Tick += SearchTimer_Tick;

            BtnTogglePlay.IsEnabled = false;
            TimelineSlider.IsEnabled = false;
            TimeLabel.Text = "正在加载列表...";
            UpdateHideUnavailableButtonText();
            Loaded += PlaybackWindow_Loaded;
            BtnImportVideos.Visibility = _videoImportService == null
                ? Visibility.Collapsed
                : Visibility.Visible;
            UpdateLocateButtonState();
        }

        private void BtnImportVideos_Click(object sender, RoutedEventArgs e)
        {
            if (_videoImportService == null)
                return;

            string initialDirectory = _videoImportService.IsFolderManaged(_lastImportFolder)
                ? _lastImportFolder
                : _videoImportService.ManagedRoots.FirstOrDefault(Directory.Exists) ?? _folderPath;
            var dialog = new VideoImportDialog(
                _videoImportService,
                initialDirectory,
                _videoImportService.ManagedRoots.FirstOrDefault(Directory.Exists) ?? _folderPath)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true || dialog.ImportResult is not VideoImportResult result)
                return;

            if (!string.IsNullOrWhiteSpace(dialog.SelectedFolder))
            {
                _lastImportFolder = dialog.SelectedFolder;
                _saveImportFolder?.Invoke(dialog.SelectedFolder);
            }

            if (result.Imported > 0)
            {
                _videosImported?.Invoke();
                RequestVideoLoad(1);
            }

            string summary = result.Cancelled
                ? $"已停止导入\n\n成功 {result.Imported} 个，跳过 {result.Skipped} 个，无法读取 {result.Failed} 个"
                : $"导入完成\n\n成功 {result.Imported} 个，跳过 {result.Skipped} 个，无法读取 {result.Failed} 个";
            AppDialog.ShowMessage(
                this,
                summary,
                result.Cancelled ? "导入已停止" : "导入完成",
                result.Failed > 0 ? AppDialogSeverity.Warning : AppDialogSeverity.Information);
        }

        private void PlaybackWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RequestVideoLoad();
        }

        private void DateFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            RequestVideoLoad(1);
        }

        private void TextFilterChanged(object sender, TextChangedEventArgs e)
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void SearchTimer_Tick(object? sender, EventArgs e)
        {
            _searchTimer.Stop();
            RequestVideoLoad(1);
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
        }

        private void HideUnavailableButton_Click(object sender, RoutedEventArgs e)
        {
            _hideUnavailable = !_hideUnavailable;
            UpdateHideUnavailableButtonText();
            RequestVideoLoad(1);
        }

        private void UpdateHideUnavailableButtonText()
        {
            if (HideUnavailableButtonText != null)
                HideUnavailableButtonText.Text = _hideUnavailable ? "显示异常记录" : "隐藏异常记录";
            if (HideUnavailableButtonIcon != null)
                HideUnavailableButtonIcon.Data = (Geometry)FindResource(
                    _hideUnavailable ? "FluentEyeOffIcon" : "FluentEyeIcon");
        }

        internal static string BuildHiddenHintText(int hiddenCount) =>
            $"已隐藏 {hiddenCount} 条异常记录（文件丢失或已清理）";

        private void UpdateHiddenHint(int hiddenCount)
        {
            bool show = _hideUnavailable && hiddenCount > 0;
            if (HiddenHintPanel != null)
                HiddenHintPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (HiddenHintText != null)
                HiddenHintText.Text = BuildHiddenHintText(hiddenCount);
        }

        private void RequestVideoLoad(int? requestedPage = null)
        {
            if (!IsLoaded || _isClosing)
                return;

            DateTime? start = DpStartDate.SelectedDate;
            DateTime? end = DpEndDate.SelectedDate;
            if (start.HasValue && end.HasValue && start > end)
                (start, end) = (end, start);
            string? keyword = SearchBox?.Text.Trim();
            int page = Math.Max(1, requestedPage ?? _currentPage);

            _pendingVideoLoad = new VideoLoadRequest(start, end, keyword, page);
            _videoLoadRequestVersion++;
            if (!_videoLoadLoopRunning)
                _ = ProcessVideoLoadQueueAsync();
        }

        private async Task ProcessVideoLoadQueueAsync()
        {
            _videoLoadLoopRunning = true;
            _isLoadingVideos = true;
            SetLoadingState(true, "正在加载列表...");
            try
            {
                while (!_isClosing && _pendingVideoLoad is VideoLoadRequest request)
                {
                    _pendingVideoLoad = null;
                    int requestVersion = _videoLoadRequestVersion;
                    (List<VideoItem> Items, int Total, int HiddenCount) result;
                    try
                    {
                        result = await Task.Run(() =>
                            BuildVideoPage(request.Start, request.End, request.Keyword, request.Page));
                        if (!IsCurrentLoadRequest(requestVersion, _videoLoadRequestVersion, _isClosing))
                            continue;

                        int pageCount = GetPageCount(result.Total);
                        int normalizedPage = pageCount == 0 ? 1 : Math.Min(request.Page, pageCount);
                        if (pageCount > 0 && normalizedPage != request.Page)
                        {
                            result = await Task.Run(() =>
                                BuildVideoPage(request.Start, request.End, request.Keyword, normalizedPage));
                            if (!IsCurrentLoadRequest(requestVersion, _videoLoadRequestVersion, _isClosing))
                                continue;
                        }

                        _currentPage = normalizedPage;
                    }
                    catch (Exception ex)
                    {
                        if (!IsCurrentLoadRequest(requestVersion, _videoLoadRequestVersion, _isClosing))
                            continue;

                        _allVideos = new List<VideoItem>();
                        _totalVideos = 0;
                        _currentPage = 1;
                        ShowCurrentPage();
                        UpdateHiddenHint(0);
                        AppDialog.Error(this, $"加载回放列表失败：{ex.Message}", "回放错误");
                        continue;
                    }

                    _allVideos = result.Items;
                    _totalVideos = result.Total;
                    ShowCurrentPage();
                    UpdateHiddenHint(result.HiddenCount);
                }
            }
            finally
            {
                _isLoadingVideos = false;
                _videoLoadLoopRunning = false;
                SetLoadingState(false, "00:00:00 / 00:00:00");
                if (!_isClosing && _pendingVideoLoad.HasValue)
                    _ = ProcessVideoLoadQueueAsync();
            }
        }

        private (List<VideoItem> Items, int Total, int HiddenCount) BuildVideoPage(DateTime? start, DateTime? end, string? keyword, int page)
        {
            var videos = new List<VideoItem>();
            int hiddenCount = 0;
            if (_db != null)
            {
                try
                {
                    if (_hideUnavailable)
                    {
                        hiddenCount = LoadAllVideoItems(start, end, keyword, videos);
                        videos = videos.Where(v => !v.IsDeleted && !v.IsMissing).ToList();
                        int total = videos.Count;
                        return (videos.Skip((page - 1) * PageSize).Take(PageSize).ToList(), total, hiddenCount);
                    }

                    var result = _db.QueryVideosPaged(
                        start,
                        end,
                        string.IsNullOrEmpty(keyword) ? null : keyword,
                        page,
                        PageSize,
                        includeDeleted: _showDeletedVideos,
                        searchMode: VideoSearchMode.ExactOrderIdentifiers);
                    if (result.Total == 0 && !string.IsNullOrWhiteSpace(keyword))
                    {
                        result = _db.QueryVideosPaged(
                            start,
                            end,
                            keyword,
                            page,
                            PageSize,
                            includeDeleted: _showDeletedVideos,
                            searchMode: VideoSearchMode.OrderIdentifierContains);
                     }
                     foreach (var record in result.Records)
                    {
                        videos.Add(CreateVideoItem(record));
                    }
                    return (videos, result.Total, 0);
                 }
                catch
                {
                    videos = new List<VideoItem>();
                    LoadVideosFromFileSystem(videos, start, end);
                }
            }
            else
            {
                LoadVideosFromFileSystem(videos, start, end);
            }

            if (_hideUnavailable)
            {
                int before = videos.Count;
                videos = videos.Where(v => !v.IsDeleted && !v.IsMissing).ToList();
                hiddenCount = before - videos.Count;
            }
            else if (!_showDeletedVideos)
            {
                videos = videos.Where(v => !v.IsDeleted && !v.IsMissing).ToList();
            }
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                string normalized = keyword.Trim();
                videos = videos.Where(v =>
                    v.DisplayName.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                    (v.OrderId?.Contains(normalized, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
            }
            int totalVisible = videos.Count;
            return (videos.Skip((page - 1) * PageSize).Take(PageSize).ToList(), totalVisible, hiddenCount);
        }

        private int LoadAllVideoItems(DateTime? start, DateTime? end, string? keyword, List<VideoItem> videos)
        {
            string searchKeyword = keyword?.Trim() ?? "";
            var records = _db!.QueryVideoRecords(
                start,
                end,
                searchKeyword,
                includeDeleted: true,
                searchMode: VideoSearchMode.ExactOrderIdentifiers);
            if (records.Count == 0 && searchKeyword.Length > 0)
            {
                records = _db.QueryVideoRecords(
                    start,
                    end,
                    searchKeyword,
                    includeDeleted: true,
                    searchMode: VideoSearchMode.OrderIdentifierContains);
            }

            int hidden = 0;
            foreach (var record in records)
            {
                VideoItem item = CreateVideoItem(record);
                videos.Add(item);
                if (item.IsDeleted || item.IsMissing)
                    hidden++;
            }
            return hidden;
        }

        internal static VideoItem CreateVideoItem(VideoRecord record)
        {
            bool deleted = record.IsDeleted;
            bool storedOnHost = string.Equals(
                record.StorageState,
                "Remote",
                StringComparison.OrdinalIgnoreCase);

            // 乐观解析：本地文件存在时优先使用本地路径；本地已清理但已归档
            // （Verified/LocalDeleted）且配置了归档路径时，直接用网络路径播放。
            // 列表构建不做 NAS 探测，避免离线 NAS 阻塞列表加载。
            string localPath = record.FilePath ?? "";
            bool localExists = !string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath);
            string archivePath = record.ArchivePath ?? "";
            bool archiveEligible = !localExists
                && record.ArchiveStatus is VideoArchiveStatus.Verified or VideoArchiveStatus.LocalDeleted
                && !string.IsNullOrWhiteSpace(archivePath);
            string resolvedPath = localExists ? localPath : archiveEligible ? archivePath : "";
            bool missing = !deleted && !storedOnHost && string.IsNullOrWhiteSpace(resolvedPath);
            bool archiveWarning = record.ArchiveStatus is
                VideoArchiveStatus.Conflict
                or VideoArchiveStatus.Failed
                or VideoArchiveStatus.NASFull
                or VideoArchiveStatus.LocalDeleted
                or VideoArchiveStatus.NasDeleted;
            string archiveStatusText = record.ArchiveStatus switch
            {
                VideoArchiveStatus.Conflict => $"归档冲突：网络端已有不同版本，请检查 {record.ArchivePath}",
                VideoArchiveStatus.Failed => $"归档失败，等待自动重试：{record.ArchivePath}",
                VideoArchiveStatus.NASFull => $"归档暂停：NAS 空间不足，请清理 {record.ArchivePath}",
                VideoArchiveStatus.LocalDeleted => record.ArchiveCompletedAt != null
                    ? "已归档（本地副本已清理）"
                    : "本地录像已清理，未备份到 NAS",
                VideoArchiveStatus.NasDeleted => "NAS 副本已循环清理",
                _ => ""
            };
            // 归档路径不创建 FileInfo，避免列表构建触碰 SMB；大小使用数据库记录值。
            FileInfo? info = (!deleted && !missing && !storedOnHost && localExists)
                ? new FileInfo(localPath)
                : null;
            return new VideoItem
            {
                DisplayName = GetOrderDisplayName(record.TrackingNumber, record.OrderId, record.FileName),
                FullPath = string.IsNullOrWhiteSpace(resolvedPath)
                    ? record.FilePath ?? ""
                    : resolvedPath,
                OrderId = record.OrderId,
                Mode = record.Mode,
                Duration = record.DurationSeconds > 0 ? $"{(int)record.DurationSeconds}s" : "",
                FileSize = (deleted || missing || storedOnHost || !localExists)
                    ? FormatFileSize(record.FileSizeBytes)
                    : FormatFileSize(info!.Length),
                StopReason = GetStopReasonDisplay(record.SourceType, record.StopReason),
                VideoCodec = record.VideoCodec,
                VideoEncoder = record.VideoEncoder,
                SourceDisplay = GetSourceDisplay(
                    record.SourceType,
                    record.SourceDeviceId,
                    record.SourceDeviceName,
                    record.SourceDeviceKind),
                IsStoredOnHost = storedOnHost,
                IsMissing = missing,
                IsDeleted = deleted,
                IsArchiveWarning = archiveWarning,
                ArchiveStatusText = archiveStatusText,
                DeleteReason = record.DeleteReason,
                DeletedAt = record.DeletedAt,
                File = info
            };
        }

        private void LoadVideosFromFileSystem(List<VideoItem> videos, DateTime? start, DateTime? end)
        {
            if (!Directory.Exists(_folderPath))
                return;

            DateTime startDate = start?.Date ?? DateTime.MinValue.Date;
            DateTime endDate = end?.Date ?? DateTime.MaxValue.Date;
            foreach (var dateFolder in Directory.EnumerateDirectories(_folderPath))
            {
                string folderName = Path.GetFileName(dateFolder);
                if (!DateTime.TryParse(folderName, out var folderDate))
                    continue;

                if (folderDate.Date < startDate || folderDate.Date > endDate)
                    continue;

                foreach (var file in EnumerateVideoFiles(dateFolder))
                {
                    videos.Add(new VideoItem
                    {
                        DisplayName = GetOrderDisplayName("", "", file.Name),
                        FullPath = file.FullName,
                        FileSize = FormatFileSize(file.Length),
                        File = file
                    });
                }
            }

            videos.Sort((a, b) => DateTime.Compare(b.File?.CreationTime ?? DateTime.MinValue, a.File?.CreationTime ?? DateTime.MinValue));
        }

        internal static string GetOrderDisplayName(string? trackingNumber, string? orderId, string? fileName)
        {
            if (!string.IsNullOrWhiteSpace(trackingNumber))
                return trackingNumber.Trim();
            if (!string.IsNullOrWhiteSpace(orderId))
                return orderId.Trim();

            string stem = Path.GetFileNameWithoutExtension(fileName ?? "").Trim();
            int separatorIndex = stem.IndexOf('_');
            string parsedOrderId = separatorIndex > 0 ? stem[..separatorIndex] : stem;
            return string.IsNullOrWhiteSpace(parsedOrderId) ? "未识别面单" : parsedOrderId;
        }

        internal static string GetSourceDisplay(
            string? sourceType,
            string? sourceDeviceId,
            string? sourceDeviceName,
            string? sourceDeviceKind = null)
        {
            if (!string.Equals(sourceType, "external", StringComparison.OrdinalIgnoreCase))
                return "来源：电脑";

            string name = GetSourceDeviceDisplayName(sourceDeviceId, sourceDeviceName);
            return string.Equals(sourceDeviceKind, "pc", StringComparison.OrdinalIgnoreCase)
                ? $"来源：电脑工位 · {name}"
                : $"来源：{name}";
        }

        internal static string GetSourceDeviceDisplayName(string? sourceDeviceId, string? sourceDeviceName)
        {
            string storedName = sourceDeviceName?.Trim() ?? "";
            if (storedName.Length > 0)
                return storedName;

            string normalizedId = new((sourceDeviceId ?? "")
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
            if (normalizedId.Length > 0)
            {
                string suffix = normalizedId.Length <= 6 ? normalizedId : normalizedId[^6..];
                return $"设备 {suffix}";
            }

            return "手机设备";
        }

        internal static string GetStopReasonDisplay(string? sourceType, string? stopReason)
        {
            string value = stopReason?.Trim() ?? "";
            if (string.Equals(sourceType, "external", StringComparison.OrdinalIgnoreCase)
                && string.Equals(value.Replace(" ", ""), "APP备份", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            return value;
        }

        private IEnumerable<FileInfo> EnumerateVideoFiles(string folderPath)
        {
            var dir = new DirectoryInfo(folderPath);
            foreach (string extension in _videoExtensions)
            {
                foreach (var file in dir.GetFiles($"*{extension}"))
                    yield return file;
            }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes}B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F0}KB";
            return $"{bytes / (1024.0 * 1024.0):F1}MB";
        }

        private void ShowCurrentPage()
        {
            VideoList.ItemsSource = _allVideos;
            int pageCount = GetPageCount();
            PageStatusText.Text = pageCount == 0
                ? "共 0 条"
                : $"第 {_currentPage} / {pageCount} 页，共 {_totalVideos} 条";
            BtnPreviousPage.IsEnabled = !_isLoadingVideos && pageCount > 0 && _currentPage > 1;
            BtnNextPage.IsEnabled = !_isLoadingVideos && pageCount > 0 && _currentPage < pageCount;
            UpdateLocateButtonState();
        }

        private int GetPageCount() => GetPageCount(_totalVideos);

        private static int GetPageCount(int totalVideos) =>
            totalVideos <= 0 ? 0 : (totalVideos + PageSize - 1) / PageSize;

        private void BtnPreviousPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage <= 1) return;
            RequestVideoLoad(_currentPage - 1);
        }

        private void BtnNextPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage >= GetPageCount()) return;
            RequestVideoLoad(_currentPage + 1);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _isClosing = true;
            _pendingVideoLoad = null;
            _videoLoadRequestVersion++;

            // 1. 停止计时器
            _timer?.Stop();
            _searchTimer?.Stop();

            // 2. 彻底释放 LibVLC 资源（注意顺序）
            if (_mediaPlayer != null)
            {
                try
                {
                    // 重要：先解除事件订阅，防止销毁时触发回调导致死锁
                    _mediaPlayer.LengthChanged -= MediaPlayer_LengthChanged;
                    _mediaPlayer.TimeChanged -= MediaPlayer_TimeChanged;
                    _mediaPlayer.EndReached -= MediaPlayer_EndReached;
                    _mediaPlayer.EncounteredError -= MediaPlayer_EncounteredError;

                    if (_mediaPlayer.IsPlaying)
                    {
                        _mediaPlayer.Stop();
                    }

                    // 断开视图连接
                    PlayerView.MediaPlayer = null;

                    _mediaPlayer.Dispose();
                    _mediaPlayer = null;
                }
                catch { }
            }

            if (_libVLC != null)
            {
                try
                {
                    _libVLC.Dispose();
                    _libVLC = null;
                }
                catch { }
            }
        }

        private async void VideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VideoList.SelectedItem is not VideoItem video)
            {
                UpdateLocateButtonState();
                return;
            }

            // 增加 100ms 的防抖，防止极速连点
            await Task.Delay(100);
            if (VideoList.SelectedItem != video) return; // 如果选中的已经变了，就不执行了

            if (video.IsDeleted)
            {
                string reason = string.IsNullOrEmpty(video.DeleteReason) ? "系统清理" : video.DeleteReason;
                string time = video.DeletedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
                AppDialog.Information(
                    this,
                    $"该视频已被覆盖删除，无法播放。\n\n单号: {video.OrderId}\n删除原因: {reason}\n删除时间: {time}\n原始大小: {video.FileSize}\n录制时长: {video.Duration}",
                    "视频已删除");
                UpdateLocateButtonState(video);
                return;
            }

            if (video.IsMissing)
            {
                AppDialog.Error(
                    this,
                    $"视频文件已被外部删除或移动，无法播放。\n\n单号: {video.OrderId}\n路径: {video.FullPath}\n原始大小: {video.FileSize}\n录制时长: {video.Duration}",
                    "文件丢失");
                UpdateLocateButtonState(video);
                return;
            }

            if (video.IsStoredOnHost)
            {
                AppDialog.Information(
                    this,
                    "这段录像已转移到绑定主机，请从录制工位主界面打开主机录像页面查看",
                    "已保存到主机");
                UpdateLocateButtonState(video);
                return;
            }

            PlaySelectedVideo(video);
            UpdateLocateButtonState(video);
        }

        private async void PlaySelectedVideo(VideoItem video)
        {
            // 1. 尝试获取信号量，如果已经在切换中，则直接返回，防止疯狂点击导致的排队
            if (!await _playerSemaphore.WaitAsync(0)) return;

            try
            {
                if (!await EnsurePlayerReadyAsync())
                    return;

                // UI 状态立即重置
                ShowPlaybackCover("正在准备视频...");
                _timer.Stop();
                _currentMediaLengthMs = 0;
                TimelineSlider.Maximum = 0;
                TimelineSlider.Value = 0;
                TimeLabel.Text = "正在切换视频...";

                // 2. 在后台线程执行阻塞的 Stop 操作
                await Task.Run(() =>
                {
                    _mediaPlayer?.Stop();
                });

                // 3. 准备新媒体
                using var media = new Media(_libVLC!, new Uri(video.FullPath));

                // 增加一些优化参数，减少内存压力
                media.AddOption(":file-caching=300"); // 减小缓存

                _awaitingFirstFrame = true;
                if (!_mediaPlayer!.Play(media))
                    throw new InvalidOperationException("播放器未能启动该文件");

                _timer.Start();
                UpdatePlayState(true);
            }
            catch (Exception ex)
            {
                ShowPlaybackCover("视频播放失败");
                UpdatePlayState(false);
                AppDialog.Error(this, $"视频播放失败：{ex.Message}", "播放错误");
            }
            finally
            {
                // 4. 释放信号量，允许下一次切换
                _playerSemaphore.Release();
            }
        }

        private void BtnTogglePlay_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer?.Media == null)
                return;

            if (_isPlaying)
            {
                _mediaPlayer.Pause();
                _timer.Stop();
                UpdatePlayState(false);
            }
            else
            {
                _mediaPlayer.SetPause(false);
                _timer.Start();
                UpdatePlayState(true);
            }
        }

        private void BtnLocateFile_Click(object sender, RoutedEventArgs e)
        {
            if (VideoList.SelectedItem is not VideoItem video || video.IsUnavailable || string.IsNullOrWhiteSpace(video.FullPath))
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
            PlayStateIcon.Data = (Geometry)FindResource(isPlaying ? "FluentPauseIcon" : "FluentPlayIcon");
            PlayStateText.Text = isPlaying ? "暂停" : "播放";
            BtnTogglePlay.ToolTip = isPlaying ? "暂停" : "播放";
        }

        private void MediaPlayer_LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        {
            _currentMediaLengthMs = e.Length;
            Dispatcher.Invoke(() => TimelineSlider.Maximum = Math.Max(0, e.Length / 1000.0));
        }

        private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
        {
            if (_isDragging || _mediaPlayer == null)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                if (!this.IsLoaded) return;
                RevealPlaybackSurfaceAfterFirstFrame();
                TimelineSlider.Value = Math.Max(0, e.Time / 1000.0);
                TimeLabel.Text = $"{TimeSpan.FromMilliseconds(e.Time):hh\\:mm\\:ss} / {TimeSpan.FromMilliseconds(_currentMediaLengthMs):hh\\:mm\\:ss}";
            });
        }

        private void MediaPlayer_EndReached(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _timer.Stop();
                UpdatePlayState(false);
                TimelineSlider.Value = 0;
            });
        }

        private void MediaPlayer_EncounteredError(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                ShowPlaybackCover("视频解码失败");
                _timer.Stop();
                UpdatePlayState(false);
                AppDialog.Error(this, "播放器解码失败，请确认视频文件完整", "播放错误");
            });
        }

        private void ShowPlaybackCover(string message)
        {
            _awaitingFirstFrame = false;
            PlaybackCoverText.Text = message;
            PlaybackCover.Visibility = Visibility.Visible;
        }

        private void RevealPlaybackSurfaceAfterFirstFrame()
        {
            if (!_awaitingFirstFrame)
                return;

            _awaitingFirstFrame = false;
            PlaybackCover.Visibility = Visibility.Collapsed;
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_isDragging || _mediaPlayer?.Media == null)
                return;

            TimeLabel.Text = $"{TimeSpan.FromMilliseconds(_mediaPlayer.Time):hh\\:mm\\:ss} / {TimeSpan.FromMilliseconds(_currentMediaLengthMs):hh\\:mm\\:ss}";
        }

        private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isDragging = true;
            if (_mediaPlayer?.IsPlaying == true)
                _mediaPlayer.Pause();
        }

        private void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (_mediaPlayer == null)
                return;

            _isDragging = false;
            _mediaPlayer.Time = (long)(TimelineSlider.Value * 1000);
            _mediaPlayer.SetPause(false);
            _timer.Start();
            UpdatePlayState(true);
        }

        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDragging)
            {
                TimeLabel.Text = $"{TimeSpan.FromSeconds(e.NewValue):hh\\:mm\\:ss} / {TimeSpan.FromMilliseconds(_currentMediaLengthMs):hh\\:mm\\:ss}";
            }
        }

        private void UpdateLocateButtonState(VideoItem? video = null)
        {
            var current = video ?? VideoList.SelectedItem as VideoItem;
            BtnLocateFile.IsEnabled = current != null && !current.IsUnavailable;
        }

        private async Task<bool> EnsurePlayerReadyAsync()
        {
            if (_playerInitializationFailed)
                return false;

            if (_mediaPlayer != null)
                return true;

            if (_playerInitializing)
                return false;

            _playerInitializing = true;
            TimeLabel.Text = "正在加载播放器...";
            BtnTogglePlay.IsEnabled = false;
            TimelineSlider.IsEnabled = false;

            try
            {
                LibVLC libVLC = null!;
                LibVLCSharp.Shared.MediaPlayer mediaPlayer = null!;

                await Task.Run(() =>
                {
                    Core.Initialize();
                    libVLC = new LibVLC("--avcodec-hw=any");
                    mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(libVLC);
                });

                _libVLC = libVLC;
                _mediaPlayer = mediaPlayer;
                _mediaPlayer.LengthChanged += MediaPlayer_LengthChanged;
                _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
                _mediaPlayer.EndReached += MediaPlayer_EndReached;
                _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
                PlayerView.MediaPlayer = _mediaPlayer;
                BtnTogglePlay.IsEnabled = true;
                TimelineSlider.IsEnabled = true;
                return true;
            }
            catch (Exception ex)
            {
                _playerInitializationFailed = true;
                AppDialog.Error(this, $"播放器初始化失败：{ex.Message}\n\n回放列表仍可查看，但当前机器暂时无法内置播放", "回放错误");
                return false;
            }
            finally
            {
                _playerInitializing = false;
            }
        }

        private void SetLoadingState(bool loading, string statusText)
        {
            BtnPreviousPage.IsEnabled = !loading && _currentPage > 1;
            BtnNextPage.IsEnabled = !loading && _currentPage < GetPageCount();
            TimeLabel.Text = statusText;
        }

        internal static bool IsCurrentLoadRequest(int requestVersion, int currentRequestVersion, bool isClosing) =>
            !isClosing && requestVersion == currentRequestVersion;

        private readonly record struct VideoLoadRequest(
            DateTime? Start,
            DateTime? End,
            string? Keyword,
            int Page);
    }
}
