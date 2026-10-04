using ExpressPackingMonitoring.Input;
using ExpressPackingMonitoring.Logging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.ViewModels;
using ExpressPackingMonitoring.Services;
using System.IO;
using System.Windows.Media.Imaging;

namespace ExpressPackingMonitoring.UI
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        private const int VK_CAPITAL = 0x14;
        private DispatcherTimer _capsCheckTimer;
        private bool _capsLockStateBeforeFocus;
        private bool _capsLockOverridden;
        private bool _capsLockSuspended;
        private FloatingPreviewController? _floatingPreviewController;
        private DateTime _lastMouseActivityNotifyAt = DateTime.MinValue;
        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private bool _shutdownConfirmed;
        private bool _shutdownInProgress;
        private bool _resourceCleanupInProgress;
        private bool _exitRequestedFromTray;
        private readonly WindowCloseBehaviorController _closeBehaviorController;
        private readonly DispatcherTimer _scanAutoSubmitTimer;
        private readonly List<double> _scanInputIntervalsMs = new();
        private DateTime _lastScanInputCharAt = DateTime.MinValue;
        private int _lastScanInputLength;
        private bool _testOrderSending;
        private const double TopModeButtonTextWidth = 130;
        private const double TopRecordButtonTextWidth = 160;
        private const double TopModeButtonRightMargin = 16;
        private const double TopColumnGap = 20;
        private const double MinimumScanInputWidth = 320;
        private const double IconButtonWidth = 52;

        public static readonly DependencyProperty IsModeButtonCompactProperty = DependencyProperty.Register(
            nameof(IsModeButtonCompact),
            typeof(bool),
            typeof(MainWindow),
            new PropertyMetadata(false));

        public bool IsModeButtonCompact
        {
            get => (bool)GetValue(IsModeButtonCompactProperty);
            set => SetValue(IsModeButtonCompactProperty, value);
        }

        public static readonly DependencyProperty IsRecordButtonCompactProperty = DependencyProperty.Register(
            nameof(IsRecordButtonCompact),
            typeof(bool),
            typeof(MainWindow),
            new PropertyMetadata(false));

        public bool IsRecordButtonCompact
        {
            get => (bool)GetValue(IsRecordButtonCompactProperty);
            set => SetValue(IsRecordButtonCompactProperty, value);
        }

        private bool IsCapsLockOn() => (GetKeyState(VK_CAPITAL) & 1) != 0;

        private void ToggleCapsLock()
        {
            keybd_event((byte)VK_CAPITAL, 0x45, 0, UIntPtr.Zero);
            keybd_event((byte)VK_CAPITAL, 0x45, 2, UIntPtr.Zero);
        }

        private void EnsureCapsLockOn()
        {
            if (!IsCapsLockOn())
            {
                ToggleCapsLock();
                _capsLockOverridden = true;
            }
        }

        private void RestoreCapsLockState()
        {
            if (_capsLockOverridden && !_capsLockStateBeforeFocus && IsCapsLockOn())
            {
                ToggleCapsLock();
            }
            _capsLockOverridden = false;
        }

        private bool ShouldForceCapsLock()
        {
            return !_capsLockSuspended &&
                   IsActive &&
                   WindowState != WindowState.Minimized &&
                   ScanInputTextBox?.IsFocused == true;
        }

        private void ApplyCapsLockForScanInput()
        {
            if (!ShouldForceCapsLock())
            {
                _capsCheckTimer.Stop();
                return;
            }

            if (!_capsLockOverridden)
            {
                _capsLockStateBeforeFocus = IsCapsLockOn();
            }

            EnsureCapsLockOn();
            if (string.IsNullOrEmpty(ScanInputTextBox.Text))
                _capsCheckTimer.Start();
        }

        public void SuspendCapsLockForModalWindow()
        {
            _capsLockSuspended = true;
            _capsCheckTimer.Stop();
            RestoreCapsLockState();
        }

        public void ResumeCapsLockAfterModalWindow()
        {
            _capsLockSuspended = false;
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                if (IsActive && WindowState != WindowState.Minimized)
                {
                    ScanInputTextBox.Focus();
                    ApplyCapsLockForScanInput();
                }
            }));
        }

        public MainWindow(bool enableCloseBehaviorPrompt = true)
        {
            InitializeComponent();
            StatsBarBorder.SizeChanged += (_, _) => UpdateStatsBarVisibility();
            TopBarBorder.SizeChanged += (_, _) => UpdateTopBarCompactState();
            Loaded += (_, _) =>
            {
                UpdateStatsBarVisibility();
                UpdateTopBarCompactState();
            };
            DpiChanged += (_, _) =>
                (DataContext as MainViewModel)?.RefreshBarcodesForDpiChange();
            if (DataContext is MainViewModel statsViewModel)
            {
                statsViewModel.PropertyChanged += OnStatsViewModelPropertyChanged;
            }
            _closeBehaviorController = new WindowCloseBehaviorController(
                this,
                RequestExitFromTray,
                enableCloseBehaviorPrompt,
                (isInTray, sessionOverride) =>
                    (DataContext as MainViewModel)?.SetMainWindowInTray(isInTray, sessionOverride));
            if (CameraBarcodeRuntimeOptions.ShadowMode)
            {
                RuntimeLog.Warn(
                    "CameraBarcodeCompare",
                    "摄像头对照调试模式已启用：摄像头仅记录判定，不会触发录制；扫码枪保持真实执行");
            }
            BtnMobileConnection.Click += BtnMobileConnection_Click;
            BtnMobileConnection.PreviewMouseLeftButtonUp += BtnMobileConnection_PreviewMouseLeftButtonUp;
            BtnSwitchWorkstation.Click += BtnSwitchWorkstation_Click;
            BtnSwitchWorkstation.PreviewMouseLeftButtonUp += BtnSwitchWorkstation_PreviewMouseLeftButtonUp;
            BtnInstallUserscript.Click += BtnInstallUserscript_Click;
            BtnSendTestOrder.Click += BtnSendTestOrder_Click;
            _capsCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _capsCheckTimer.Tick += (s, e) =>
            {
                if (string.IsNullOrEmpty(ScanInputTextBox.Text))
                    ApplyCapsLockForScanInput();
                else
                    _capsCheckTimer.Stop();
            };
            _scanAutoSubmitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _scanAutoSubmitTimer.Tick += ScanAutoSubmitTimer_Tick;
            Activated += (s, e) =>
            {
                _capsLockStateBeforeFocus = IsCapsLockOn();
                _capsLockOverridden = false;
                ApplyCapsLockForScanInput();
                (DataContext as MainViewModel)?.NotifyUserActivity();
            };
            Deactivated += (s, e) =>
            {
                _capsCheckTimer.Stop();
                RestoreCapsLockState();
            };
            StateChanged += (s, e) =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    _capsCheckTimer.Stop();
                    RestoreCapsLockState();
                    // 最小化后画面交给小窗，预览按小窗尺寸发布。
                    (DataContext as MainViewModel)?.ReportMainPreviewDisplayWidth(0);
                    (DataContext as MainViewModel)?.ReportMainPreviewVisibility(false);
                }
                else
                {
                    ApplyCapsLockForScanInput();
                    ReportPreviewDisplayWidth();
                    (DataContext as MainViewModel)?.ReportMainPreviewVisibility(true);
                }
            };
            // 显示/隐藏（例如最小化到托盘）也要同步：只靠 StateChanged 会漏掉 hide/show
            IsVisibleChanged += (s, e) =>
                (DataContext as MainViewModel)?.ReportMainPreviewVisibility(
                    IsVisible && WindowState != WindowState.Minimized);
            // 小窗只由最小化触发，主界面不新增按钮，所以控制器必须在这里提前挂好。
            if (DataContext is MainViewModel floatingPreviewViewModel)
                _floatingPreviewController = new FloatingPreviewController(this, floatingPreviewViewModel);
            // 全局鼠标/键盘活跃检测，用于摄像头空闲休眠唤醒
            PreviewMouseMove += (s, e) =>
            {
                var now = DateTime.UtcNow;
                if (now - _lastMouseActivityNotifyAt < TimeSpan.FromSeconds(1)) return;
                _lastMouseActivityNotifyAt = now;
                (DataContext as MainViewModel)?.NotifyUserActivity();
            };
            PreviewKeyDown += (s, e) =>
            {
                (DataContext as MainViewModel)?.NotifyUserActivity();

                // 预览编辑态（副摄取景 / 放大取景框）必须有一条可靠的退出路径：Esc 与"完成"等效。
                if (e.Key == Key.Escape
                    && DataContext is MainViewModel vm
                    && (vm.IsEditingOverlayPreview || vm.IsEditingZoomGuide))
                {
                    ExitPreviewGuideEditing(vm);
                    e.Handled = true;
                }
            };
            Loaded += (s, e) => {
                ScanInputTextBox.Focus();
                if (DataContext is MainViewModel vm)
                {
                    vm.PropertyChanged += (sender, args) =>
                    {
                        if (args.PropertyName == nameof(MainViewModel.CameraFrameSize) ||
                            args.PropertyName == nameof(MainViewModel.IsCameraBarcodeRecognitionEnabled))
                        {
                            Dispatcher.BeginInvoke(new Action(() => UpdateCameraOverlays(vm)));
                        }
                        else if (args.PropertyName == nameof(MainViewModel.IsOverlayVisible)
                            || args.PropertyName == nameof(MainViewModel.HasOverlayFrame))
                        {
                            // 开关副画面、或副路刚出第一帧时，拖动框要立刻摆好。
                            // 识别框贴到画中画上时，框和小锁提示也要跟着挪。
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                UpdateOverlayBoxes(vm);
                                UpdateCameraBarcodeGuide(vm);
                            }));
                        }
                        else if (args.PropertyName == nameof(MainViewModel.OverlayPlacementVersion))
                        {
                            // 副画面在帧里的落位变了（改裁剪、进出取景编辑、拖动大小/位置）：
                            // 拖动框必须跟着重摆；识别框贴在画中画上时也要一起重摆，否则反馈框会错位。
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                UpdateOverlayBoxes(vm);
                                UpdateCameraBarcodeGuide(vm);
                            }));
                        }
                        else if (args.PropertyName == nameof(MainViewModel.PreviewImageSource))
                        {
                            // 预览图源变了（进出副摄取景编辑、或副摄那一屏的第一帧到达）：
                            // 识别框必须按当前这张图重新摆一次，否则会停留在上一张图的坐标上。
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                UpdateCameraBarcodeGuide(vm);
                                UpdateOverlayBoxes(vm);
                            }));
                        }
                    };
                    // 窗口/视频区域大小变化时重新计算边框位置。
                    //
                    // 只盯 VideoImage 不够：画面按 Uniform 摆放，在有黑边的那一侧拖动窗口时
                    // 画面本身尺寸不变（黑边变宽而已），SizeChanged 不会触发，框就会停在旧位置。
                    // 所以预览容器的大小变化也要盯。
                    VideoImage.SizeChanged += (_, __) =>
                    {
                        UpdateCameraOverlays(vm);
                        ReportPreviewDisplayWidth();
                    };
                    if (VideoImage.Parent is FrameworkElement previewHost)
                    {
                        previewHost.SizeChanged += (_, __) => UpdateCameraOverlays(vm);
                    }
                    ReportPreviewDisplayWidth();
                }

                Title = AppLanguage.Format("Main.Title", AppVersion.Current);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    (DataContext as MainViewModel)?.RunStartupSetupFlowsIfNeeded(this);
                }), DispatcherPriority.ContextIdle);
            };
            SourceInitialized += (_, __) =>
            {
                if (PresentationSource.FromVisual(this) is HwndSource source)
                    source.AddHook(WndProc);
            };
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (DataContext is MainViewModel vm)
            {
                if (msg == WM_ENTERSIZEMOVE)
                {
                    vm.SuppressVideoPreviewUpdates = true;
                }
                else if (msg == WM_EXITSIZEMOVE)
                {
                    vm.ResumeVideoPreviewUpdatesAfterWindowMove();
                    UpdateCameraOverlays(vm);
                }
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// 把预览控件的实际显示宽度（按 DPI 换算成设备像素）报给 ViewModel：
        /// 预览按这个尺寸发布，1080p 整帧搬到 UI 再缩放的开销就省掉了。
        /// </summary>
        private void ReportPreviewDisplayWidth()
        {
            if (DataContext is not MainViewModel vm)
                return;

            if (WindowState == WindowState.Minimized || !IsVisible)
            {
                vm.ReportMainPreviewDisplayWidth(0);
                vm.ReportMainPreviewVisibility(false);
                return;
            }

            double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            vm.ReportMainPreviewDisplayWidth(VideoImage.ActualWidth * dpiScale);
            vm.ReportMainPreviewVisibility(true);
        }

        /// <summary>预览上跟画中画有关的两层：每一路的拖动框、以及识别框。</summary>
        private void UpdateCameraOverlays(MainViewModel vm)
        {
            UpdateOverlayBoxes(vm);
            UpdateCameraBarcodeGuide(vm);
        }

        /// <summary>退出预览编辑（副摄取景或放大取景框），回到正常预览。</summary>
        private void BtnOverlayPreviewDone_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            ExitPreviewGuideEditing(vm);
        }

        /// <summary>
        /// 退出当前预览编辑态并重摆两层框。两种编辑态共用同一个"完成"按钮与 Esc，
        /// 这里按当前态分派，避免调用方各自记状态。
        /// </summary>
        private void ExitPreviewGuideEditing(MainViewModel vm)
        {
            if (vm.IsEditingZoomGuide)
                vm.ExitZoomGuideEdit();
            else
                vm.ExitOverlayPreviewEdit();
            UpdateOverlayBoxes(vm);
            UpdateCameraBarcodeGuide(vm);
        }




        /// <summary>
        /// 只读放大取景框：平时标出"录制触发时哪一块会被放大铺满"。
        /// 与识别框同一套换算（画面按 Uniform 居中摆放，两个坐标系原点并不重合），
        /// 进编辑态时这一层让位给可拖动的取景框，避免两个框叠在一起。
        /// </summary>
        private void UpdateZoomGuideBox(MainViewModel vm)
        {
            ZoomGuideBoxHost.Visibility = Visibility.Collapsed;
            ZoomGuideBoxHost.RenderTransform = null;

            bool visible = vm.Config is not { ShowZoomGuideBox: false } && !vm.IsEditingZoomGuide;
            double sourceW = vm.CameraFrameSize.Width;
            double sourceH = vm.CameraFrameSize.Height;
            double actualW = VideoImage.ActualWidth;
            double actualH = VideoImage.ActualHeight;
            if (!visible || sourceW <= 0 || sourceH <= 0 || actualW <= 0 || actualH <= 0)
            {
                ZoomGuideBoxHost.Width = 0;
                ZoomGuideBoxHost.Height = 0;
                return;
            }

            Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(sourceW, sourceH, actualW, actualH);
            Rect boxRect = CameraBarcodeGuideLayout.ToDisplayRect(vm.ZoomGuideGeometry, videoRect);
            if (boxRect.IsEmpty || boxRect.Width <= 0 || boxRect.Height <= 0)
            {
                ZoomGuideBoxHost.Width = 0;
                ZoomGuideBoxHost.Height = 0;
                return;
            }

            ZoomGuideBoxHost.Width = boxRect.Width;
            ZoomGuideBoxHost.Height = boxRect.Height;
            ZoomGuideBoxHost.Visibility = Visibility.Visible;
            if (ZoomGuideBoxHost.Parent is not FrameworkElement zoomGuideHost)
                return;

            Point videoOriginInHost = VideoImage.TranslatePoint(new Point(0, 0), zoomGuideHost);
            ZoomGuideBoxHost.RenderTransform = new TranslateTransform(
                videoOriginInHost.X + boxRect.X - ((zoomGuideHost.ActualWidth - boxRect.Width) / 2.0),
                videoOriginInHost.Y + boxRect.Y - ((zoomGuideHost.ActualHeight - boxRect.Height) / 2.0));
        }

        private void UpdateCameraBarcodeGuide(MainViewModel vm)
        {
            UpdateZoomGuideBox(vm);

            double sourceW = vm.CameraFrameSize.Width;
            double sourceH = vm.CameraFrameSize.Height;
            double actualW = VideoImage.ActualWidth;
            double actualH = VideoImage.ActualHeight;
            if (sourceW <= 0 || sourceH <= 0 || actualW <= 0 || actualH <= 0)
            {
                CameraBarcodeGuide.Width = 0;
                CameraBarcodeGuide.Height = 0;
                CameraBarcodeGuide.RenderTransform = null;
                return;
            }

            // 这里不做任何"主摄还是副摄"的判断：编辑副摄时 ViewModel 已经把
            // 当前几何与当前画面尺寸切换成副摄的，摆框逻辑与主摄完全同一条路径。
            CameraBarcodeGuideGeometry geometry = vm.CurrentCameraBarcodeGuideGeometry;
            Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(sourceW, sourceH, actualW, actualH);

            // 识别输入来自副摄（且不在取景编辑屏）：框贴到画中画上。
            // 画中画显示的就是"框内那块裁剪"，所以框等于画中画本身；
            // 绿/黄识别状态和提示文字都落在这块上，识别反馈不会丢。
            if (!vm.IsEditingOverlayPreview
                && vm.ShouldUseOverlayChannelForBarcode
                && vm.VideoFrame is { PixelWidth: > 0, PixelHeight: > 0 } overlayFrame
                && vm.BarcodeOverlayChannelNumber > 0
                && vm.TryResolveOverlayRect(vm.BarcodeOverlayChannelNumber,
                    overlayFrame.PixelWidth,
                    overlayFrame.PixelHeight,
                    out CameraOverlayRect overlay))
            {
                double overlayScale = videoRect.Width / overlayFrame.PixelWidth;
                videoRect = new Rect(
                    videoRect.X + (overlay.X * overlayScale),
                    videoRect.Y + (overlay.Y * overlayScale),
                    overlay.Width * overlayScale,
                    overlay.Height * overlayScale);
                geometry = new CameraBarcodeGuideGeometry(1.0, 1.0, 0, 0);
                // 框贴到画中画上时，框线圆角也跟着画中画走，两个圆角才对得上。
                int overlayRadius = Math.Max(
                    1,
                    (int)Math.Round(CameraOverlayComposer.ResolveCornerRadius(overlay.Width, overlay.Height) * overlayScale));
                CameraBarcodeGuideBox.RadiusX = overlayRadius;
                CameraBarcodeGuideBox.RadiusY = overlayRadius;
            }
            else
            {
                // 画在主画面上的识别框保持自己一贯的圆角
                CameraBarcodeGuideBox.RadiusX = 6;
                CameraBarcodeGuideBox.RadiusY = 6;
            }

            Rect guideRect = CameraBarcodeGuideLayout.ToDisplayRect(geometry, videoRect);
            if (guideRect.IsEmpty)
            {
                CameraBarcodeGuide.Width = 0;
                CameraBarcodeGuide.Height = 0;
                CameraBarcodeGuide.RenderTransform = null;
                return;
            }

            CameraBarcodeGuide.Width = guideRect.Width;
            CameraBarcodeGuide.Height = guideRect.Height;
            // 取景框的位置是在"画面"（VideoImage）坐标系里算出来的，而框是挂在预览容器上的：
            // 画面按 Uniform 居中时两个原点并不重合（上下会差出一段留白），必须先换算过去，
            // 否则框会整体偏移。容器的实际尺寸也不能拿画面的尺寸代替。
            if (CameraBarcodeGuide.Parent is not FrameworkElement guideHost)
                return;

            Point videoOriginInHost = VideoImage.TranslatePoint(new Point(0, 0), guideHost);
            CameraBarcodeGuide.RenderTransform = new TranslateTransform(
                videoOriginInHost.X + guideRect.X - ((guideHost.ActualWidth - guideRect.Width) / 2.0),
                videoOriginInHost.Y + guideRect.Y - ((guideHost.ActualHeight - guideRect.Height) / 2.0));
            // 画中画在主摄识别框之上：被小窗盖住的那段框线要真的被遮掉，不能透出来。
            // 只裁框体本身，别裁到四角把手和拖动命中层（它们要留在框外一点）。
            CameraBarcodeGuideBox.Clip = BuildGuideClip(guideRect, ResolveOverlayOccluders(vm, actualW, actualH));

            // 小锁与提示是独立的一层（铺满整块预览、在最顶层）：居中放在取景框顶部内侧，
            // 再整体夹进预览范围内 —— 框很窄、或者贴着预览边缘时也不会被预览边界裁掉。
            // 坐标同样要先从"画面坐标系"换算到提示层的坐标系（两者原点不重合）。
            Point hintOriginInLayer = VideoImage.TranslatePoint(new Point(0, 0), CameraBarcodeGuideHintLayer);
            double hostWidth = Math.Max(guideRect.Width, 1);
            double panelWidth = CameraBarcodeGuideHintPanel.ActualWidth > 0
                ? CameraBarcodeGuideHintPanel.ActualWidth
                : CameraBarcodeGuideHintPanel.DesiredSize.Width;
            if (panelWidth <= 0)
                panelWidth = hostWidth;

            // 提示文字按框宽换行（下面给状态条限宽），于是整块提示永远不会比框宽：
            // 居中放就必然落在框里，也就不会被框或预览边界裁掉。
            CameraBarcodeGuideStatusBorder.MaxWidth = Math.Max(80, guideRect.Width - 38);
            double panelLeft = hintOriginInLayer.X + guideRect.X + ((guideRect.Width - panelWidth) / 2);

            // 宿主与取景框同宽、面板在宿主里居中：面板左边界 = 宿主左边界 + (宿主宽 - 面板宽)/2
            CameraBarcodeGuideHintHost.Width = hostWidth;
            Canvas.SetLeft(CameraBarcodeGuideHintHost, panelLeft - ((hostWidth - panelWidth) / 2));
            Canvas.SetTop(CameraBarcodeGuideHintHost, hintOriginInLayer.Y + guideRect.Y + 10);
        }

        /// <summary>
        /// 预览里每一块画中画的矩形：只有"识别框画在主画面上"时才需要拿它们挖洞。
        /// 编辑取景时预览里没有画中画；识别来源是画中画时框本身就贴在那块上，不能再挖掉。
        /// </summary>
        private List<Rect> ResolveOverlayOccluders(MainViewModel vm, double actualW, double actualH)
        {
            var occluders = new List<Rect>();
            if (vm.IsEditingOverlayPreview
                || vm.ShouldUseOverlayChannelForBarcode
                || vm.VideoFrame is not { PixelWidth: > 0, PixelHeight: > 0 } frame)
            {
                return occluders;
            }

            Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(
                frame.PixelWidth,
                frame.PixelHeight,
                actualW,
                actualH);
            if (videoRect.IsEmpty || videoRect.Width <= 0)
                return occluders;

            double scale = videoRect.Width / frame.PixelWidth;
            foreach (int channelNumber in vm.VisibleOverlayChannelNumbers)
            {
                if (!vm.TryResolveOverlayRect(channelNumber, frame.PixelWidth, frame.PixelHeight, out CameraOverlayRect overlay))
                    continue;

                occluders.Add(new Rect(
                    videoRect.X + (overlay.X * scale),
                    videoRect.Y + (overlay.Y * scale),
                    overlay.Width * scale,
                    overlay.Height * scale));
            }

            return occluders;
        }

        /// <summary>
        /// 把识别框裁成"框减掉每一块画中画"的形状；没有遮挡时返回 null（不裁剪）。
        /// 这样被画中画盖住的那段框线真的被遮掉，不会透出来。
        /// </summary>
        private static Geometry? BuildGuideClip(Rect guideRect, IReadOnlyList<Rect> occluders)
        {
            // 外扩 2px：框线是 3px 描边，正好压在矩形边界上，不外扩会被裁掉一圈。
            Geometry clip = new RectangleGeometry(
                new Rect(-2, -2, guideRect.Width + 4, guideRect.Height + 4));
            bool clipped = false;
            foreach (Rect pip in occluders)
            {
                if (pip.Width <= 0 || pip.Height <= 0)
                    continue;

                clip = new CombinedGeometry(
                    GeometryCombineMode.Exclude,
                    clip,
                    new RectangleGeometry(new Rect(pip.X - guideRect.X, pip.Y - guideRect.Y, pip.Width, pip.Height)));
                clipped = true;
            }

            return clipped ? clip : null;
        }

        /// <summary>
        /// 识别框解锁后可直接在主界面调整：拖动框体平移，拖动四角改变大小。
        /// 拖动过程即时生效但不落盘，松手时才写配置，避免鼠标每动一下都写一次文件。
        /// </summary>
        private void CameraGuideMoveThumb_DragDelta(object sender, DragDeltaEventArgs e) =>
            AdjustCameraBarcodeGuide(geometry => CameraBarcodeGuideLayout.Move(
                geometry,
                GetCameraGuideVideoRect(),
                e.HorizontalChange,
                e.VerticalChange));

        private void CameraGuideHandleThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb thumb
                || thumb.Tag is not string tag
                || !Enum.TryParse(tag, out CameraBarcodeGuideHandle handle))
            {
                return;
            }

            AdjustCameraBarcodeGuide(geometry => CameraBarcodeGuideLayout.Resize(
                geometry,
                GetCameraGuideVideoRect(),
                handle,
                e.HorizontalChange,
                e.VerticalChange));
        }

        private void CameraGuideDrag_Completed(object sender, DragCompletedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            // 编辑副摄时 ViewModel 会把读写都落到副摄那一组，这里无需分支。
            vm.ApplyCameraBarcodeGuideGeometry(vm.CurrentCameraBarcodeGuideGeometry, persist: true);
        }

        private void BtnCameraGuideLock_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            vm.ToggleCameraBarcodeGuideLock();
            UpdateCameraBarcodeGuide(vm);
        }

        private void AdjustCameraBarcodeGuide(Func<CameraBarcodeGuideGeometry, CameraBarcodeGuideGeometry> adjust)
        {
            if (DataContext is not MainViewModel vm)
                return;

            // 编辑副摄时 ViewModel 会把读写都落到副摄那一组，这里无需分支。
            vm.ApplyCameraBarcodeGuideGeometry(adjust(vm.CurrentCameraBarcodeGuideGeometry), persist: false);
            UpdateCameraBarcodeGuide(vm);
        }

        /// <summary>预览控件里画面实际占据的矩形；拖动换算必须和取景用的整帧比例一致</summary>
        private Rect GetCameraGuideVideoRect()
        {
            if (DataContext is not MainViewModel vm)
                return Rect.Empty;

            // 与 UpdateCameraBarcodeGuide 用同一个尺寸来源（编辑副摄时 ViewModel 已切换）。
            return CameraBarcodeGuideLayout.GetVideoRect(
                vm.CameraFrameSize.Width,
                vm.CameraFrameSize.Height,
                VideoImage.ActualWidth,
                VideoImage.ActualHeight);
        }

        /// <summary>
        /// 条码区域左键也呼出菜单：现场更习惯直接点一下，不必记得右键。
        /// </summary>
        private void BarcodeArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement element
                || element.ContextMenu is not ContextMenu menu)
            {
                return;
            }

            menu.PlacementTarget = element;
            menu.IsOpen = true;
            e.Handled = true;
        }

        /// <summary>
        /// 条码右键菜单：现场常把指令条码打印出来贴墙给摄像头扫，这里提供
        /// "打印这一条 / 打印整套 / 打开条码图片位置"；条码冷却隐藏时对应项置灰。
        /// </summary>
        private void BarcodeContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu menu)
                return;

            BarcodePrintService.BarcodePrintItem? barcode = ResolveContextBarcode(menu);
            menu.Tag = barcode;
            foreach (object entry in menu.Items)
            {
                if (entry is MenuItem { Tag: "current" } item)
                    item.IsEnabled = barcode != null;
            }
        }

        private void PrintCurrentBarcode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item
                || item.Parent is not ContextMenu menu
                || menu.Tag is not BarcodePrintService.BarcodePrintItem barcode)
            {
                return;
            }

            PrintCommandBarcodes([barcode], AppLanguage.Get("打印单条指令条码"));
        }

        private void PrintAllBarcodes_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            PrintCommandBarcodes(vm.CommandBarcodePrintItems, AppLanguage.Get("指令条码"));
        }

        /// <summary>
        /// 重新生成整套指令条码图片（固定放在用户数据目录下，覆盖旧文件）并打开该文件夹。
        /// 菜单项和条码面板上的图标按钮共用这里，用户不需要再选保存位置。
        /// </summary>
        private void OpenBarcodeImageFolder_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            int generated = vm.RefreshCommandBarcodeImageFiles();
            if (generated <= 0)
            {
                AppDialog.Error(this, AppLanguage.Get("条码图片生成失败"), AppLanguage.Get("指令条码"));
                return;
            }

            WindowsShellFileLocator.OpenFolder(AppPaths.CommandBarcodeImageDir);
            vm.ShowToast(AppLanguage.Format("已生成指令条码图片", generated));
        }

        private void PrintCommandBarcodes(
            IReadOnlyList<BarcodePrintService.BarcodePrintItem> items,
            string title)
        {
            if (items.Count == 0)
                return;

            try
            {
                var dialog = new PrintDialog();
                if (dialog.ShowDialog() != true)
                    return;

                DrawingVisual page = BarcodePrintService.BuildPage(
                    items,
                    dialog.PrintableAreaWidth,
                    dialog.PrintableAreaHeight,
                    title,
                    AppLanguage.Get("打印条码说明"));
                dialog.PrintVisual(page, title);
                (DataContext as MainViewModel)?.ShowToast(AppLanguage.Get("指令条码已发送到打印机"));
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Print", "Printing command barcodes failed", ex);
                AppDialog.Error(this, AppLanguage.Get("打印失败，请检查打印机"), AppLanguage.Get("指令条码"));
            }
        }

        /// <summary>右键点的是哪一条条码；条码正在冷却、界面上没有条码时返回 null</summary>
        private BarcodePrintService.BarcodePrintItem? ResolveContextBarcode(ContextMenu menu)
        {
            if (DataContext is not MainViewModel vm
                || menu.PlacementTarget is not FrameworkElement target
                || target.Tag is not string slot)
            {
                return null;
            }

            bool firstBarcode = slot == "1";
            if (firstBarcode ? vm.Barcode1Image == null : vm.Barcode2Image == null)
                return null;

            string payload = firstBarcode ? vm.Barcode1Payload : vm.Barcode2Payload;
            if (string.IsNullOrWhiteSpace(payload))
                return null;

            string label = firstBarcode ? vm.Barcode1Label : vm.Barcode2Label;
            return new BarcodePrintService.BarcodePrintItem(
                string.IsNullOrWhiteSpace(label) ? payload : label,
                payload);
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel) viewModel.OpenSettings();
        }

        private void BtnMobileConnection_Click(object sender, RoutedEventArgs e)
        {
            ExecuteMobileConnection();
            e.Handled = true;
        }

        private void BtnSwitchWorkstation_Click(object sender, RoutedEventArgs e)
        {
            ExecuteSwitchWorkstation();
            e.Handled = true;
        }

        private void BtnMobileConnection_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ExecuteMobileConnection();
            e.Handled = true;
        }

        private void BtnSwitchWorkstation_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ExecuteSwitchWorkstation();
            e.Handled = true;
        }

        private void ExecuteMobileConnection()
        {
            if (DataContext is MainViewModel viewModel) viewModel.ShowMainConnection(this);
        }

        private void ExecuteSwitchWorkstation()
        {
            if (DataContext is MainViewModel viewModel) viewModel.SwitchWorkstation();
        }

        private void ScanInputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ResetScanAutoSubmitState();
                string scanResult = ScanInputTextBox.Text.Trim();
                if (DataContext is MainViewModel viewModel)
                {
                    if (viewModel.ScanCommand.CanExecute(scanResult)) viewModel.ScanCommand.Execute(scanResult);
                }
                // 彻底交由 ViewModel 接管清空逻辑
                e.Handled = true;
            }
        }

        private void ScanInputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (DataContext is not MainViewModel viewModel || !viewModel.Config.EnableScannerAutoSubmit)
            {
                ResetScanAutoSubmitState();
                _lastScanInputLength = ScanInputTextBox.Text?.Length ?? 0;
                return;
            }

            string text = ScanInputTextBox.Text ?? "";
            if (text.Length == 0)
            {
                ResetScanAutoSubmitState();
                return;
            }

            int addedCount = text.Length - _lastScanInputLength;
            if (addedCount <= 0)
            {
                ResetScanAutoSubmitState();
                _lastScanInputLength = text.Length;
                return;
            }

            var now = DateTime.Now;
            int sequenceBreakMs = Math.Max(100, viewModel.Config.ScannerAutoSubmitMaxKeyIntervalMs);
            for (int i = 0; i < addedCount; i++)
            {
                if (_lastScanInputCharAt != DateTime.MinValue)
                {
                    double elapsed = (now - _lastScanInputCharAt).TotalMilliseconds;
                    if (elapsed > sequenceBreakMs)
                    {
                        _scanInputIntervalsMs.Clear();
                    }
                    else
                    {
                        _scanInputIntervalsMs.Add(elapsed);
                    }
                }
                _lastScanInputCharAt = now;
            }

            _lastScanInputLength = text.Length;
            ScheduleScanAutoSubmitCheck(viewModel.Config.ScannerAutoSubmitQuietMs);
        }

        private void ScheduleScanAutoSubmitCheck(int quietMs)
        {
            _scanAutoSubmitTimer.Stop();
            _scanAutoSubmitTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(quietMs, 120, 600));
            _scanAutoSubmitTimer.Start();
        }

        private void ScanAutoSubmitTimer_Tick(object? sender, EventArgs e)
        {
            _scanAutoSubmitTimer.Stop();

            if (DataContext is not MainViewModel viewModel || !viewModel.Config.EnableScannerAutoSubmit)
                return;

            if ((DateTime.Now - _lastScanInputCharAt).TotalMilliseconds < viewModel.Config.ScannerAutoSubmitQuietMs)
            {
                ScheduleScanAutoSubmitCheck(viewModel.Config.ScannerAutoSubmitQuietMs);
                return;
            }

            string scanResult = ScanInputTextBox.Text.Trim();
            if (scanResult.Length < viewModel.Config.ScannerAutoSubmitMinLength)
                return;

            if (!viewModel.IsAutoSubmitScanCandidate(scanResult))
                return;

            if (!ScannerAutoSubmitPolicy.IsFastSequence(
                    _scanInputIntervalsMs,
                    scanResult.Length,
                    viewModel.Config.ScannerAutoSubmitMaxAverageIntervalMs,
                    viewModel.Config.ScannerAutoSubmitMaxKeyIntervalMs))
                return;

            ResetScanAutoSubmitState();
            if (viewModel.ScanCommand.CanExecute(scanResult))
                viewModel.ScanCommand.Execute(scanResult);
        }

        private void ResetScanAutoSubmitState()
        {
            _scanAutoSubmitTimer.Stop();
            _scanInputIntervalsMs.Clear();
            _lastScanInputCharAt = DateTime.MinValue;
            _lastScanInputLength = ScanInputTextBox?.Text?.Length ?? 0;
        }

        private void ScanInputTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            _capsCheckTimer.Stop();
            // 延迟检查 IsActive，避免在 Deactivated 之前抢先 re-focus 导致 CapsLock 恢复失败
            Dispatcher.BeginInvoke(new System.Action(() => { if (!_capsLockSuspended && this.IsActive) ScanInputTextBox.Focus(); }));
        }

        private void ScanInputTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            ApplyCapsLockForScanInput();
            Dispatcher.BeginInvoke(new System.Action(() => ScanInputTextBox.SelectAll()));
        }

        private void ScanInputTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!ScanInputTextBox.IsKeyboardFocusWithin) { e.Handled = true; ScanInputTextBox.Focus(); }
        }

        private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            var vm = DataContext as MainViewModel;

            if (_shutdownConfirmed)
            {
                e.Cancel = true;
                await FinishShutdownAsync(vm);
                return;
            }

            e.Cancel = true;
            if (_shutdownInProgress) return;

            WindowCloseChoice closeChoice = _closeBehaviorController.HandleClose(
                vm?.Config ?? WorkstationConfigStore.Load(),
                bypassPreference: WorkstationNetwork.IsRestartPending || _exitRequestedFromTray);
            _exitRequestedFromTray = false;
            if (closeChoice != WindowCloseChoice.Exit)
                return;

            // 1. 判断是否需要提示：只有正在录制时才提示
            if (vm != null && vm.IsRecording && !WorkstationNetwork.IsRestartPending)
            {
                string msg = "当前正在录制，退出将自动保存当前视频。\n确定要退出程序吗？";
                // 如果用户在弹窗中点击了“取消”，则拦截退出事件
                if (!AppDialog.Confirm(
                        this,
                        msg,
                        "正在录制 - 退出确认",
                        AppDialogSeverity.Warning,
                        confirmText: "退出并保存",
                        cancelText: "继续录制",
                        isDangerous: true))
                {
                    e.Cancel = true;
                    return;
                }
            }

            _shutdownInProgress = true;
            _capsCheckTimer.Stop();
            RestoreCapsLockState();
            (string shutdownSource, string shutdownDetail) = RuntimeLog.GetShutdownRequest();
            if (string.Equals(shutdownSource, "not-recorded", StringComparison.Ordinal))
            {
                RuntimeLog.RecordShutdownRequest(
                    "WpfWindowClosing",
                    $"isActive={IsActive}, windowState={WindowState}, isVisible={IsVisible}");
                (shutdownSource, shutdownDetail) = RuntimeLog.GetShutdownRequest();
            }
            RuntimeLog.Info("Shutdown", $"Main window closing requested session={RuntimeLog.CurrentSessionId}, source={shutdownSource}, detail={shutdownDetail}");

            bool saved = true;
            if (vm != null)
            {
                var progress = new Progress<string>(msg =>
                {
                    vm.BusyText = "正在关闭程序...";
                    vm.IsBusy = true;
                    if (!IsRoutineShutdownProgressMessage(msg))
                        vm.ShowToast(msg, ToastSeverity.Information);
                });
                saved = await vm.SaveRecordingsBeforeShutdownAsync(progress);
            }

            if (!saved)
            {
                _shutdownInProgress = false;
                WorkstationNetwork.CancelPendingRestart();
                AppDialog.Error(this, "录像保存失败，请检查日志", "退出已取消");
                return;
            }

            _shutdownConfirmed = true;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    Close();
                }
                catch (InvalidOperationException ex)
                {
                    RuntimeLog.Warn("Shutdown", $"Confirmed close failed, force shutdown: {ex.Message}");
                    _ = FinishShutdownAsync(vm);
                }
            }), DispatcherPriority.Background);
        }

        private void BtnInstallUserscript_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel viewModel)
                viewModel.OpenUserscriptGuide();
            e.Handled = true;
        }

        private async void BtnSendTestOrder_Click(object sender, RoutedEventArgs e)
        {
            if (_testOrderSending || DataContext is not MainViewModel viewModel)
                return;

            _testOrderSending = true;
            BtnSendTestOrder.IsEnabled = false;
            SendTestOrderButtonText.Text = "正在发送";
            try
            {
                WorkstationNetwork.TestOrderBroadcastResult result =
                    await WorkstationNetwork.SendTestOrderToRecordingDevicesAsync(
                        WorkstationNetwork.ResolveTestOrderHostAddress(
                            viewModel.MonitorAccessAddress,
                            viewModel.BoundHostAddress));
                AppDialogSeverity severity = result.HasTargets && result.FailureCount == 0
                    ? AppDialogSeverity.Information
                    : AppDialogSeverity.Warning;
                AppDialog.ShowMessage(
                    this,
                    WorkstationNetwork.FormatTestOrderBroadcastResult(result),
                    "发送测试订单",
                    severity);
            }
            finally
            {
                _testOrderSending = false;
                BtnSendTestOrder.IsEnabled = true;
                SendTestOrderButtonText.Text = "发送测试订单";
            }

            e.Handled = true;
        }

        private void RequestExitFromTray()
        {
            _exitRequestedFromTray = true;
            Close();
        }

        private static bool IsRoutineShutdownProgressMessage(string message)
        {
            return !string.IsNullOrWhiteSpace(message)
                && message.Contains("文件不存在，跳过", StringComparison.Ordinal);
        }

        internal enum BottomBarLayout
        {
            AllText,
            AllIconOnly,
            WithoutTotalIconOnly,
            OnlyTodayIconOnly,
            OnlyTodayNoData
        }

        internal static BottomBarLayout ResolveBottomBarLayout(
            double availableContentWidth,
            double todayWidth,
            double averageWidth,
            double totalWidth,
            double gap,
            double buttonsTextWidth,
            double buttonsIconWidth)
        {
            const double tolerance = 1.0;
            if (todayWidth + gap + averageWidth + gap + totalWidth + buttonsTextWidth
                <= availableContentWidth + tolerance)
                return BottomBarLayout.AllText;
            if (todayWidth + gap + averageWidth + gap + totalWidth + buttonsIconWidth
                <= availableContentWidth + tolerance)
                return BottomBarLayout.AllIconOnly;
            if (todayWidth + gap + averageWidth + buttonsIconWidth
                <= availableContentWidth + tolerance)
                return BottomBarLayout.WithoutTotalIconOnly;
            if (todayWidth + buttonsIconWidth <= availableContentWidth + tolerance)
                return BottomBarLayout.OnlyTodayIconOnly;
            return BottomBarLayout.OnlyTodayNoData;
        }

        internal enum ActionButtonLayout
        {
            Text,
            IconOnly,
            IconOnlyNoData
        }

        internal static (bool AverageVisible, bool TotalVisible, ActionButtonLayout Buttons)
            ResolveBottomBarVisibility(BottomBarLayout layout) =>
            layout switch
            {
                BottomBarLayout.AllText => (true, true, ActionButtonLayout.Text),
                BottomBarLayout.AllIconOnly => (true, true, ActionButtonLayout.IconOnly),
                BottomBarLayout.WithoutTotalIconOnly => (true, false, ActionButtonLayout.IconOnly),
                BottomBarLayout.OnlyTodayIconOnly => (false, false, ActionButtonLayout.IconOnly),
                _ => (false, false, ActionButtonLayout.IconOnlyNoData)
            };

        private bool _statsBarUpdating;
        private bool _topBarUpdating;

        private void UpdateStatsBarVisibility()
        {
            if (_statsBarUpdating || StatsBarBorder == null || StatsBarBorder.ActualWidth <= 0)
                return;

            _statsBarUpdating = true;
            try
            {
                TodayCountGroup.Visibility = Visibility.Visible;
                AverageTimeGroup.Visibility = Visibility.Visible;
                TotalTimeGroup.Visibility = Visibility.Visible;
                ApplyBottomButtonLayout(ActionButtonLayout.Text);
                DataButton.Visibility = Visibility.Visible;

                double todayWidth = MeasureStatsGroupWidth(TodayCountGroup);
                double averageWidth = MeasureStatsGroupWidth(AverageTimeGroup);
                double totalWidth = MeasureStatsGroupWidth(TotalTimeGroup);
                double availableContentWidth = Math.Max(0, StatsBarBorder.ActualWidth - 40);
                double buttonsTextWidth = ActionButtonsPanel.Children
                    .OfType<Button>()
                    .Sum(MeasureButtonOuterWidth);
                double buttonsIconWidth = ActionButtonsPanel.Children
                    .OfType<Button>()
                    .Sum(button => IconButtonWidth + button.Margin.Left + button.Margin.Right);

                BottomBarLayout layout = ResolveBottomBarLayout(
                    availableContentWidth,
                    todayWidth,
                    averageWidth,
                    totalWidth,
                    16,
                    buttonsTextWidth,
                    buttonsIconWidth);
                ApplyBottomBarLayout(layout);
            }
            finally
            {
                _statsBarUpdating = false;
            }
        }

        private static double MeasureStatsGroupWidth(FrameworkElement group)
        {
            group.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Max(0, group.DesiredSize.Width - group.Margin.Left - group.Margin.Right);
        }

        private static double MeasureButtonOuterWidth(Button button)
        {
            button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return button.DesiredSize.Width + button.Margin.Left + button.Margin.Right;
        }

        private void ApplyBottomBarLayout(BottomBarLayout layout)
        {
            (bool averageVisible, bool totalVisible, ActionButtonLayout buttons) =
                ResolveBottomBarVisibility(layout);
            AverageTimeGroup.Visibility =
                averageVisible ? Visibility.Visible : Visibility.Collapsed;
            TotalTimeGroup.Visibility =
                totalVisible ? Visibility.Visible : Visibility.Collapsed;
            ApplyBottomButtonLayout(buttons);
            DataButton.Visibility =
                buttons == ActionButtonLayout.IconOnlyNoData
                    ? Visibility.Collapsed
                    : Visibility.Visible;
        }

        private void ApplyBottomButtonLayout(ActionButtonLayout layout)
        {
            bool iconOnly = layout != ActionButtonLayout.Text;
            double width = iconOnly ? IconButtonWidth : 120;
            DataButton.Width = width;
            PlaybackButton.Width = width;
            SettingsButton.Width = width;

            DataButtonText.Visibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;
            PlaybackButtonText.Visibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;
            SettingsButtonText.Visibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;

            Thickness iconMargin = iconOnly ? new Thickness(0) : new Thickness(0, 0, 7, 0);
            DataButtonIcon.Margin = iconMargin;
            PlaybackButtonIcon.Margin = iconMargin;
            SettingsButtonIcon.Margin = iconMargin;
        }

        internal enum TopBarCompactState
        {
            BothText,
            ModeIconOnly,
            BothIconOnly
        }

        internal static TopBarCompactState ResolveTopBarCompactState(
            double availableWidth,
            double modeTextWidth,
            double recordTextWidth,
            double iconWidth,
            double modeRightMargin,
            double columnGap,
            double minimumScanWidth)
        {
            const double tolerance = 1.0;
            if (modeTextWidth + modeRightMargin + columnGap + recordTextWidth + minimumScanWidth
                <= availableWidth + tolerance)
                return TopBarCompactState.BothText;
            if (iconWidth + modeRightMargin + columnGap + recordTextWidth + minimumScanWidth
                <= availableWidth + tolerance)
                return TopBarCompactState.ModeIconOnly;
            return TopBarCompactState.BothIconOnly;
        }

        private void UpdateTopBarCompactState()
        {
            if (_topBarUpdating || TopBarBorder == null || TopBarBorder.ActualWidth <= 0)
                return;

            _topBarUpdating = true;
            try
            {
                double availableWidth = Math.Max(0, TopBarBorder.ActualWidth - 40);
                TopBarCompactState state = ResolveTopBarCompactState(
                    availableWidth,
                    TopModeButtonTextWidth,
                    TopRecordButtonTextWidth,
                    IconButtonWidth,
                    TopModeButtonRightMargin,
                    TopColumnGap,
                    MinimumScanInputWidth);
                IsModeButtonCompact =
                    state is TopBarCompactState.ModeIconOnly or TopBarCompactState.BothIconOnly;
                IsRecordButtonCompact = state == TopBarCompactState.BothIconOnly;
            }
            finally
            {
                _topBarUpdating = false;
            }
        }

        private void OnStatsViewModelPropertyChanged(
            object? sender,
            System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.TotalPieces)
                || e.PropertyName == nameof(MainViewModel.AveragePackTimeDisplay)
                || e.PropertyName == nameof(MainViewModel.TotalPackTimeDisplay)
                || e.PropertyName == nameof(MainViewModel.CurrentMode)
                || e.PropertyName == nameof(MainViewModel.IsRecording)
                || e.PropertyName == nameof(MainViewModel.IsBusy))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateStatsBarVisibility();
                    UpdateTopBarCompactState();
                }));
            }
        }

        private async Task FinishShutdownAsync(MainViewModel? vm)
        {
            if (_resourceCleanupInProgress) return;
            _resourceCleanupInProgress = true;
            _capsCheckTimer.Stop();
            RestoreCapsLockState();

            if (vm != null)
            {
                vm.BusyText = "正在关闭程序...";
                vm.IsBusy = true;
            }

            try
            {
                if (vm is System.IDisposable disposable)
                    await Task.Run(disposable.Dispose);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Shutdown", "Background resource cleanup failed", ex);
            }
            finally
            {
                _closeBehaviorController.Dispose();
                // 录像、Web 服务和数据库均已释放，此时才允许按新的录像方式启动。
                WorkstationNetwork.TryStartPendingRestart();

                // 录像收尾已经完成；解除 Closing 处理器后显式退出，避免后台资源让进程残留。
                Closing -= Window_Closing;
                (string source, string detail) = RuntimeLog.GetShutdownRequest();
                RuntimeLog.Info("Shutdown", $"Process exit requested session={RuntimeLog.CurrentSessionId}, pid={Environment.ProcessId}, source={source}, detail={detail}");
                try { Application.Current?.Shutdown(0); } catch { }
                Environment.Exit(0);
            }
        }
    }
}
