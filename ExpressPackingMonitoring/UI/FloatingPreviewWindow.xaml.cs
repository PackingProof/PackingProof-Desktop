using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 悬浮置顶小窗：店员在快递助手等其它软件里操作时，持续显示画面、录制状态和单号。
    /// 只读取 <see cref="MainViewModel"/> 的现有状态，不介入录像与摄像头生命周期。
    /// </summary>
    public partial class FloatingPreviewWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        /// <summary>录制中的流光节奏与主界面录制卡片保持一致，避免两处观感割裂。</summary>
        private static readonly Duration ShimmerDuration = new(TimeSpan.FromSeconds(2));

        /// <summary>预录制呼吸要比录制流光更快更明显，店员扫一眼就知道缓冲在滚。</summary>
        private static readonly Duration BreathDuration = new(TimeSpan.FromSeconds(0.6));

        private readonly MainViewModel _viewModel;
        private readonly Action _restoreMainWindow;
        private Storyboard? _breathStoryboard;
        private Storyboard? _shimmerStoryboard;

        // 音频端点枚举要走 COM，缓存住上一次结果，点开菜单时先显示再后台刷新。
        private IReadOnlyList<AudioEndpointInfo>? _microphoneCache;
        private IReadOnlyList<AudioEndpointInfo>? _playbackCache;
        private FloatingPreviewCorner _cornerPreference = FloatingPreviewCorner.BottomRight;
        private double _appliedFrameAspect;
        private DispatcherTimer? _noticeTimer;
        private bool _closedByOwner;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        /// <param name="restoreMainWindow">恢复主窗口的回调，只有左上角按钮与双击会走它。</param>
        public FloatingPreviewWindow(MainViewModel viewModel, Action restoreMainWindow)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _restoreMainWindow = restoreMainWindow ?? throw new ArgumentNullException(nameof(restoreMainWindow));

            InitializeComponent();
            DataContext = viewModel;

            RestoreButton.ToolTip = AppLanguage.Get("回到主界面");
            CloseButton.ToolTip = AppLanguage.Get("关闭小窗");
            MicrophoneButton.ToolTip = AppLanguage.Get("选择麦克风");
            SpeakerButton.ToolTip = AppLanguage.Get("选择播放设备");

            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // 置顶窗口必须不抢焦点，否则店员在快递助手里打字会被打断。
            IntPtr handle = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

            PlaceInsideWorkArea();
            ApplyStatus();
            PrewarmEndpointCaches();
        }

        /// <summary>
        /// 小窗一出现就在后台把设备列表枚举好，
        /// 这样第一次点麦克风或喇叭也能立刻弹出菜单，而不是卡在 COM 枚举上。
        /// </summary>
        private void PrewarmEndpointCaches()
        {
            Task.Run(() =>
            {
                IReadOnlyList<AudioEndpointInfo> microphones = _viewModel.ListMicrophoneEndpoints();
                IReadOnlyList<AudioEndpointInfo> speakers = _viewModel.ListPlaybackEndpoints();
                Dispatcher.BeginInvoke(() =>
                {
                    _microphoneCache = microphones;
                    _playbackCache = speakers;
                });
            });
        }

        /// <summary>按上次关闭时判定的角落贴回去，而不是永远固定在右下角。</summary>
        private void PlaceInsideWorkArea()
        {
            _cornerPreference = FloatingPreviewPlacement.Parse(_viewModel.FloatingPreviewCorner);
            ApplyCornerPlacement(_cornerPreference);
        }

        private void ApplyCornerPlacement(FloatingPreviewCorner corner)
        {
            Point position = FloatingPreviewPlacement.ResolvePosition(
                corner,
                new Size(Width, Height),
                SystemParameters.WorkArea);

            Left = position.X;
            Top = position.Y;
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.FloatingPreviewIndicator))
                Dispatcher.BeginInvoke(ApplyStatus);
            else if (e.PropertyName == nameof(MainViewModel.VideoFrame))
                Dispatcher.BeginInvoke(SyncWindowToFrameAspectRatio);
        }

        /// <summary>
        /// 让窗口宽高跟着实际相机画面的比例走，消除上下或左右黑边。
        /// 相机分辨率要等第一帧才知道，而且中途换相机或改分辨率都会变，所以按帧比例校正。
        /// 只在比例确实变了时调整，避免每帧都动窗口。
        /// </summary>
        private void SyncWindowToFrameAspectRatio()
        {
            if (_viewModel.VideoFrame is not { PixelWidth: > 0, PixelHeight: > 0 } frame)
                return;

            double frameAspect = (double)frame.PixelWidth / frame.PixelHeight;
            if (Math.Abs(frameAspect - _appliedFrameAspect) < 0.001) return;
            _appliedFrameAspect = frameAspect;

            double statusHeight = StatusBar.ActualHeight > 0 ? StatusBar.ActualHeight : StatusBar.MinHeight;
            double chrome = RootBorder.BorderThickness.Top + RootBorder.BorderThickness.Bottom;

            // 保持当前宽度，只调整高度，避免窗口在屏幕上突然变宽。
            double previewWidth = Math.Max(1, Width - RootBorder.BorderThickness.Left - RootBorder.BorderThickness.Right);
            double targetHeight = previewWidth / frameAspect + statusHeight + chrome;

            Rect workArea = SystemParameters.WorkArea;
            targetHeight = Math.Min(targetHeight, workArea.Height);
            if (Math.Abs(targetHeight - Height) < 1) return;

            Height = targetHeight;
            // 改高度后可能顶出工作区，重新贴回预设角落。
            ApplyCornerPlacement(_cornerPreference);
        }

        /// <summary>
        /// 三种状态灯：录制红色常亮并走流光，预录制蓝到灰呼吸，待机灰色常亮。
        /// 颜色全部取主题资源，跟随明暗主题，不写死十六进制。
        /// </summary>
        private void ApplyStatus()
        {
            StopAnimations();

            switch (_viewModel.FloatingPreviewIndicator)
            {
                case FloatingPreviewIndicator.Recording:
                    StatusDot.Fill = ResolveBrush("AccentRed");
                    StartShimmer();
                    break;

                case FloatingPreviewIndicator.PreRecording:
                    StartBreath();
                    break;

                default:
                    StatusDot.Fill = ResolveBrush("TextMuted");
                    break;
            }
        }

        /// <summary>
        /// 蓝到灰呼吸，提示缓冲正在滚动但尚未正式录制。
        /// 直接动画填充色而不是整体透明度，灰端才不会退化成"看不见"。
        /// </summary>
        private void StartBreath()
        {
            Color blue = ResolveColor("AccentBlue", Color.FromRgb(0x3B, 0x82, 0xF6));
            Color grey = ResolveColor("TextMuted", Color.FromRgb(0x8A, 0x8A, 0x94));

            // 动画要改写 Fill，先换成独立可变画刷，避免动到主题里的共享冻结画刷。
            StatusDot.Fill = new SolidColorBrush(blue);

            var animation = new ColorAnimation
            {
                From = blue,
                To = grey,
                Duration = BreathDuration,
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };

            _breathStoryboard = new Storyboard();
            _breathStoryboard.Children.Add(animation);
            Storyboard.SetTarget(animation, StatusDot);
            Storyboard.SetTargetProperty(
                animation,
                new PropertyPath("(Shape.Fill).(SolidColorBrush.Color)"));
            _breathStoryboard.Begin();
        }

        /// <summary>
        /// 复用主界面录制卡片的流光做法：平移渐变画刷的 RelativeTransform 而不是平移控件，
        /// 渐变尾巴才能平滑进出而不是在中间被拉伸。
        /// 流光铺在整条状态区上（连同单号一起扫过），不是单独画一根进度条。
        /// </summary>
        private void StartShimmer()
        {
            ShimmerLayer.Visibility = Visibility.Visible;

            var animation = new DoubleAnimation
            {
                From = -1.5,
                To = 1.5,
                Duration = ShimmerDuration,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };

            _shimmerStoryboard = new Storyboard();
            _shimmerStoryboard.Children.Add(animation);
            Storyboard.SetTarget(animation, ShimmerLayer);
            Storyboard.SetTargetProperty(
                animation,
                new PropertyPath("(Shape.Fill).(Brush.RelativeTransform).(TranslateTransform.X)"));
            _shimmerStoryboard.Begin();
        }

        private void StopAnimations()
        {
            _breathStoryboard?.Stop();
            _breathStoryboard = null;
            _shimmerStoryboard?.Stop();
            _shimmerStoryboard = null;
            ShimmerLayer.Visibility = Visibility.Collapsed;
        }

        /// <summary>主题资源缺失时退回待机灰，保证状态灯始终可见。</summary>
        private Brush ResolveBrush(string resourceKey) =>
            TryFindResource(resourceKey) as Brush
            ?? TryFindResource("TextMuted") as Brush
            ?? new SolidColorBrush(ResolveColor("TextMuted", Color.FromRgb(0x8A, 0x8A, 0x94)));

        private Color ResolveColor(string brushResourceKey, Color fallback) =>
            TryFindResource(brushResourceKey) is SolidColorBrush brush ? brush.Color : fallback;

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            ControlLayer.Visibility = Visibility.Visible;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            ControlLayer.Visibility = Visibility.Collapsed;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            // 双击回主窗口，与会议软件小窗一致；单击拖动。
            if (e.ClickCount == 2)
            {
                RestoreMainWindowAndClose();
                return;
            }

            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); }
                catch (InvalidOperationException) { /* 拖动期间窗口已关闭 */ }
            }
        }

        /// <summary>左上角：回到主界面。</summary>
        private void RestoreButton_Click(object sender, RoutedEventArgs e) => RestoreMainWindowAndClose();

        /// <summary>右上角：只关小窗，主界面保持原状，不弹回来打断店员。</summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseFromOwner();

        private void MicrophoneButton_Click(object sender, RoutedEventArgs e) =>
            ShowEndpointMenu(
                MicrophoneButton,
                _viewModel.ListMicrophoneEndpoints,
                _viewModel.CurrentMicrophoneEndpointId,
                _viewModel.CurrentMicrophoneEndpointName,
                _viewModel.TrySelectMicrophoneEndpoint,
                isMicrophone: true,
                "未检测到麦克风");

        private void SpeakerButton_Click(object sender, RoutedEventArgs e) =>
            ShowEndpointMenu(
                SpeakerButton,
                _viewModel.ListPlaybackEndpoints,
                _viewModel.CurrentPlaybackEndpointId,
                _viewModel.CurrentPlaybackEndpointName,
                _viewModel.TrySelectPlaybackEndpoint,
                isMicrophone: false,
                "未检测到播放设备");

        /// <summary>
        /// 小窗空间很小，设备选择用上下文菜单而不是常驻下拉框。
        /// 枚举音频端点要走 COM，点开时同步枚举会明显卡顿，所以先用缓存立刻弹出菜单，
        /// 再在后台线程刷新；设备有变化时就地更新菜单项。
        /// </summary>
        private void ShowEndpointMenu(
            FrameworkElement anchor,
            Func<IReadOnlyList<AudioEndpointInfo>> enumerate,
            string currentId,
            string currentName,
            Func<AudioEndpointInfo, bool> select,
            bool isMicrophone,
            string emptyText)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = anchor,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
                Style = (Style)FindResource("AppContextMenuStyle")
            };

            IReadOnlyList<AudioEndpointInfo> initial =
                (isMicrophone ? _microphoneCache : _playbackCache) ?? Array.Empty<AudioEndpointInfo>();
            FillEndpointMenu(menu, initial, currentId, currentName, select, isMicrophone, emptyText);

            ControlLayer.Visibility = Visibility.Visible;
            menu.Closed += (_, _) =>
            {
                if (!IsMouseOver)
                    ControlLayer.Visibility = Visibility.Collapsed;
            };
            menu.IsOpen = true;

            // 后台刷新真实设备列表，避免点开瞬间卡在 COM 枚举上。
            Task.Run(enumerate).ContinueWith(
                task =>
                {
                    if (task.IsFaulted || task.Result == null) return;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (isMicrophone)
                            _microphoneCache = task.Result;
                        else
                            _playbackCache = task.Result;

                        if (menu.IsOpen)
                            FillEndpointMenu(menu, task.Result, currentId, currentName, select, isMicrophone, emptyText);
                    });
                },
                TaskScheduler.Default);
        }

        private void FillEndpointMenu(
            ContextMenu menu,
            IReadOnlyList<AudioEndpointInfo> endpoints,
            string currentId,
            string currentName,
            Func<AudioEndpointInfo, bool> select,
            bool isMicrophone,
            string emptyText)
        {
            menu.Items.Clear();

            if (endpoints.Count == 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = AppLanguage.Get(emptyText),
                    IsEnabled = false,
                    Style = (Style)FindResource("AppMenuItemStyle")
                });
                return;
            }

            bool hasExplicitChoice = !string.IsNullOrWhiteSpace(currentId);
            foreach (AudioEndpointInfo endpoint in endpoints)
            {
                AudioEndpointInfo captured = endpoint;
                var item = new MenuItem
                {
                    Header = endpoint.Name,
                    IsCheckable = true,
                    Style = (Style)FindResource("AppMenuItemStyle"),
                    // 旧配置只存了名称没存 Id 时按名称回落匹配，避免菜单里一个勾都没有。
                    IsChecked = hasExplicitChoice
                        ? string.Equals(endpoint.Id, currentId, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(endpoint.Name, currentName, StringComparison.OrdinalIgnoreCase)
                };
                item.Click += (_, _) => SelectEndpoint(captured, select, isMicrophone);
                menu.Items.Add(item);
            }
        }

        /// <summary>
        /// 切换结果必须就地反馈：主窗口已最小化，Toast 看不见。
        /// 麦克风额外说明"下一单生效"，因为正在录的这一单不会被打断。
        /// </summary>
        private void SelectEndpoint(
            AudioEndpointInfo endpoint,
            Func<AudioEndpointInfo, bool> select,
            bool isMicrophone)
        {
            try
            {
                if (!select(endpoint))
                {
                    ShowInlineNotice(AppLanguage.Get("设备切换保存失败，本次未生效"));
                    RuntimeLog.Warn("FloatingPreview", $"音频设备切换未保存：{endpoint.Name}");
                    return;
                }

                if (!isMicrophone)
                {
                    ShowInlineNotice(AppLanguage.Format("播放设备已切换为 {0}", endpoint.Name));
                    return;
                }

                ShowInlineNotice(_viewModel.IsRecording
                    ? AppLanguage.Format("麦克风已切换为 {0}，下一单录制生效", endpoint.Name)
                    : AppLanguage.Format("麦克风已切换为 {0}", endpoint.Name));
            }
            catch (Exception ex)
            {
                ShowInlineNotice(AppLanguage.Get("设备切换失败"));
                RuntimeLog.Error("FloatingPreview", $"音频设备切换失败：{ex.Message}");
            }
        }

        private void RestoreMainWindowAndClose()
        {
            try { _restoreMainWindow(); }
            catch (Exception ex) { RuntimeLog.Error("FloatingPreview", $"恢复主窗口失败：{ex.Message}"); }

            CloseFromOwner();
        }

        /// <summary>由主窗口或退出流程调用，确保不会重复触发恢复逻辑。</summary>
        public void CloseFromOwner()
        {
            if (_closedByOwner) return;
            _closedByOwner = true;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            PersistCornerPreference();
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            StopAnimations();
            if (_noticeTimer != null)
            {
                _noticeTimer.Stop();
                _noticeTimer = null;
            }
            _viewModel.IsFloatingPreviewActive = false;
            base.OnClosed(e);
        }

        /// <summary>
        /// 关闭时按窗口中心判断更靠近哪个角落并记下来，下次直接贴到那个预设位置。
        /// 只记角落不记坐标，换分辨率或换显示器也不会把小窗放到屏幕外。
        /// </summary>
        private void PersistCornerPreference()
        {
            try
            {
                FloatingPreviewCorner corner = FloatingPreviewPlacement.ResolveCorner(
                    new Rect(Left, Top, Width, Height),
                    SystemParameters.WorkArea);
                _viewModel.SaveFloatingPreviewCorner(corner);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("FloatingPreview", $"记录小窗停靠角落失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 就地提示：主窗口处于最小化状态，Toast 在主界面上弹出来店员根本看不见，
        /// 所以设备切换这类反馈必须显示在小窗自己身上。
        /// </summary>
        private void ShowInlineNotice(string message)
        {
            InlineNoticeText.Text = message;
            InlineNoticeBorder.Visibility = Visibility.Visible;

            _noticeTimer ??= new DispatcherTimer();
            _noticeTimer.Stop();
            _noticeTimer.Interval = TimeSpan.FromSeconds(3.5);
            _noticeTimer.Tick -= NoticeTimer_Tick;
            _noticeTimer.Tick += NoticeTimer_Tick;
            _noticeTimer.Start();
        }

        private void NoticeTimer_Tick(object? sender, EventArgs e)
        {
            _noticeTimer?.Stop();
            InlineNoticeBorder.Visibility = Visibility.Collapsed;
        }
    }
}
