using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.UI.Controls;
using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 所有 XAML 窗口/控件都要能真正构造并走完一次布局。
///
/// 设置窗口把 ContextMenu 内联写在 Setter.Value 里以后，加载直接抛 InvalidCastException，
/// 而编译、BAML 校验和"读源码断言字符串"的守卫都发现不了，只有用户点开设置才炸。
/// 这里两道守卫：真的构造一遍 + 新增 XAML 窗口必须登记（构造或写清为什么暂时不构造）。
/// </summary>
[Collection("WPF render tests")]
public sealed class WindowConstructionTests
{
    private const string AppXamlClass = "ExpressPackingMonitoring.App";

    /// <summary>在别的渲染测试里已经真构造过的窗口。</summary>
    private static readonly string[] CoveredByOtherTests =
    [
        "ExpressPackingMonitoring.UI.PlaybackWindow",  // PlaybackWindowRenderTests
        "ExpressPackingMonitoring.UI.PlaybackScreenshotDialog", // PlaybackWindowRenderTests / UiScreenshotTests
        "ExpressPackingMonitoring.UI.SettingsWindow"   // SettingsWindowRenderTests
    ];

    /// <summary>暂时不做真构造的窗口：必须写清原因，改到它们时重新评估。</summary>
    private static readonly Dictionary<string, string> KnownGaps = new(StringComparer.Ordinal)
    {
        ["ExpressPackingMonitoring.UI.FirstUseSetupWizardWindow"] = "构造里就拉起条码识别与摄像头枚举",
        ["ExpressPackingMonitoring.UI.FloatingPreviewWindow"] = "需要真实 MainViewModel 与摄像头预览栈",
        ["ExpressPackingMonitoring.PrintWorkstationWindow"] = "构造即启动工位会话",
        ["ExpressPackingMonitoring.ViewerClientWindow"] = "构造即启动查看端会话"
    };

    [Fact]
    public void EveryXamlWindowOrControlIsConstructedOrRegistered()
    {
        string[] declared = DeclaredXamlClasses();
        string[] unregistered = declared
            .Where(name => !CoveredByOtherTests.Contains(name, StringComparer.Ordinal))
            .Where(name => !KnownGaps.ContainsKey(name))
            .Where(name => !Factories().Any(entry => entry.Name == name))
            .ToArray();

        Assert.True(
            unregistered.Length == 0,
            "新增/改名的 XAML 窗口或控件必须在 WindowConstructionTests 里真构造一次，"
            + "或在 KnownGaps 里登记并写清原因: " + string.Join("、", unregistered));

        // 反向检查：登记过的类型真的还在，避免改名后留下过期条目
        string[] staleCovered = CoveredByOtherTests
            .Concat(KnownGaps.Keys)
            .Where(name => !declared.Contains(name, StringComparer.Ordinal))
            .ToArray();
        Assert.True(
            staleCovered.Length == 0,
            "这些登记项已经不存在了，请从守卫名单里删掉: " + string.Join("、", staleCovered));
    }

    [Fact]
    public void VideoCompressionDialog_KeepsActionsInsideWindowOutsideScroll()
    {
        RunOnStaThread(() =>
        {
            var dialog = new VideoCompressionDialog(
                new VideoCompressionSource(
                    "TEST-001",
                    "source.mp4",
                    Path.Combine(Path.GetTempPath(), "source.mp4"),
                    "h264",
                    60,
                    120L * VideoCompressionPolicy.Mebibyte),
                validatedEncoders: ["av1_nvenc"]);
            dialog.Measure(new Size(620, 500));
            dialog.Arrange(new Rect(0, 0, 620, 500));
            dialog.UpdateLayout();

            var layout = Assert.IsType<Grid>(dialog.FindName("DialogLayout"));
            var start = Assert.IsType<Button>(dialog.FindName("StartButton"));
            var codecBox = Assert.IsType<ComboBox>(
                dialog.FindName("CodecModeBox"));
            Assert.Equal("h264", codecBox.SelectedValue);
            Assert.Equal(3, codecBox.Items.Count);
            var targetSlider = Assert.IsType<Slider>(
                dialog.FindName("TargetSizeSlider"));
            var preset50 = Assert.IsType<Button>(
                dialog.FindName("TargetPreset50Button"));
            targetSlider.Value = 60;
            Assert.Equal(60, targetSlider.Value, precision: 3);
            Assert.Same(
                dialog.FindResource("PresetButtonActiveStyle"),
                preset50.Style);
            targetSlider.Value = 65;
            Assert.Equal(65, targetSlider.Value, precision: 3);
            Assert.Same(
                dialog.FindResource("PresetButtonStyle"),
                preset50.Style);
            Point bottomRight = start.TransformToAncestor(layout)
                .Transform(new Point(start.ActualWidth, start.ActualHeight));

            Assert.InRange(bottomRight.X, 0, layout.ActualWidth + 0.5);
            Assert.InRange(bottomRight.Y, 0, layout.ActualHeight + 0.5);

            DependencyObject? current = start;
            while (current != null)
            {
                Assert.IsNotType<ScrollViewer>(current);
                current = VisualTreeHelper.GetParent(current);
            }
            dialog.Close();
        });
    }

