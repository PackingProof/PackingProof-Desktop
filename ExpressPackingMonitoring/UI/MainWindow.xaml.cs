using ExpressPackingMonitoring.Input;
using ExpressPackingMonitoring.Logging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.ViewModels;
using ExpressPackingMonitoring.Services;

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

        public static readonly DependencyProperty IsCompactLayoutProperty = DependencyProperty.Register(
            nameof(IsCompactLayout),
            typeof(bool),
            typeof(MainWindow),
            new PropertyMetadata(false));

        public bool IsCompactLayout
        {
            get => (bool)GetValue(IsCompactLayoutProperty);
            set => SetValue(IsCompactLayoutProperty, value);
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
            if (DataContext is MainViewModel statsViewModel)
            {
                statsViewModel.PropertyChanged += OnStatsViewModelPropertyChanged;
            }
            _closeBehaviorController = new WindowCloseBehaviorController(
                this,
                RequestExitFromTray,
                enableCloseBehaviorPrompt);
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
                }
                else
                {
                    ApplyCapsLockForScanInput();
                }
            };
            // 全局鼠标/键盘活跃检测，用于摄像头空闲休眠唤醒
            PreviewMouseMove += (s, e) =>
            {
                var now = DateTime.UtcNow;
                if (now - _lastMouseActivityNotifyAt < TimeSpan.FromSeconds(1)) return;
                _lastMouseActivityNotifyAt = now;
                (DataContext as MainViewModel)?.NotifyUserActivity();
            };
            PreviewKeyDown += (s, e) => (DataContext as MainViewModel)?.NotifyUserActivity();
            Loaded += (s, e) => {
                ScanInputTextBox.Focus();
                if (DataContext is MainViewModel vm)
                {
                    vm.PropertyChanged += (sender, args) =>
                    {
                        if (args.PropertyName == nameof(MainViewModel.LastZoomRect) ||
                            args.PropertyName == nameof(MainViewModel.CameraFrameSize) ||
                            args.PropertyName == nameof(MainViewModel.IsCameraBarcodeRecognitionEnabled))
                        {
                            Dispatcher.BeginInvoke(new Action(() => UpdateCameraOverlays(vm)));
                        }
                    };
                    // 窗口/视频区域大小变化时重新计算边框位置
                    VideoImage.SizeChanged += (_, __) =>
                    {
                        UpdateCameraOverlays(vm);
                    };
                }

                Title = AppLanguage.Format("Main.Title", AppVersion.Current);
#if DEBUG
                Title += " [摄像头对照调试：摄像头不触发录制]";
#endif

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

        private void UpdateZoomBorder(Rect zoomRect)
        {
            var vm = DataContext as MainViewModel;
            if (zoomRect == Rect.Empty || vm == null || vm.CameraFrameSize.Width <= 0 || vm.CameraFrameSize.Height <= 0)
            {
                ZoomPreviewBorder.Visibility = Visibility.Collapsed;
                return;
            }

            double actualW = VideoImage.ActualWidth;
            double actualH = VideoImage.ActualHeight;
            // 始终基于摄像头原始帧尺寸计算，而非 VideoImage.Source（放大时 Source 会变）
            double sourceW = vm.CameraFrameSize.Width;
            double sourceH = vm.CameraFrameSize.Height;

            if (actualW <= 0 || actualH <= 0) return;

            // Uniform 缩放比例
            double scale = Math.Min(actualW / sourceW, actualH / sourceH);

            ZoomPreviewBorder.Width = zoomRect.Width * scale;
            ZoomPreviewBorder.Height = zoomRect.Height * scale;
            ZoomPreviewBorder.Visibility = Visibility.Visible;
        }

        private void UpdateCameraOverlays(MainViewModel vm)
        {
            UpdateZoomBorder(vm.LastZoomRect);
            UpdateCameraBarcodeGuide(vm);
        }

        private void UpdateCameraBarcodeGuide(MainViewModel vm)
        {
            double sourceW = vm.CameraFrameSize.Width;
            double sourceH = vm.CameraFrameSize.Height;
            double actualW = VideoImage.ActualWidth;
            double actualH = VideoImage.ActualHeight;
            if (sourceW <= 0 || sourceH <= 0 || actualW <= 0 || actualH <= 0)
            {
                CameraBarcodeGuide.Width = 0;
                CameraBarcodeGuide.Height = 0;
                return;
            }

            double scale = Math.Min(actualW / sourceW, actualH / sourceH);
            CameraBarcodeGuide.Width = sourceW * CameraBarcodeFrameDecoder.GuideWidthRatio * scale;
            CameraBarcodeGuide.Height = sourceH * CameraBarcodeFrameDecoder.GuideHeightRatio * scale;
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
                    await WorkstationNetwork.SendTestOrderToRecordingDevicesAsync(viewModel.MonitorAccessAddress);
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

        internal enum StatsBarVisibility
        {
            All,
            WithoutTotal,
            OnlyToday
        }

        internal static StatsBarVisibility ResolveStatsVisibility(
            double availableWidth,
            double todayWidth,
            double averageWidth,
            double totalWidth,
            double gap)
        {
            const double tolerance = 1.0;
            if (todayWidth + gap + averageWidth + gap + totalWidth <= availableWidth + tolerance)
                return StatsBarVisibility.All;
            if (todayWidth + gap + averageWidth <= availableWidth + tolerance)
                return StatsBarVisibility.WithoutTotal;
            return StatsBarVisibility.OnlyToday;
        }

        internal static (bool AverageVisible, bool TotalVisible) ResolveGroupVisibility(
            StatsBarVisibility visibility) =>
            visibility switch
            {
                StatsBarVisibility.All => (true, true),
                StatsBarVisibility.WithoutTotal => (true, false),
                _ => (false, false)
            };

        internal enum ActionButtonLayout
        {
            Text,
            IconOnly,
            IconOnlyNoData
        }

        internal static ActionButtonLayout ResolveActionButtonLayout(
            double availableContentWidth,
            double onlyTodayWidth,
            double buttonsTextWidth,
            double buttonsIconWidth,
            double buttonsIconWithoutDataWidth)
        {
            const double tolerance = 1.0;
            if (onlyTodayWidth + buttonsTextWidth <= availableContentWidth + tolerance)
                return ActionButtonLayout.Text;
            if (onlyTodayWidth + buttonsIconWidth <= availableContentWidth + tolerance)
                return ActionButtonLayout.IconOnly;
            return ActionButtonLayout.IconOnlyNoData;
        }

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
                double buttonsIconWithoutDataWidth = buttonsIconWidth
                    - (IconButtonWidth + DataButton.Margin.Left + DataButton.Margin.Right);

                StatsBarVisibility visibility =
                    ResolveStatsVisibility(
                        availableContentWidth - buttonsTextWidth,
                        todayWidth,
                        averageWidth,
                        totalWidth,
                        16);
                (bool averageVisible, bool totalVisible) = ResolveGroupVisibility(visibility);
                TotalTimeGroup.Visibility =
                    totalVisible ? Visibility.Visible : Visibility.Collapsed;
                AverageTimeGroup.Visibility =
                    averageVisible ? Visibility.Visible : Visibility.Collapsed;

                ActionButtonLayout buttonLayout = visibility == StatsBarVisibility.OnlyToday
                    ? ResolveActionButtonLayout(
                        availableContentWidth,
                        todayWidth,
                        buttonsTextWidth,
                        buttonsIconWidth,
                        buttonsIconWithoutDataWidth)
                    : ActionButtonLayout.Text;
                ApplyBottomButtonLayout(buttonLayout);
                DataButton.Visibility =
                    buttonLayout == ActionButtonLayout.IconOnlyNoData
                        ? Visibility.Collapsed
                        : Visibility.Visible;
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

        private void UpdateTopBarCompactState()
        {
            if (_topBarUpdating || TopBarBorder == null || TopBarBorder.ActualWidth <= 0)
                return;

            _topBarUpdating = true;
            try
            {
                double availableWidth = Math.Max(0, TopBarBorder.ActualWidth - 40);
                double requiredWidth = TopModeButtonTextWidth
                    + TopModeButtonRightMargin
                    + TopColumnGap
                    + TopRecordButtonTextWidth
                    + MinimumScanInputWidth;
                IsCompactLayout = requiredWidth > availableWidth;
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
