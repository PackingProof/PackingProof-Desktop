using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;

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

        // 悬浮提示与右键菜单需要的明细，与 Web 端 buildVideoTooltip 的字段保持一致。
        public string TrackingNumber { get; set; } = "";
        public string SourceOrderId { get; set; } = "";
        public string BuyerMessage { get; set; } = "";
        public string SellerMemo { get; set; } = "";
        public string ProductInfo { get; set; } = "";
        public string OrderInfoPushTime { get; set; } = "";
        public DateTime StartTime { get; set; }
        public string FileName { get; set; } = "";

        /// <summary>右键复制单号时优先取快递单号，与列表显示口径一致。</summary>
        public string CopyableOrderId =>
            !string.IsNullOrWhiteSpace(TrackingNumber) ? TrackingNumber
            : !string.IsNullOrWhiteSpace(OrderId) ? OrderId
            : "";

        /// <summary>只有本地真实存在的文件才能在资源管理器里定位。</summary>
        public bool CanLocateFile =>
            !string.IsNullOrWhiteSpace(FullPath) && !IsDeleted && !IsStoredOnHost && !IsMissing;

        public string ToolTipText => PlaybackTooltipBuilder.Build(this);

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
        private readonly string _computerName;
        /// <summary>设备号→主机当前分配的昵称。记录里存的是写入当时的名字，改名后要靠它归并。</summary>
        private readonly IReadOnlyDictionary<string, string>? _currentSourceDeviceNames;
        private readonly VideoDatabase? _db;
        private readonly bool _showDeletedVideos;
        private bool _excludeUnavailableRecords;
        private readonly VideoFolderImportService? _videoImportService;
        private readonly Action<string>? _saveImportFolder;
        private readonly Action? _videosImported;
        private string _lastImportFolder;
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _searchTimer;
        private DispatcherTimer? _toastTimer;
        private readonly string[] _videoExtensions = [".mp4", ".mkv"];
        private const int PageSize = 50;
        private LibVLC? _libVLC;
        private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;
        private List<VideoItem> _allVideos = new();
        private bool _isExportingOrderNumbers;
        private bool _isDragging;
        /// <summary>拖动进度条前的播放状态：拖动时先暂停，拖完按这个状态恢复。</summary>
        private bool _wasPlayingBeforeScrub;
        private bool _suppressTimelineValueChanged;
        private bool _isPlaying;
        private bool _isLoadingVideos;
        private bool _isClosing;
        private bool _videoLoadLoopRunning;
        private bool _playerInitializationFailed;
        private Task<bool>? _playerReadyTask;
        private bool _awaitingFirstFrame;
        private int _currentPage = 1;
        private int _totalVideos;
        private bool _hasMoreVideoPages;
        private bool _usingApproximatePaging;
        private int _videoLoadRequestVersion;
        private VideoLoadRequest? _pendingVideoLoad;
        private long _currentMediaLengthMs;
        private readonly RecordingFilterState _filterState = new();
        private bool _suppressFilterEvents;
        private readonly SemaphoreSlim _playerSemaphore = new SemaphoreSlim(1, 1);

        public PlaybackWindow(
            string folderPath,
            VideoDatabase? db = null,
            bool showDeletedVideos = true,
            string localComputerName = "")
            : this(folderPath, db, showDeletedVideos, null, localComputerName: localComputerName)
        {
        }

        internal PlaybackWindow(
            string folderPath,
            VideoDatabase? db,
            bool showDeletedVideos,
            VideoFolderImportService? videoImportService,
            string lastImportFolder = "",
            Action<string>? saveImportFolder = null,
            Action? videosImported = null,
            string localComputerName = "",
            IReadOnlyDictionary<string, string>? currentSourceDeviceNames = null)
        {
            InitializeComponent();
            _folderPath = folderPath;
            _computerName = localComputerName ?? "";
            _currentSourceDeviceNames = currentSourceDeviceNames;
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
            _excludeUnavailableRecords = !showDeletedVideos;
            Loaded += PlaybackWindow_Loaded;
            BtnImportVideos.Visibility = _videoImportService == null
                ? Visibility.Collapsed
                : Visibility.Visible;
            UpdateExportOrderNumbersButtonState();
            LoadSourceFilterOptions();
            UpdateLocateButtonState();
        }

        private async void ExportOrderNumbersButton_Click(object sender, RoutedEventArgs e)
        {
            if (_db == null || !ExportOrderNumbersButton.IsEnabled)
                return;

            // 导出必须沿用筛选面板里当前生效的条件（日期、业务、设备），
            // 否则筛了设备导出来的表里还会混进别的设备。
            _filterState.NormalizeDateRange();
            DateTime? start = _filterState.StartDate?.Date;
            DateTime? end = _filterState.EndDate?.Date;
            // 来源类型直接取下拉项带过来的值，不再靠"有没有设备号"去猜；
            // 同名多设备合并后设备号是空的，猜的话会把手机筛成本机。
            bool localOnly = string.Equals(_filterState.SourceType, "pc", StringComparison.OrdinalIgnoreCase);
            var filter = new OrderNumberExportFilter(
                start,
                end,
                _filterState.Mode,
                _filterState.SourceId,
                localOnly ? "" : _filterState.SourceName,
                _filterState.SourceType,
                localOnly ? Array.Empty<string>() : _filterState.SourceIds);

            var saveDialog = new SaveFileDialog
            {
                Title = "导出单号",
                Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
                DefaultExt = ".xlsx",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = $"单号_{BuildOrderExportRangeName(start, end)}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
            };
            if (saveDialog.ShowDialog(this) != true)
                return;

            _isExportingOrderNumbers = true;
            ExportOrderNumbersButton.IsEnabled = false;
            ExportOrderNumbersButtonText.Text = "正在导出...";
            try
            {
                await StopPlaybackForExportAsync();
                if (_isClosing)
                    return;

                var progressDialog = new OrderNumberExportProgressDialog(
                    _db,
                    filter,
                    saveDialog.FileName,
                    _currentSourceDeviceNames,
                    _computerName)
                {
                    Owner = this
                };
                progressDialog.ShowDialog();

                switch (progressDialog.Outcome)
                {
                    case OrderNumberExportOutcome.Success:
                        LocateExportedOrderFile(saveDialog.FileName);
                        break;
                    case OrderNumberExportOutcome.Empty:
                        AppDialog.Information(this, "当前日期范围内没有可导出的单号", "导出单号");
                        break;
                    case OrderNumberExportOutcome.Cancelled:
                        AppDialog.Information(this, "已取消导出，未生成文件", "已取消导出");
                        break;
                    case OrderNumberExportOutcome.Failed:
                        AppDialog.Error(
                            this,
                            $"导出单号失败：{progressDialog.FailureMessage}",
                            "导出失败");
                        break;
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Playback", "导出单号失败", ex);
                AppDialog.Error(this, $"导出单号失败：{ex.Message}", "导出失败");
            }
            finally
            {
                ExportOrderNumbersButtonText.Text = "导出单号";
                _isExportingOrderNumbers = false;
                UpdateExportOrderNumbersButtonState();
            }
        }

        /// <summary>
        /// 右键菜单挂在 ListViewItem 上，DataContext 就是这一行；
        /// 取不到时说明菜单不是从行上弹出的，直接忽略而不是操作当前选中项。
        /// </summary>
        private static VideoItem? GetContextMenuItem(object sender) =>
            (sender as FrameworkElement)?.DataContext as VideoItem;

        private void CopyOrderId_Click(object sender, RoutedEventArgs e)
        {
            VideoItem? item = GetContextMenuItem(sender);
            if (item == null) return;

            if (string.IsNullOrWhiteSpace(item.CopyableOrderId))
            {
                AppDialog.Information(this, "这条录像没有记录单号", "复制单号");
                return;
            }

            CopyToClipboard(item.CopyableOrderId, "单号已复制");
        }

        private void CopyFilePath_Click(object sender, RoutedEventArgs e)
        {
            VideoItem? item = GetContextMenuItem(sender);
            if (item == null) return;

            string path = string.IsNullOrWhiteSpace(item.FullPath) ? item.FileName : item.FullPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                AppDialog.Information(this, "这条录像没有记录文件路径", "复制文件路径");
                return;
            }

            CopyToClipboard(path, "文件路径已复制");
        }

        private void LocateFile_Click(object sender, RoutedEventArgs e)
        {
            VideoItem? item = GetContextMenuItem(sender);
            if (item == null) return;

            if (!item.CanLocateFile)
            {
                AppDialog.Error(this, "录像已清理或不在本机，无法定位文件", "定位失败");
                return;
            }

            LocateExportedOrderFile(item.FullPath);
        }

        /// <summary>
        /// 右键菜单弹出时必须先关掉悬浮提示。
        /// 提示是 Popup，层级和菜单相当，不关掉就会盖住菜单挡住操作；
        /// 菜单显示期间也禁用提示，避免鼠标在菜单上移动时又把它勾出来。
        /// </summary>
        private void VideoRowContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if ((sender as ContextMenu)?.PlacementTarget is not FrameworkElement row) return;

            ToolTipService.SetIsEnabled(row, false);
            if (row.ToolTip is ToolTip tooltip)
                tooltip.IsOpen = false;
        }

        private void VideoRowContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            if ((sender as ContextMenu)?.PlacementTarget is FrameworkElement row)
                ToolTipService.SetIsEnabled(row, true);
        }

        /// <summary>剪贴板是全局独占资源，输入法、剪贴板管理器或其它程序正占用时
        /// OpenClipboard 会直接失败（CLIPBRD_E_CANT_OPEN）。这是常见的瞬时冲突，
        /// 重试几次基本都能成功，不该一次失败就弹错误框。
        /// </summary>
        private void CopyToClipboard(string text, string successMessage)
        {
            const int maxAttempts = 10;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    // copy: true 让内容在本程序退出后仍留在剪贴板里。
                    Clipboard.SetDataObject(text, copy: true);
                    ShowPlaybackToast(successMessage);
                    return;
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    System.Diagnostics.Debug.WriteLine($"[Playback] 复制到剪贴板第 {attempt} 次失败，稍后重试：{ex.Message}");
                    Thread.Sleep(60);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("Playback", $"复制到剪贴板失败：{ex.Message}");
                    AppDialog.Error(
                        this,
                        "剪贴板被其它程序占用，复制失败。请稍后重试，或关闭输入法、剪贴板管理器等占用剪贴板的程序",
                        "复制失败");
                    return;
                }
            }
        }

        /// <summary>复制类操作的轻提示，2 秒后自动收起，不打断当前操作。</summary>
        private void ShowPlaybackToast(string message)
        {
            PlaybackToastText.Text = message;
            PlaybackToast.Visibility = Visibility.Visible;

            _toastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _toastTimer.Stop();
            _toastTimer.Tick -= PlaybackToastTimer_Tick;
            _toastTimer.Tick += PlaybackToastTimer_Tick;
            _toastTimer.Start();
        }

        private void PlaybackToastTimer_Tick(object? sender, EventArgs e)
        {
            _toastTimer?.Stop();
            PlaybackToast.Visibility = Visibility.Collapsed;
        }

        private void LocateExportedOrderFile(string filePath)
        {
            try
            {
                FileLocationResult result = WindowsShellFileLocator.Locate(filePath);
                if (result == FileLocationResult.OpenedFolder)
                {
                    AppDialog.Information(
                        this,
                        "已打开表格所在文件夹，但系统未能自动选中导出文件",
                        "定位导出文件");
                }
                else if (result != FileLocationResult.Selected)
                {
                    AppDialog.Error(this, "导出文件不存在或路径无效", "定位失败");
                }
            }
            catch (Exception ex)
            {
                AppDialog.Error(this, $"无法打开文件管理器：{ex.Message}", "定位失败");
            }
        }

        private async Task StopPlaybackForExportAsync()
        {
            await _playerSemaphore.WaitAsync();
            try
            {
                if (_isClosing)
                    return;

                _timer.Stop();
                _awaitingFirstFrame = false;
                CancelAwaitingFirstFrame();
                await Task.Run(() => _mediaPlayer?.Stop());
                UpdatePlayState(false);
                _currentMediaLengthMs = 0;
                SetTimelineMaximum(0);
                SetTimelineValue(0);
                TimeLabel.Text = PlaybackTimeLabelFormatter.FormatRange(0, 0);
                ShowPlaybackCover("请选择录像开始播放");
            }
            finally
            {
                _playerSemaphore.Release();
            }
        }

        internal static string BuildOrderExportRangeName(DateTime? start, DateTime? end)
        {
            if (!start.HasValue && !end.HasValue)
                return "全部";
            if (start.HasValue && end.HasValue)
                return $"{start:yyyyMMdd}-{end:yyyyMMdd}";
            return start.HasValue ? $"{start:yyyyMMdd}起" : $"截至{end:yyyyMMdd}";
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
            ApplyDatePickerLimits();
            RequestVideoLoad();
            // 提前把播放器（和 libvlc 实例）准备好：一是开窗时就能解析列表第一条录像的分辨率、
            // 把窗口调到没有黑边；二是首次点播放不用再等初始化。
            _ = EnsurePlayerReadyAsync();
        }

        private void DateFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressFilterEvents) return;

            // 先把选择器上的日期同步进筛选状态，再刷新徽章，
            // 否则角标和胶囊上显示的还是改动前的日期，看起来像"改了没反应"。
            SyncDateFilterFromPickers();
            ApplyDatePickerLimits();
            RefreshFilterIndicators();
            RequestVideoLoad(1);
        }

        /// <summary>
        /// 录像只可能发生在今天或更早，所以日历不给选未来的日子；
        /// 结束日期也不能早于开始日期，省得选完又被交换。
        /// </summary>
        private void ApplyDatePickerLimits()
        {
            (DateTime? startMax, DateTime? endMin, DateTime endMax) =
                RecordingFilterState.BuildDatePickerLimits(
                    DpStartDate.SelectedDate,
                    DpEndDate.SelectedDate,
                    DateTime.Today);
            DpStartDate.DisplayDateEnd = startMax;
            DpEndDate.DisplayDateStart = endMin;
            DpEndDate.DisplayDateEnd = endMax;
        }

        private void SyncDateFilterFromPickers()
        {
            _filterState.StartDate = DpStartDate.SelectedDate;
            _filterState.EndDate = DpEndDate.SelectedDate;
            _filterState.NormalizeDateRange();
        }

        /// <summary>
        /// 筛选按钮呼出面板。面板里的日期、来源、类型改动都即时生效，
        /// 不再需要一个"应用"按钮。
        /// </summary>
        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            FilterPopup.IsOpen = !FilterPopup.IsOpen;
        }

        /// <summary>
        /// 点面板以外的地方收起筛选面板。两种点击要放过：
        /// 面板自己（弹窗内容的路由事件会冒泡到 Popup 的逻辑父级，也会走到这里，
        /// 不放过的话点日期、来源、发退货都会把面板关掉，等于没法操作），
        /// 以及筛选按钮本身（这里先关、紧接着按钮的 Click 又开，第二次点击等于没关）。
        /// </summary>
        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!FilterPopup.IsOpen) return;
            if (e.OriginalSource is DependencyObject source && IsWithinFilterUi(source)) return;
            FilterPopup.IsOpen = false;
        }

        /// <summary>
        /// 判断点击是否落在筛选按钮或筛选面板里。
        /// 面板、日历、下拉都是独立弹窗，视觉树到 PopupRoot 就断了，
        /// 断了之后要接着走逻辑树才能回到 Popup 本身。
        /// </summary>
        private bool IsWithinFilterUi(DependencyObject source)
        {
            var pending = new Stack<DependencyObject>();
            var seen = new HashSet<DependencyObject>();
            pending.Push(source);

            while (pending.Count > 0)
            {
                DependencyObject node = pending.Pop();
                if (!seen.Add(node))
                    continue;
                if (ReferenceEquals(node, FilterButton) || ReferenceEquals(node, FilterPopup))
                    return true;

                // 视觉树和逻辑树都要往上找：模板里的元素只有视觉父级，
                // 弹窗内容的视觉父级是 PopupRoot、只有逻辑父级才是 Popup 本身。
                if (node is Visual or System.Windows.Media.Media3D.Visual3D
                    && VisualTreeHelper.GetParent(node) is DependencyObject visualParent)
                {
                    pending.Push(visualParent);
                }

                if (LogicalTreeHelper.GetParent(node) is DependencyObject logicalParent)
                    pending.Push(logicalParent);
            }

            return false;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || !FilterPopup.IsOpen) return;
            FilterPopup.IsOpen = false;
            e.Handled = true;
        }

        private void Window_Deactivated(object sender, EventArgs e) => FilterPopup.IsOpen = false;

        private void SourceFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressFilterEvents) return;

            if (SourceFilterBox.SelectedItem is VideoSourceOption option)
            {
                _filterState.SourceId = option.DeviceId;
                _filterState.SourceType = option.IsAll ? "" : option.SourceType;
                _filterState.SourceName = option.IsAll ? "" : option.Name;
                // 同名多设备合并成一项时按设备号集合筛选，历史记录（含改名前的）都不会漏。
                _filterState.SourceIds = option.IsAll ? Array.Empty<string>() : option.DeviceIds ?? Array.Empty<string>();
            }
            else
            {
                _filterState.SourceId = "";
                _filterState.SourceType = "";
                _filterState.SourceName = "";
                _filterState.SourceIds = Array.Empty<string>();
            }

            RefreshFilterIndicators();
            RequestVideoLoad(1);
        }

        private void ModeFilterChanged(object sender, RoutedEventArgs e)
        {
            // XAML 里第一个 RadioButton 带 IsChecked="True"，BAML 解析到它就会触发 Checked，
            // 那时后面两个同组按钮的字段还没赋值，直接取 IsChecked 会 NRE，
            // 表现为"打开回放窗口失败"。构造未完成时不处理，默认值由构造函数补。
            if (_suppressFilterEvents || ModeReturnRadio is null || ModeShippingRadio is null) return;

            _filterState.Mode =
                ModeReturnRadio.IsChecked == true ? RecordingModeFilter.Return
                : ModeShippingRadio.IsChecked == true ? RecordingModeFilter.Shipping
                : "";

            RefreshFilterIndicators();
            RequestVideoLoad(1);
        }

        private void ClearAllFilters_Click(object sender, RoutedEventArgs e)
        {
            ApplyFilterStateToControls(state => state.ClearAll());
            FilterPopup.IsOpen = false;
        }

        /// <summary>点胶囊徽章上的叉，只清除这一项筛选。</summary>
        private void RemoveFilterBadge_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string key) return;
            ApplyFilterStateToControls(state => state.Clear(key));
        }

        /// <summary>
        /// 改筛选状态并把控件同步回去。
        /// 期间要屏蔽控件事件，否则每改一个控件都会触发一次查询。
        /// </summary>
        private void ApplyFilterStateToControls(Action<RecordingFilterState> mutate)
        {
            mutate(_filterState);

            _suppressFilterEvents = true;
            try
            {
                // 先放开日历的上下限，否则新日期落在旧限制之外时会被拒掉
                DpStartDate.DisplayDateEnd = null;
                DpEndDate.DisplayDateStart = null;
                DpEndDate.DisplayDateEnd = null;
                DpStartDate.SelectedDate = _filterState.StartDate;
                DpEndDate.SelectedDate = _filterState.EndDate;

                if (_filterState.HasModeFilter)
                {
                    ModeReturnRadio.IsChecked = _filterState.Mode == RecordingModeFilter.Return;
                    ModeShippingRadio.IsChecked = _filterState.Mode == RecordingModeFilter.Shipping;
                    ModeAllRadio.IsChecked = false;
                }
                else
                {
                    ModeAllRadio.IsChecked = true;
                }

                if (!_filterState.HasSourceFilter && SourceFilterBox.Items.Count > 0)
                    SourceFilterBox.SelectedIndex = 0;
            }
            finally
            {
                _suppressFilterEvents = false;
            }

            ApplyDatePickerLimits();
            RefreshFilterIndicators();
            RequestVideoLoad(1);
        }

        /// <summary>刷新筛选按钮角标与胶囊徽章。</summary>
        private void RefreshFilterIndicators()
        {
            IReadOnlyList<RecordingFilterBadge> badges = _filterState.BuildBadges();
            FilterBadgeList.ItemsSource = badges;
            FilterBadgeList.Visibility = badges.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            CountBadge.Visibility = _filterState.ActiveCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            FilterCountText.Text = _filterState.ActiveCount.ToString();
        }

        /// <summary>
        /// 来源下拉项。IsAll 用于区分"全部设备"这一项。
        /// SourceType 必须一起带上：同名多设备合并后 DeviceId 是空的，
        /// 只靠 DeviceId 分不出要筛本机还是外部设备。
        /// </summary>
        private sealed record VideoSourceOption(
            string Name,
            string SourceType,
            string DeviceId,
            bool IsAll,
            IReadOnlyList<string>? DeviceIds = null);

        /// <summary>
        /// 填充来源下拉。设备列表来自数据库里出现过的来源，
        /// 没有录像时只保留"全部设备"。
        /// </summary>
        private void LoadSourceFilterOptions()
        {
            var options = new List<VideoSourceOption> { new("全部设备", "", "", true) };
            try
            {
                if (_db != null)
                {
                    // 同一台手机换过设备号就会在数据库里分成多组，
                    // 不按显示名合并的话下拉里会出现好几个"手机1"。
                    // 本机那一项的名字走 GetSourceDisplay，和 Web 端的命名口径保持一致。
                    IReadOnlyList<VideoSourceFilterOption> grouped = VideoSourceFilterOptions.Build(
                        _db.GetVideoSources(),
                        source => string.Equals(source.SourceType, "external", StringComparison.OrdinalIgnoreCase)
                            ? GetSourceDeviceDisplayName(
                                source.DeviceId,
                                ResolveCurrentSourceDeviceName(source.DeviceId, source.DeviceName))
                            : GetSourceDisplay(source.SourceType, source.DeviceId, source.DeviceName, null, _computerName));
                    foreach (VideoSourceFilterOption source in grouped)
                        options.Add(new VideoSourceOption(source.Name, source.SourceType, source.DeviceId, false, source.FilterDeviceIds));
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Playback", $"读取录像来源失败：{ex.Message}");
            }

            _suppressFilterEvents = true;
            try
            {
                SourceFilterBox.ItemsSource = options;
                SourceFilterBox.SelectedIndex = 0;
            }
            finally
            {
                _suppressFilterEvents = false;
            }
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

        private void RequestVideoLoad(int? requestedPage = null)
        {
            if (!IsLoaded || _isClosing)
                return;

            SyncDateFilterFromPickers();
            string? keyword = SearchBox?.Text.Trim();
            int page = Math.Max(1, requestedPage ?? _currentPage);

            _pendingVideoLoad = new VideoLoadRequest(
                _filterState.StartDate,
                _filterState.EndDate,
                keyword,
                page,
                _filterState.Mode,
                _filterState.SourceType,
                _filterState.SourceId,
                _filterState.SourceName,
                _filterState.SourceIds);
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
                    VideoPageLoadResult result;
                    try
                    {
                        result = await Task.Run(() =>
                            BuildVideoPage(request.Start, request.End, request.Keyword, request.Page, request.Mode, request.SourceType, request.SourceId, request.SourceName, request.SourceIds));
                        if (!IsCurrentLoadRequest(requestVersion, _videoLoadRequestVersion, _isClosing))
                            continue;

                        int pageCount = result.UsesApproximatePaging ? 0 : GetPageCount(result.Total);
                        int normalizedPage = result.UsesApproximatePaging
                            ? result.Page
                            : pageCount == 0 ? request.Page : Math.Min(request.Page, pageCount);
                        if (!result.UsesApproximatePaging && pageCount > 0 && normalizedPage != request.Page)
                        {
                            result = await Task.Run(() =>
                                BuildVideoPage(request.Start, request.End, request.Keyword, normalizedPage, request.Mode, request.SourceType, request.SourceId, request.SourceName, request.SourceIds));
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
                        _hasMoreVideoPages = false;
                        _usingApproximatePaging = false;
                        _currentPage = 1;
                        ShowCurrentPage();
                        AppDialog.Error(this, $"加载回放列表失败：{ex.Message}", "回放错误");
                        continue;
                    }

                    _allVideos = result.Items;
                    _totalVideos = result.Total;
                    _hasMoreVideoPages = result.HasMore;
                    _usingApproximatePaging = result.UsesApproximatePaging;
                    ShowCurrentPage();
                }
            }
            finally
            {
                _isLoadingVideos = false;
                _videoLoadLoopRunning = false;
                SetLoadingState(false, PlaybackTimeLabelFormatter.FormatRange(0, 0));
                if (!_isClosing && _pendingVideoLoad.HasValue)
                    _ = ProcessVideoLoadQueueAsync();
            }
        }

        /// <param name="Page">
        /// 实际取到内容的页码。排除不可用录像时可能跳过整页空结果，
        /// 上一页、下一页要从这里接着走，否则会来回翻同一段。
        /// </param>
        private sealed record VideoPageLoadResult(
            List<VideoItem> Items,
            int Total,
            bool HasMore,
            bool UsesApproximatePaging,
            int Page);

        private VideoPageLoadResult BuildVideoPage(
            DateTime? start,
            DateTime? end,
            string? keyword,
            int page,
            string mode = "",
            string sourceType = "",
            string sourceId = "",
            string sourceName = "",
            IReadOnlyList<string>? sourceIds = null)
        {
            var videos = new List<VideoItem>();
            bool hasSearchKeyword = !string.IsNullOrWhiteSpace(keyword);
            string normalizedMode = RecordingModeFilter.Normalize(mode);
            // 同名多设备合并后只有 SourceType 与设备号集合，本机那一项也只有 SourceType，
            // 所以有没有筛来源要连它们一起看，不然选了等于没筛。
            bool hasSourceFilter = !string.IsNullOrWhiteSpace(sourceType)
                || !string.IsNullOrWhiteSpace(sourceId)
                || !string.IsNullOrWhiteSpace(sourceName)
                || (sourceIds?.Count ?? 0) > 0;
            if (_db != null)
            {
                try
                {
                    // 来源筛选只有分页查询支持，命中时不能再走排除不可用的快捷路径。
                    if (_excludeUnavailableRecords && !hasSearchKeyword && !hasSourceFilter)
                    {
                        // 文件在不在磁盘上只能逐条判断，数据库分页管不了，
                        // 所以这一页的文件全都不在时会一条都显示不出来，底下却还写着"共 N 条"。
                        // 遇到整页都被筛掉就继续往后取，跳过这些空页，别让界面看着像没录像。
                        int windowPage = page;
                        bool hasMore;
                        int total = 0;
                        while (true)
                        {
                            CursorVideoResult window = _db.QueryVideosWindow(
                                start,
                                end,
                                "",
                                windowPage,
                                PageSize,
                                includeDeleted: false,
                                searchMode: VideoSearchMode.ExactOrderIdentifiers,
                                mode: normalizedMode);

                            if (windowPage == page)
                                total = window.Total;
                            hasMore = window.HasMore;
                            videos.AddRange(window.Records
                                .Select(record => CreateVideoItem(record, _computerName, _currentSourceDeviceNames))
                                .Where(item => !item.IsMissing));

                            if (videos.Count > 0 || !hasMore)
                                break;
                            windowPage++;
                        }

                        return new VideoPageLoadResult(videos, total, hasMore, true, windowPage);
                    }

                    var result = _db.QueryVideosPaged(
                        start,
                        end,
                        string.IsNullOrEmpty(keyword) ? null : keyword,
                        page,
                        PageSize,
                        includeDeleted: ShouldIncludeDeletedVideos(_showDeletedVideos, keyword),
                        searchMode: VideoSearchMode.ExactOrderIdentifiers,
                        sourceType: sourceType ?? "",
                        deviceId: sourceId ?? "",
                        sourceDeviceName: sourceName ?? "",
                        mode: normalizedMode,
                        deviceIds: sourceIds);
                    if (result.Total == 0 && !string.IsNullOrWhiteSpace(keyword))
                    {
                        result = _db.QueryVideosPaged(
                            start,
                            end,
                            keyword,
                            page,
                            PageSize,
                            includeDeleted: ShouldIncludeDeletedVideos(_showDeletedVideos, keyword),
                            searchMode: VideoSearchMode.OrderIdentifierContains,
                            sourceType: sourceType ?? "",
                            deviceId: sourceId ?? "",
                            sourceDeviceName: sourceName ?? "",
                            mode: normalizedMode,
                            deviceIds: sourceIds);
                     }
                     foreach (var record in result.Records)
                    {
                        videos.Add(CreateVideoItem(record, _computerName, _currentSourceDeviceNames));
                    }
                    return new VideoPageLoadResult(videos, result.Total, page * PageSize < result.Total, false, page);
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

            if (_excludeUnavailableRecords)
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
            return new VideoPageLoadResult(
                videos.Skip((page - 1) * PageSize).Take(PageSize).ToList(),
                totalVisible,
                videos.Count > page * PageSize,
                false,
                page);
        }

        internal static bool ShouldIncludeDeletedVideos(bool showDeletedVideos, string? keyword) =>
            showDeletedVideos || !string.IsNullOrWhiteSpace(keyword);

        internal static VideoItem CreateVideoItem(
            VideoRecord record,
            string? localComputerName = null,
            IReadOnlyDictionary<string, string>? currentSourceDeviceNames = null)
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
                    ResolveCurrentSourceDeviceName(currentSourceDeviceNames, record.SourceDeviceId, record.SourceDeviceName),
                    record.SourceDeviceKind,
                    localComputerName),
                IsStoredOnHost = storedOnHost,
                IsMissing = missing,
                IsDeleted = deleted,
                IsArchiveWarning = archiveWarning,
                ArchiveStatusText = archiveStatusText,
                DeleteReason = record.DeleteReason,
                DeletedAt = record.DeletedAt,
                File = info,
                TrackingNumber = record.TrackingNumber ?? "",
                SourceOrderId = record.SourceOrderId ?? "",
                BuyerMessage = record.BuyerMessage ?? "",
                SellerMemo = record.SellerMemo ?? "",
                ProductInfo = record.ProductInfo ?? "",
                OrderInfoPushTime = record.OrderInfoPushTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                StartTime = record.StartTime,
                FileName = record.FileName
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
            string? sourceDeviceKind = null,
            string? localComputerName = null)
        {
            if (!string.Equals(sourceType, "external", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(localComputerName) ? "电脑" : localComputerName.Trim();

            string name = GetSourceDeviceDisplayName(sourceDeviceId, sourceDeviceName);
            return string.Equals(sourceDeviceKind, "pc", StringComparison.OrdinalIgnoreCase)
                ? $"电脑工位 · {name}"
                : name;
        }

        /// <summary>
        /// 记录里的来源名是写入当时的快照，设备改名后会留下老昵称。
        /// 能按设备号查到主机当前分配的名字时用它，查不到再退回快照名。
        /// </summary>
        private string ResolveCurrentSourceDeviceName(string? deviceId, string? storedName) =>
            ResolveCurrentSourceDeviceName(_currentSourceDeviceNames, deviceId, storedName);

        internal static string ResolveCurrentSourceDeviceName(
            IReadOnlyDictionary<string, string>? names,
            string? deviceId,
            string? storedName) =>
            RecordingSourceNameLookup.Resolve(names, deviceId, storedName);

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
            BtnPreviousPage.IsEnabled = !_isLoadingVideos && _currentPage > 1;
            BtnNextPage.IsEnabled = !_isLoadingVideos && (_usingApproximatePaging ? _hasMoreVideoPages : pageCount > 0 && _currentPage < pageCount);
            UpdateExportOrderNumbersButtonState();
            UpdateLocateButtonState();
        }

        /// <summary>
        /// 当前筛选一条录像都没有时不给导出，否则点下去只会得到一张空表。
        /// 导出进行中由导出流程自己控制按钮，这里不插手。
        /// </summary>
        private void UpdateExportOrderNumbersButtonState()
        {
            if (_isExportingOrderNumbers)
                return;

            bool hasRecords = _totalVideos > 0 || _allVideos.Count > 0;
            ExportOrderNumbersButton.IsEnabled = _db != null && !_isClosing && hasRecords;
            ExportOrderNumbersButton.ToolTip = ExportOrderNumbersButton.IsEnabled
                ? "按当前筛选导出单号"
                : "当前筛选没有匹配的录像，没有可导出的单号";
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
            if (_usingApproximatePaging ? !_hasMoreVideoPages : _currentPage >= GetPageCount()) return;
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
            CancelAwaitingFirstFrame();

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
                    _mediaPlayer.Vout -= MediaPlayer_Vout;
                    _mediaPlayer.Paused -= MediaPlayer_Paused;

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
                SetTimelineMaximum(0);
                SetTimelineValue(0);
                TimeLabel.Text = "正在切换视频...";

                // 起播前先按这段录像的分辨率把窗口调好（解析文件头，不依赖播放器状态）
                await FitWindowToVideoBeforePlaybackAsync(video.FullPath);
                if (_isClosing)
                    return;

                // 2. 在后台线程执行阻塞的 Stop 操作
                await Task.Run(() =>
                {
                    _mediaPlayer?.Stop();
                });

                // 3. 准备新媒体
                using var media = new Media(_libVLC!, new Uri(video.FullPath));

                // 增加一些优化参数，减少内存压力
                media.AddOption(":file-caching=300"); // 减小缓存
                // 先以暂停状态打开媒体：等 libvlc 解出首帧、视频输出建好（见 PlaybackWindow.Playback.cs）
                // 再放开播放。否则时钟立刻开始走，画面还没上屏的前几帧就被丢掉了。
                media.AddOption(":start-paused");

                BeginAwaitingFirstFrame();
                if (!_mediaPlayer!.Play(media))
                    throw new InvalidOperationException("播放器未能启动该文件");

                // 放开播放与开始计时都交给"首帧就绪"那一步，这里不再提前把状态改成播放中。
            }
            catch (Exception ex)
            {
                CancelAwaitingFirstFrame();
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
            Dispatcher.Invoke(() => SetTimelineMaximum(e.Length / 1000.0));
        }

        private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
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
            Dispatcher.Invoke(() =>
            {
                _timer.Stop();
                UpdatePlayState(false);
                SetTimelineValue(0);
            });
        }

        private void MediaPlayer_EncounteredError(object? sender, EventArgs e)
        {
            CancelAwaitingFirstFrame();
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

        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
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
                TimelineSlider.Value = Math.Clamp(value, TimelineSlider.Minimum, TimelineSlider.Maximum);
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
            LocateFileText.Visibility = textVisibility;
            PlayStateIcon.Margin = iconOnly ? new Thickness(0) : new Thickness(0, 0, 6, 0);
            LocateFileIcon.Margin = iconOnly ? new Thickness(0) : new Thickness(0, 0, 6, 0);
            TimeLabel.MinWidth = iconOnly ? 0 : 110;
        }

        private void UpdateLocateButtonState(VideoItem? video = null)
        {
            var current = video ?? VideoList.SelectedItem as VideoItem;
            BtnLocateFile.IsEnabled = current != null && !current.IsUnavailable;
        }

        /// <summary>
        /// 播放器只初始化一次；开窗时的提前初始化和用户第一次点播放共享同一个任务，
        /// 否则"初始化进行中"的那次点击会被当成失败直接返回（点了没反应）。
        /// </summary>
        private Task<bool> EnsurePlayerReadyAsync() =>
            _playerReadyTask ??= InitializePlayerAsync();

        private async Task<bool> InitializePlayerAsync()
        {
            if (_playerInitializationFailed)
                return false;

            if (_mediaPlayer != null)
                return true;

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
                    // 开窗时解析分辨率那一步已经建过实例（进程级共享），这里直接复用。
                    libVLC = _libVLC ?? new LibVLC("--avcodec-hw=any");
                    mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(libVLC);
                });

                _libVLC = libVLC;
                _mediaPlayer = mediaPlayer;
                _mediaPlayer.LengthChanged += MediaPlayer_LengthChanged;
                _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
                _mediaPlayer.EndReached += MediaPlayer_EndReached;
                _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
                // 首帧/视频输出就绪：这一对事件决定什么时候放开播放（见 PlaybackWindow.Playback.cs）
                _mediaPlayer.Vout += MediaPlayer_Vout;
                _mediaPlayer.Paused += MediaPlayer_Paused;
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
        }

        private void SetLoadingState(bool loading, string statusText)
        {
            BtnPreviousPage.IsEnabled = !loading && _currentPage > 1;
            BtnNextPage.IsEnabled = !loading && (_usingApproximatePaging
                ? _hasMoreVideoPages
                : _currentPage < GetPageCount());
            TimeLabel.Text = statusText;
        }

        internal static bool IsCurrentLoadRequest(int requestVersion, int currentRequestVersion, bool isClosing) =>
            !isClosing && requestVersion == currentRequestVersion;

        private readonly record struct VideoLoadRequest(
            DateTime? Start,
            DateTime? End,
            string? Keyword,
            int Page,
            string Mode,
            string SourceType,
            string SourceId,
            string SourceName,
            IReadOnlyList<string>? SourceIds = null);
    }
}