    [Fact]
    public void VideoCompressionDialog_SelectsSourceCodecAndOnlyShowsAvailableAv1()
    {
        RunOnStaThread(() =>
        {
            (
                string SourceCodec,
                bool Av1Available,
                string ExpectedCodec,
                int ExpectedItemCount)[] cases =
            [
                ("h264", true, "h264", 3),
                ("h265", false, "h265", 2),
                ("hevc", true, "h265", 3),
                ("av1", true, "av1", 3),
                ("av1", false, "h265", 2)
            ];

            foreach (var testCase in cases)
            {
                var dialog = new VideoCompressionDialog(
                    new VideoCompressionSource(
                        "TEST-CODEC",
                        "source.mp4",
                        Path.Combine(Path.GetTempPath(), "source.mp4"),
                        testCase.SourceCodec,
                        60,
                        120L * VideoCompressionPolicy.Mebibyte),
                    validatedEncoders: testCase.Av1Available
                        ? ["av1_nvenc"]
                        : []);
                var codecBox = Assert.IsType<ComboBox>(
                    dialog.FindName("CodecModeBox"));

                Assert.Equal(testCase.ExpectedCodec, codecBox.SelectedValue);
                Assert.Equal(testCase.ExpectedItemCount, codecBox.Items.Count);
                dialog.Close();
            }
        });
    }

    [Fact]
    public void StatusCard_UsesAttentionColorForPausedStates()
    {
        RunOnStaThread(() =>
        {
            foreach (string status in new[] { "已暂停", "备份暂停" })
            {
                var card = new StatusCard
                {
                    ShortStatusText = status,
                    IsAttention = status == "已暂停"
                };
                card.Measure(new Size(320, 80));
                card.Arrange(new Rect(0, 0, 320, 80));
                card.UpdateLayout();

                var text = Assert.IsType<TextBlock>(
                    card.FindName("ShortStatusTextBlock"));
                var expected = Assert.IsType<SolidColorBrush>(
                    card.FindResource("TransferAttentionText"));
                var actual = Assert.IsType<SolidColorBrush>(text.Foreground);

                Assert.Equal(expected.Color, actual.Color);
                Assert.Equal(FontWeights.SemiBold, text.FontWeight);
            }

            var localizedCard = new StatusCard
            {
                ShortStatusText = "Paused",
                IsAttention = true
            };
            localizedCard.Measure(new Size(320, 80));
            localizedCard.Arrange(new Rect(0, 0, 320, 80));
            localizedCard.UpdateLayout();

            var localizedText = Assert.IsType<TextBlock>(
                localizedCard.FindName("ShortStatusTextBlock"));
            var attention = Assert.IsType<SolidColorBrush>(
                localizedCard.FindResource("TransferAttentionText"));
            Assert.Equal(
                attention.Color,
                Assert.IsType<SolidColorBrush>(localizedText.Foreground).Color);
            Assert.Equal(FontWeights.SemiBold, localizedText.FontWeight);
        });
    }

    /// <summary>
    /// 品牌地址编辑器在真实控件树上走一遍：老地址回填、品牌路径重新拼、未知路径落到"其他"。
    /// </summary>
    [Fact]
    public void NetworkCameraAddressEditor_ComposesAndFallsBackToManual()
    {
        RunOnStaThread(() =>
        {
            var editor = new NetworkCameraAddressEditor();

            editor.Load("rtsp://admin:secret@192.168.110.108:554/cam/realmonitor?channel=1&subtype=0");
            Assert.True(editor.TryGetUrl(out string composed, out string error), error);
            Assert.Equal(
                "rtsp://admin:secret@192.168.110.108:554/cam/realmonitor?channel=1&subtype=0",
                composed);

            var channel = (TextBox)editor.FindName("ChannelTextBox");
            channel.Text = "2";
            Assert.True(editor.TryGetUrl(out string withChannel, out error), error);
            Assert.Equal(
                "rtsp://admin:secret@192.168.110.108:554/cam/realmonitor?channel=2&subtype=0",
                withChannel);

            editor.Load("rtsp://admin:secret@192.168.110.108:554/custom/path", force: true);
            Assert.True(editor.TryGetUrl(out string manual, out error), error);
            Assert.Equal("rtsp://admin:secret@192.168.110.108:554/custom/path", manual);
        });
    }

    [Fact]
    public void NetworkCameraAddressEditor_TwoWayUrlBindingRoundTrips()
    {
        RunOnStaThread(() =>
        {
            var holder = new UrlHolder
            {
                Url = "rtsp://admin:pw@192.168.1.64:554/Streaming/Channels/101"
            };
            var editor = new NetworkCameraAddressEditor();
            editor.SetBinding(
                NetworkCameraAddressEditor.UrlProperty,
                new Binding(nameof(UrlHolder.Url))
                {
                    Source = holder,
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                });

            Assert.True(editor.TryGetUrl(out string loaded, out string error), error);
            Assert.Equal(holder.Url, loaded);

            var host = (TextBox)editor.FindName("HostTextBox");
            host.Text = "10.0.0.9";
            Assert.Contains("10.0.0.9", holder.Url);
            Assert.True(editor.TryGetUrl(out string updated, out error), error);
            Assert.Equal(holder.Url, updated);
        });
    }

    private sealed class UrlHolder : INotifyPropertyChanged
    {
        private string _url = "";

        public string Url
        {
            get => _url;
            set
            {
                if (string.Equals(_url, value, StringComparison.Ordinal))
                    return;
                _url = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Url)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    [Fact]
    public void RegisteredWindowsAndControls_ConstructAndLayout()
    {
        var disposables = new List<IDisposable>();
        var failures = new List<string>();

        RunOnStaThread(() =>
        {
            foreach ((string name, Func<object> factory) in Factories())
            {
                try
                {
                    object instance = factory();
                    if (instance is IDisposable disposable) disposables.Add(disposable);
                    Layout(instance);
                }
                catch (Exception ex)
                {
                    var detail = new System.Text.StringBuilder();
                    for (Exception? current = ex; current != null; current = current.InnerException)
                        detail.Append($"{current.GetType().Name}: {current.Message} ");
                    failures.Add($"{name}（{detail.ToString().Trim()}）");
                }
            }
        });

        foreach (IDisposable disposable in disposables)
        {
            try { disposable.Dispose(); } catch { }
        }

        Assert.True(failures.Count == 0, "这些窗口/控件构造失败：" + string.Join("；", failures));
    }

    [Fact]
    public void UpdateDialogCannotSetResultAfterItCloses()
    {
        Assert.True(UpdateAvailableDialog.CanSetDialogResult(
            isShownAsDialog: true,
            isClosed: false));
        Assert.False(UpdateAvailableDialog.CanSetDialogResult(
            isShownAsDialog: false,
            isClosed: false));
        Assert.False(UpdateAvailableDialog.CanSetDialogResult(
            isShownAsDialog: true,
            isClosed: true));
    }

    [Fact]
    public void SmoothProgressBar_ShowsInitialTargetBeforeWindowLoads()
    {
        RunOnStaThread(() =>
        {
            var progress = new SmoothProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                TargetValue = 63
            };

            Assert.Equal(63, progress.Value);
        });
    }

    [Fact]
    public void TrayIconRemainsVisibleWhileMainWindowIsMinimized()
    {
        RunOnStaThread(() =>
        {
            var window = new Window();
            using var tray = new TrayIconService(window, () => { });
            Assert.True(tray.IsVisible);

            window.Show();
            tray.MinimizeToTray();

            Assert.True(tray.IsVisible);
            window.Close();
        });
    }

    [Fact]
    public void TrayIconUsesTheSmallSingleCharacterFrame()
    {
        RunOnStaThread(() =>
        {
            using System.Drawing.Icon? icon = TrayIconService.LoadTrayIcon();
            Assert.NotNull(icon);
            Assert.InRange(icon!.Width, 16, 24);
            using System.Drawing.Bitmap bitmap = icon.ToBitmap();
            Assert.Equal(0, bitmap.GetPixel(0, 0).A);
            Assert.Equal(0, bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1).A);
        });
    }

    [Fact]
    public void WorkstationSelectionFooterStaysOutsideScrollViewer()
    {
        RunOnStaThread(() =>
        {
            var window = new WorkstationSelectionWindow();
            window.Measure(new Size(860, 720));
            window.Arrange(new Rect(0, 0, 860, 720));
            window.UpdateLayout();

            var confirm = window.FindName("ConfirmPurposeButton") as DependencyObject;
            Assert.NotNull(confirm);

            DependencyObject? current = VisualTreeHelper.GetParent(confirm);
            while (current != null)
            {
                Assert.IsNotType<ScrollViewer>(current);
                current = VisualTreeHelper.GetParent(current);
            }

            window.Close();
        });
    }

    [Fact]
    public void WorkstationSelectionShowsOnlyBrandAvailableAnswers()
    {
        RunOnStaThread(() =>
        {
            var features = new ProductFeatures(
                VideoRecording: true,
                PhotoCapture: false,
                MediaLibrary: true,
                RemoteMediaAccess: false,
                LanCollaboration: false,
                ComputerBackupReceive: false,
                MobileBackupReceive: false,
                MobileApp: false,
                NasArchive: false,
                Clip: false,
                Compress: false,
                ExtensionApi: false,
                ExtensionMarket: false,
                Userscript: false,
                OrderReceiver: false,
                Tts: false,
                Inputs: new InputFeatures(true, true, true, true, true));
            var window = new WorkstationSelectionWindow(
                currentPreset: null,
                confirmCurrentPreset: false,
                hostUnavailable: false,
                availableRoles:
                [
                    ProductRoleCapabilityMapper.Get(
                        ProductRoleCapabilityMapper.RecordingHost)
                ],
                productFeatures: features);
            window.Measure(new Size(860, 720));
            window.Arrange(new Rect(0, 0, 860, 720));
            window.UpdateLayout();

            Assert.True(((RadioButton)window.FindName("RecordYesChoice")).IsEnabled);
            Assert.False(((RadioButton)window.FindName("RecordNoChoice")).IsEnabled);
            Assert.True(((RadioButton)window.FindName("StorageYesChoice")).IsEnabled);
            Assert.False(((RadioButton)window.FindName("StorageNoChoice")).IsEnabled);
            Assert.Equal(
                "录像主机",
                ((TextBlock)window.FindName("ResultTitleText")).Text);
            Assert.DoesNotContain(
                "NAS",
                ((TextBlock)window.FindName("ResultDescriptionText")).Text,
                StringComparison.Ordinal);

            window.Content = null;
        });
    }

    [Fact]
    public void LocalOnlyMainWindowHidesNetworkAndExtensionEntries()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow(enableCloseBehaviorPrompt: false);
            var viewModel = Assert.IsType<ExpressPackingMonitoring.ViewModels.MainViewModel>(
                window.DataContext);
            var features = new ProductFeatures(
                VideoRecording: true,
                PhotoCapture: false,
                MediaLibrary: true,
                RemoteMediaAccess: false,
                LanCollaboration: false,
                ComputerBackupReceive: false,
                MobileBackupReceive: false,
                MobileApp: false,
                NasArchive: false,
                Clip: false,
                Compress: false,
                ExtensionApi: false,
                ExtensionMarket: false,
                Userscript: false,
                OrderReceiver: false,
                Tts: false,
                Inputs: new InputFeatures(true, true, true, true, true));
            var effective = EffectiveProductCapabilities.Compose(
                features,
                ProductRoleCapabilityMapper.Get(
                    ProductRoleCapabilityMapper.RecordingHost),
                RuntimeEnvironment.FullyAvailable,
                new ProductCapabilityIdentity(
                    "local-capture",
                    1,
                    new string('0', 64)));
            viewModel.CapabilityOverride = SettingsCapabilities.FromEffective(
                effective,
                canChoosePurpose: false);

            window.Measure(new Size(1350, 850));
            window.Arrange(new Rect(0, 0, 1350, 850));
            window.UpdateLayout();

            Assert.Equal(
                Visibility.Collapsed,
                ((FrameworkElement)window.FindName("RecordingHostMobileBackupStatus")).Visibility);
            Assert.Equal(
                Visibility.Collapsed,
                ((FrameworkElement)window.FindName("BtnMobileConnection")).Visibility);
            Assert.Equal(
                Visibility.Collapsed,
                ((FrameworkElement)window.FindName("BtnSendTestOrder")).Visibility);
            Assert.Equal(
                Visibility.Collapsed,
                ((FrameworkElement)window.FindName("ExtensionButton")).Visibility);
            Assert.Equal(
                Visibility.Visible,
                ((FrameworkElement)window.FindName("PlaybackButton")).Visibility);

            window.Content = null;
        });
    }

    [Fact]
    public void RecordingWorkstationMainWindowShowsUserscriptInstallButton()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow(enableCloseBehaviorPrompt: false);
            var viewModel = Assert.IsType<ExpressPackingMonitoring.ViewModels.MainViewModel>(
                window.DataContext);
            var features = new ProductFeatures(
                VideoRecording: true,
                PhotoCapture: true,
                MediaLibrary: true,
                RemoteMediaAccess: false,
                LanCollaboration: true,
                ComputerBackupReceive: false,
                MobileBackupReceive: false,
                MobileApp: false,
                NasArchive: false,
                Clip: true,
                Compress: true,
                ExtensionApi: true,
                ExtensionMarket: false,
                Userscript: true,
                OrderReceiver: true,
                Tts: true,
                Inputs: new InputFeatures(true, true, true, true, true));
            var effective = EffectiveProductCapabilities.Compose(
                features,
                ProductRoleCapabilityMapper.Get(
                    ProductRoleCapabilityMapper.RecordingWorkstation),
                RuntimeEnvironment.FullyAvailable,
                new ProductCapabilityIdentity(
                    "recording-workstation",
                    1,
                    new string('0', 64)));
            viewModel.CapabilityOverride = SettingsCapabilities.FromEffective(
                effective,
                canChoosePurpose: true);

            window.Measure(new Size(1350, 850));
            window.Arrange(new Rect(0, 0, 1350, 850));
            window.UpdateLayout();

            Assert.Equal(
                Visibility.Visible,
                ((FrameworkElement)window.FindName("BtnInstallUserscript")).Visibility);
            Assert.Equal(
                Visibility.Visible,
                ((FrameworkElement)window.FindName("BtnSendTestOrder")).Visibility);

            window.Content = null;
        });
    }

    [Fact]
    public void PhotoCaptureButtonIsNonFocusableAndMatchesRecordButtonTypography()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow(enableCloseBehaviorPrompt: false);
            var viewModel = Assert.IsType<ExpressPackingMonitoring.ViewModels.MainViewModel>(
                window.DataContext);
            var features = new ProductFeatures(
                VideoRecording: true,
                PhotoCapture: true,
                MediaLibrary: true,
                RemoteMediaAccess: false,
                LanCollaboration: false,
                ComputerBackupReceive: false,
                MobileBackupReceive: false,
                MobileApp: false,
                NasArchive: false,
                Clip: false,
                Compress: false,
                ExtensionApi: false,
                ExtensionMarket: false,
                Userscript: false,
                OrderReceiver: false,
                Tts: false,
                Inputs: new InputFeatures(true, true, true, true, true));
            EffectiveProductCapabilities effective = EffectiveProductCapabilities.Compose(
                features,
                ProductRoleCapabilityMapper.Get(ProductRoleCapabilityMapper.RecordingHost),
                RuntimeEnvironment.FullyAvailable,
                new ProductCapabilityIdentity(
                    "photo-button",
                    1,
                    new string('0', 64)));
            viewModel.CapabilityOverride = SettingsCapabilities.FromEffective(
                effective,
                canChoosePurpose: false);

            var photoButton = Assert.IsType<Button>(
                window.FindName("PhotoCaptureButton"));
            var recordButton = Assert.IsType<Button>(
                window.FindName("RecordButton"));
            viewModel.SetPhotoCaptureButtonVisibility(false);
            Assert.Equal(Visibility.Collapsed, photoButton.Visibility);

            var rightClick = new MouseButtonEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                MouseButton.Right)
            {
                RoutedEvent = UIElement.MouseRightButtonUpEvent
            };
            recordButton.RaiseEvent(rightClick);
            Assert.True(rightClick.Handled);
            Assert.Equal(Visibility.Visible, photoButton.Visibility);
            Assert.True(viewModel.Config.ShowPhotoCaptureButton);

            rightClick = new MouseButtonEventArgs(
                Mouse.PrimaryDevice,
                Environment.TickCount,
                MouseButton.Right)
            {
                RoutedEvent = UIElement.MouseRightButtonUpEvent
            };
            recordButton.RaiseEvent(rightClick);
            Assert.Equal(Visibility.Collapsed, photoButton.Visibility);
            Assert.False(viewModel.Config.ShowPhotoCaptureButton);
            Assert.DoesNotContain(
                "ShowPhotoCaptureButton",
                File.ReadAllText(Path.Combine(
                    FindRepositoryRoot(),
                    "src", "ExpressPackingMonitoring", "UI", "SettingsWindow.xaml")),
                StringComparison.Ordinal);

            Assert.False(photoButton.Focusable);
            Assert.Equal(recordButton.FontSize, photoButton.FontSize);
            Assert.Equal(recordButton.FontWeight, photoButton.FontWeight);
            window.Content = null;
        });
    }

    [Fact]
    public void PhotoOnlyProductStillExposesThePhotoEntry()
    {
        RunOnStaThread(() =>
        {
            var window = new MainWindow(enableCloseBehaviorPrompt: false);
            var viewModel = Assert.IsType<ExpressPackingMonitoring.ViewModels.MainViewModel>(
                window.DataContext);
            var features = new ProductFeatures(
                VideoRecording: false,
                PhotoCapture: true,
                MediaLibrary: true,
                RemoteMediaAccess: false,
                LanCollaboration: false,
                ComputerBackupReceive: false,
                MobileBackupReceive: false,
                MobileApp: false,
                NasArchive: false,
                Clip: false,
                Compress: false,
                ExtensionApi: false,
                ExtensionMarket: false,
                Userscript: false,
                OrderReceiver: false,
                Tts: false,
                Inputs: new InputFeatures(true, false, true, true, false));
            EffectiveProductCapabilities effective = EffectiveProductCapabilities.Compose(
                features,
                ProductRoleCapabilityMapper.Get(ProductRoleCapabilityMapper.RecordingHost),
                RuntimeEnvironment.FullyAvailable,
                new ProductCapabilityIdentity(
                    "photo-only",
                    1,
                    new string('0', 64)));
            viewModel.CapabilityOverride = SettingsCapabilities.FromEffective(
                effective,
                canChoosePurpose: false);
            viewModel.SetPhotoCaptureButtonVisibility(false);

            Assert.False(viewModel.Capabilities.CanOperateVideo);
            Assert.True(viewModel.Capabilities.CanOperatePhoto);
            Assert.Equal(
                Visibility.Collapsed,
                ((FrameworkElement)window.FindName("RecordButton")).Visibility);
            var photoButton = Assert.IsType<Button>(window.FindName("PhotoCaptureButton"));
            Assert.Equal(Visibility.Visible, photoButton.Visibility);
            Assert.True(photoButton.IsEnabled);
            window.Content = null;
        });
    }

    [Fact]
    public void StatisticsWindowFocusCleanup_ClearsOwnerButtonKeyboardFocus()
    {
        RunOnStaThread(() =>
        {
            var owner = new Window { Width = 640, Height = 480 };
            var statsButton = new Button { Content = "统计" };
            owner.Content = statsButton;
            owner.Show();
            try
            {
                statsButton.Focus();
                Keyboard.Focus(statsButton);
                Dispatcher.CurrentDispatcher.Invoke(
                    () => { },
                    DispatcherPriority.Input);
                Assert.Same(
                    statsButton,
                    FocusManager.GetFocusedElement(owner));

                StatisticsWindow.ClearOwnerKeyboardFocus(owner);
                Dispatcher.CurrentDispatcher.Invoke(
                    () => { },
                    DispatcherPriority.ApplicationIdle);

                Assert.NotSame(
                    statsButton,
                    FocusManager.GetFocusedElement(owner));
            }
            finally
            {
                owner.Close();
            }
        });
    }

    /// <summary>真构造的窗口与控件清单（与守卫的名单一一对应）。</summary>
    private static IEnumerable<(string Name, Func<object> Factory)> Factories()
    {
        yield return (
            "ExpressPackingMonitoring.UI.MainWindow",
            () => new MainWindow(enableCloseBehaviorPrompt: false));
        yield return (
            "ExpressPackingMonitoring.UI.StatisticsWindow",
            () => new StatisticsWindow(new VideoDatabase(":memory:")));
        yield return (
            "ExpressPackingMonitoring.UI.BackupDeviceEnrollmentApprovalWindow",
            () => new BackupDeviceEnrollmentApprovalWindow(new BackupDeviceEnrollmentRequest
            {
                DeviceId = "test-device",
                DeviceName = "测试手机",
                DeviceKind = "mobile",
                RemoteAddress = "192.168.1.10"
            }));
        yield return (
            "ExpressPackingMonitoring.UI.CameraBarcodeUpgradeDialog",
            () => new CameraBarcodeUpgradeDialog());
        yield return (
            "ExpressPackingMonitoring.UI.CloseBehaviorDialog",
            () => new CloseBehaviorDialog());
        yield return (
            "ExpressPackingMonitoring.UI.ConfirmDialog",
            () => new ConfirmDialog("测试消息", "测试标题"));
        yield return (
            "ExpressPackingMonitoring.UI.ExtensionEnrollmentApprovalWindow",
            () => new ExtensionEnrollmentApprovalWindow(
                new ExtensionEnrollmentRequest
                {
                    ExtensionInstanceId = "test-extension",
                    ProviderId = "test-provider",
                    DisplayName = "测试扩展",
                    Version = "1.0.0",
                    RequestedPermissions = ["storage.read"]
                },
                localOriginNodeId: "local-node",
                localOriginNodeName: "本机"));
        yield return (
            "ExpressPackingMonitoring.UI.ExtensionMarketWindow",
            () => new ExtensionMarketWindow());
        yield return (
            "ExpressPackingMonitoring.UI.ManualCleanupDialog",
            () => new ManualCleanupDialog(ManualCleanupKind.ByTime));
        yield return (
            "ExpressPackingMonitoring.UI.MobileAppUpdatePromptWindow",
            () => new MobileAppUpdatePromptWindow("测试提示", () => { }));
        yield return (
            "ExpressPackingMonitoring.UI.MobileConnectionWindow",
            () => new MobileConnectionWindow("http://127.0.0.1:5280/?key=test", accessProtected: true));
        yield return (
            "ExpressPackingMonitoring.UI.ModeTransitionButton",
            () => new ModeTransitionButton());
        yield return (
            "ExpressPackingMonitoring.UI.OrderNumberExportProgressDialog",
            () => new OrderNumberExportProgressDialog(
                new VideoDatabase(":memory:"),
                new OrderNumberExportFilter(null, null),
                Path.Combine(Path.GetTempPath(), "订单号导出.csv")));
        yield return (
            "ExpressPackingMonitoring.UI.RecognitionSampleExportProgressDialog",
            () => new RecognitionSampleExportProgressDialog(
                Path.GetTempPath(),
                Guid.NewGuid().ToString()));
        yield return (
            "ExpressPackingMonitoring.UI.StoragePathSelectionDialog",
            () => new StoragePathSelectionDialog(Path.Combine(Path.GetTempPath(), "PackingProofVideos")));
        yield return (
            "ExpressPackingMonitoring.UI.StorageReserveDialog",
            () => new StorageReserveDialog(new StorageLocation
            {
                Path = Path.Combine(Path.GetTempPath(), "PackingProofVideos"),
                Priority = 0
            }));
        yield return (
            "ExpressPackingMonitoring.UI.Controls.InputClearButton",
            () => new InputClearButton());
        yield return (
            "ExpressPackingMonitoring.UI.Controls.InputPasteButton",
            () => new InputPasteButton());
        yield return (
            "ExpressPackingMonitoring.UI.Controls.StatusCard",
            () => new StatusCard());
        yield return (
            "ExpressPackingMonitoring.UI.Controls.NetworkCameraAddressEditor",
            () => new NetworkCameraAddressEditor());
        yield return (
            "ExpressPackingMonitoring.UI.TrayKeyboardListeningDialog",
            () => new TrayKeyboardListeningDialog());
        yield return (
            "ExpressPackingMonitoring.UI.TransferManagementWindow",
            () => new TransferManagementWindow(
                new TransferManagementSnapshotService(
                    TransferManagementMode.HostDevices,
                    new AppConfig(),
                    new VideoDatabase(":memory:"),
                    webServer: null,
                    queueStore: null,
                    transferService: null)));
        yield return (
            "ExpressPackingMonitoring.UI.UpdateAvailableDialog",
            () => new UpdateAvailableDialog(new UpdateCheckResult
            {
                HasUpdate = true,
                LatestVersion = "0.0.99",
                Title = "测试更新",
                Body = "测试说明",
                DownloadUrl = "https://example.invalid/download"
            }));
        yield return (
            "ExpressPackingMonitoring.UI.VideoCompressionDialog",
            () => new VideoCompressionDialog(
                new VideoCompressionSource(
                    "TEST-001",
                    "source.mp4",
                    Path.Combine(Path.GetTempPath(), "source.mp4"),
                    "h264",
                    60,
                    120L * VideoCompressionPolicy.Mebibyte)));
        yield return (
            "ExpressPackingMonitoring.UI.VideoImportDialog",
            () => new VideoImportDialog(
                new VideoFolderImportService(
                    new VideoDatabase(":memory:"),
                    [Path.GetTempPath()],
                    sourceDeviceId: "test-device",
                    sourceDeviceName: "测试设备"),
                Path.GetTempPath(),
                Path.GetTempPath()));
        yield return (
            "ExpressPackingMonitoring.ManualHostConnectionWindow",
            () => new ManualHostConnectionWindow(requiresCompleteLink: false));
        yield return (
            "ExpressPackingMonitoring.WorkstationSelectionWindow",
            () => new WorkstationSelectionWindow());
    }

    /// <summary>走完一次布局：模板展开、资源键解析与内容赋值都在这一步发生。</summary>
    private static void Layout(object instance)
    {
        if (instance is Window window)
        {
            window.Measure(new Size(1200, 900));
            window.Arrange(new Rect(0, 0, 1200, 900));
            window.UpdateLayout();
            window.Close();
            return;
        }

        if (instance is FrameworkElement element)
        {
            element.Measure(new Size(1200, 900));
            element.Arrange(new Rect(0, 0, 1200, 900));
            element.UpdateLayout();
        }
    }

    private static string[] DeclaredXamlClasses()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        return Directory.GetFiles(projectPath, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => Regex
                .Matches(File.ReadAllText(path), "x:Class=\"([^\"]+)\"")
                .Select(match => match.Groups[1].Value))
            .Where(name => !string.Equals(name, AppXamlClass, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>WPF 控件必须在 STA 线程上创建，测试宿主默认 MTA。</summary>
    private static void RunOnStaThread(Action action)
    {
        WpfTestHost.Run(() =>
        {
            action();
        }, TimeSpan.FromSeconds(120), "STA 线程执行超时");
    }

    /// <summary>与 App.xaml 相同的合并顺序，否则窗口里的资源键解析不到。</summary>
    private static void LoadAppResources()
    {
        string[] files =
        [
            "ColorTokens.xaml", "LightTheme.xaml", "ComboBoxTheme.xaml", "DatePickerTheme.xaml",
            "SpinBoxTheme.xaml", "TextBoxTheme.xaml", "ButtonTheme.xaml", "ScrollBarTheme.xaml",
            "FluentIcons.xaml", "SliderTheme.xaml", "MenuTheme.xaml"
        ];

        var merged = new ResourceDictionary();
        foreach (string file in files)
        {
            merged.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/ExpressPackingMonitoring;component/themes/{file.ToLowerInvariant()}",
                    UriKind.Absolute)
            });
        }

        if (Application.Current != null)
            Application.Current.Resources = merged;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
