using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.UI.Controls;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 英文界面截图：默认跳过，设置 PACKINGPROOF_UI_SHOTS=1 时在测试进程里离屏渲染各窗口，
/// 产物写到 artifacts/ui-shots/en，供人工核对翻译和布局溢出。
/// </summary>
[Collection("WPF render tests")]
public sealed class UiScreenshotTests
{
    private static bool _localizationReady;
    private static readonly List<Window> Windows = [];

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("PACKINGPROOF_UI_SHOTS") == "1";

    [Fact]
    public void CaptureEnglishScreens()
    {
        if (!Enabled) return;

        string language = Environment.GetEnvironmentVariable("PACKINGPROOF_UI_SHOTS_LANG") ?? AppLanguage.English;
        string folder = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh"
            : language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
                ? "ja"
                : "en";
        string outputDirectory = Path.Combine(FindRepositoryRoot(), "artifacts", "ui-shots", folder);
        Directory.CreateDirectory(outputDirectory);

        RunOnStaThread(() =>
        {
            AppLanguage.Initialize(language);
            if (!_localizationReady)
            {
                WpfLocalization.EnableAutomaticWpfLocalization();
                _localizationReady = true;
            }

            try
            {
                try
                {
                    CaptureSettings(outputDirectory);
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(outputDirectory, "settings.error.txt"), ex.ToString());
                }

                if (Environment.GetEnvironmentVariable("PACKINGPROOF_UI_SHOTS_STANDALONE") == "1")
                    CaptureStandalone(outputDirectory);
            }
            finally
            {
                // 截图结束自己收尾：先断掉 HwndSource，再关闭所有窗口。
                foreach (System.Windows.Interop.HwndSource source in Sources)
                    source.Dispose();
                Sources.Clear();

                // Close() 在测试进程里可能卡住消息循环；直接断开内容即可，
                // HwndSource 已经在上一步销毁，不会留下可见窗口。
                foreach (Window window in Windows)
                {
                    try { window.Content = null; }
                    catch { }
                }
                Windows.Clear();
            }
        });
    }

    private static void CaptureSettings(string outputDirectory)
    {
        // 先构造一次但不挂 HwndSource：WPF 在测试宿主里需要先完成一次窗口初始化，
        // 后续截图窗口才能稳定解析应用级资源。
        _ = CreateSettingsWindow(out _, out _);
        var window = CreateSettingsWindow(out _, out TabControl? tabs);
        if (tabs == null)
            return;

        AttachToSource(window);
        TabItem[] visibleTabs = tabs.Items.OfType<TabItem>()
            .Where(item => item.Visibility == Visibility.Visible)
            .ToArray();
        for (int index = 0; index < visibleTabs.Length; index++)
        {
            try
            {
                tabs.SelectedItem = visibleTabs[index];
                Pump(window);
                Save(window, Path.Combine(outputDirectory, $"settings-{index:00}.png"));
                ScrollViewer? scroller = FindScrollableContent(window);
                if (scroller is { ScrollableHeight: > 0 })
                {
                    scroller.ScrollToEnd();
                    Pump(window);
                    Save(window, Path.Combine(outputDirectory, $"settings-{index:00}-bottom.png"));
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(
                    Path.Combine(outputDirectory, $"settings-{index:00}.error.txt"),
                    ex.ToString());
            }
        }
    }

    private static SettingsWindow CreateSettingsWindow(out AppConfig config, out TabControl? tabs)
    {
        config = new AppConfig
        {
            DeploymentPreset = DeploymentPresets.RecordingWorkstation,
            StorageLocations =
            [
                new StorageLocation { Path = Path.Combine(Path.GetTempPath(), "PackingProofVideos"), Priority = 0 }
            ]
        };
        AppConfig.NormalizeAfterLoad(config);

        var context = new SettingsContext
        {
            Capabilities = SettingsCapabilities.ForPreset(config.DeploymentPreset),
            ApplyAsync = _ => Task.FromResult(true)
        };

        var window = new SettingsWindow(context, config, 12d, "12%");
        Windows.Add(window);
        tabs = window.FindName("SettingsTabControl") as TabControl;
        return window;
    }

    private static void CaptureStandalone(string outputDirectory)
    {
        CaptureTransferManagement(outputDirectory);
        CaptureViewerHostSelection(outputDirectory);
        TryCapture("statistics", outputDirectory, () => new StatisticsWindow(new VideoDatabase(":memory:")));
        TryCapture("workstation-selection", outputDirectory, () => new WorkstationSelectionWindow());
        TryCapture(
            "manual-host-connection",
            outputDirectory,
            () => new ManualHostConnectionWindow(requiresCompleteLink: false));
        TryCapture(
            "close-behavior",
            outputDirectory,
            () => new CloseBehaviorDialog());
        TryCapture(
            "camera-barcode-upgrade",
            outputDirectory,
            () => new CameraBarcodeUpgradeDialog());
        TryCapture(
            "mobile-connection",
            outputDirectory,
            () => new MobileConnectionWindow(
                "http://192.168.1.250:5280/?key=1a8cb7fe",
                accessProtected: true));
        TryCapture(
            "playback",
            outputDirectory,
            () => new PlaybackWindow(
                Path.GetTempPath(),
                null,
                true,
                "本机",
                webClipUrlProvider: _ => Task.FromResult<string?>("http://127.0.0.1:5280/"))
            {
                Width = 1440,
                Height = 760
            });
        TryCapture(
            "playback-narrow",
            outputDirectory,
            () => new PlaybackWindow(
                Path.GetTempPath(),
                null,
                true,
                "本机",
                webClipUrlProvider: _ => Task.FromResult<string?>("http://127.0.0.1:5280/"))
            {
                Width = 920,
                Height = 700
            });
        TryCapture(
            "screenshot-dialog",
            outputDirectory,
            () => new PlaybackScreenshotDialog(CreateScreenshotDialogPreview(640, 360))
            {
                SizeToContent = SizeToContent.Manual,
                Height = 420
            });
        TryCapture(
            "screenshot-dialog-portrait",
            outputDirectory,
            () => new PlaybackScreenshotDialog(CreateScreenshotDialogPreview(360, 640))
            {
                SizeToContent = SizeToContent.Manual,
                Height = 420
            });
        TryCapture(
            "video-compression-dialog",
            outputDirectory,
            () =>
            {
                var dialog = new VideoCompressionDialog(
                    new VideoCompressionSource(
                        "SF1234567890",
                        "source.mp4",
                        Path.Combine(Path.GetTempPath(), "source.mp4"),
                        "h264",
                        95,
                        286L * VideoCompressionPolicy.Mebibyte));
                if (dialog.FindName("TargetSizeSlider") is Slider targetSize)
                    targetSize.Value = 143;
                return dialog;
            });
        // 扩展市场构造时会去拉远端目录，截图时容易被网络重试卡住，先跳过。
    }

    private static void CaptureViewerHostSelection(string outputDirectory)
    {
        TryCapture(
            "viewer-host-selected",
            outputDirectory,
            () =>
            {
                var window = new ViewerClientWindow(
                    new AppConfig
                    {
                        DeploymentPreset = DeploymentPresets.RecordingWorkstation
                    },
                    DeploymentPresets.RecordingWorkstation);
                var hosts = Assert.IsType<ListBox>(window.FindName("HostsList"));
                hosts.ItemsSource = new[]
                {
                    new PackingProofNodeInfo
                    {
                        NodeName = "Macmini",
                        Address = "http://192.168.31.26:5280"
                    }
                };
                hosts.SelectedIndex = 0;
                hosts.IsEnabled = false;
                return window;
            });
    }

    private static void CaptureTransferManagement(string outputDirectory)
    {
        TryCapture(
            "transfer-host-compact",
            outputDirectory,
            () =>
            {
                var window = CreateTransferManagementWindow(
                    TransferManagementMode.HostDevices);
                var viewModel = (TransferManagementWindowViewModel)window.DataContext;
                DateTimeOffset now = DateTimeOffset.Now;
                viewModel.ApplySnapshotForPreview(new TransferManagementSnapshot(
                    HostConnectionState.Online,
                    38,
                    0,
                    2,
                    null,
                    null,
                    null,
                    null,
                    [],
                    0,
                    null,
                    [
                        new HostDeviceRow(
                            "phone-2",
                            "手机2",
                            false,
                            100,
                            now.AddMinutes(-2),
                            DeviceAttentionKind.Offline),
                        new HostDeviceRow(
                            "pc-1",
                            "电脑1",
                            false,
                            1,
                            now.AddMinutes(-4),
                            DeviceAttentionKind.Offline),
                        new HostDeviceRow(
                            "phone-1",
                            "手机1",
                            true,
                            10000,
                            now,
                            DeviceAttentionKind.None),
                        new HostDeviceRow(
                            "pc-2",
                            "电脑2",
                            true,
                            10,
                            now.AddMinutes(-2),
                            DeviceAttentionKind.None),
                        new HostDeviceRow(
                            "phone-3",
                            "手机3",
                            true,
                            100,
                            now.AddMinutes(-8),
                            DeviceAttentionKind.None),
                        new HostDeviceRow(
                            "phone-4",
                            "手机4",
                            true,
                            1,
                            now.AddMinutes(-15),
                            DeviceAttentionKind.None),
                        new HostDeviceRow(
                            "pc-3",
                            "电脑3",
                            true,
                            0,
                            now.AddHours(-1),
                            DeviceAttentionKind.None)
                    ]));
                return window;
            });

        TryCapture(
            "transfer-slave-compact",
            outputDirectory,
            () =>
            {
                var window = CreateTransferManagementWindow(
                    TransferManagementMode.RecordingTransfer,
                    isUploadPaused: () => false,
                    toggleUploadPause: () => Task.FromResult(true));
                var viewModel = (TransferManagementWindowViewModel)window.DataContext;
                DateTimeOffset now = DateTimeOffset.Now;
                viewModel.ApplySnapshotForPreview(new TransferManagementSnapshot(
                    HostConnectionState.StorageBlocked,
                    162,
                    50000,
                    2,
                    new TransferTaskRow(
                        1,
                        "AUTO_WEB_0142",
                        1264 * 1024 * 1024,
                        795 * 1024 * 1024,
                        ""),
                    null,
                    null,
                    null,
                    [
                        new TransferTaskRow(
                            2,
                            "AUTO_WEB_0138",
                            1264 * 1024 * 1024,
                            0,
                            "保存主机空间不足，上传已暂停"),
                        new TransferTaskRow(
                            3,
                            "AUTO_WEB_0131",
                            92 * 1024 * 1024,
                            0,
                            "保存主机连接中断，稍后自动继续")
                    ],
                    0,
                    now.AddSeconds(10),
                    []));
                return window;
            });

        TryCapture(
            "transfer-slave-paused",
            outputDirectory,
            () =>
            {
                var window = CreateTransferManagementWindow(
                    TransferManagementMode.RecordingTransfer,
                    isUploadPaused: () => true,
                    toggleUploadPause: () => Task.FromResult(true));
                var viewModel = (TransferManagementWindowViewModel)window.DataContext;
                viewModel.ApplySnapshotForPreview(new TransferManagementSnapshot(
                    HostConnectionState.PausedByUser,
                    0,
                    728,
                    0,
                    null,
                    new TransferTaskRow(
                        4,
                        "AUTO_WEB_0140",
                        624 * 1024 * 1024,
                        100 * 1024 * 1024,
                        ""),
                    null,
                    null,
                    [],
                    0,
                    null,
                    []));
                return window;
            });

        TryCapture(
            "transfer-slave-pending",
            outputDirectory,
            () =>
            {
                var window = CreateTransferManagementWindow(
                    TransferManagementMode.RecordingTransfer,
                    isUploadPaused: () => false,
                    toggleUploadPause: () => Task.FromResult(true));
                var viewModel = (TransferManagementWindowViewModel)window.DataContext;
                DateTimeOffset now = DateTimeOffset.Now;
                viewModel.ApplySnapshotForPreview(new TransferManagementSnapshot(
                    HostConnectionState.Online,
                    0,
                    707,
                    0,
                    null,
                    null,
                    new TransferTaskRow(
                        5,
                        "AUTO_WEB_0139",
                        384 * 1024 * 1024,
                        0,
                        ""),
                    now.AddSeconds(15),
                    [],
                    0,
                    null,
                    []));
                return window;
            });

        TryCapture(
            "transfer-slave-paused-queued",
            outputDirectory,
            () =>
            {
                var window = CreateTransferManagementWindow(
                    TransferManagementMode.RecordingTransfer,
                    isUploadPaused: () => true,
                    toggleUploadPause: () => Task.FromResult(true));
                var viewModel = (TransferManagementWindowViewModel)window.DataContext;
                viewModel.ApplySnapshotForPreview(new TransferManagementSnapshot(
                    HostConnectionState.PausedByUser,
                    219,
                    657,
                    0,
                    null,
                    null,
                    new TransferTaskRow(
                        6,
                        "AUTO_WEB_0137",
                        276 * 1024 * 1024,
                        0,
                        ""),
                    null,
                    [],
                    0,
                    null,
                    []));
                return window;
            });

        TryCapture(
            "transfer-slave-checking",
            outputDirectory,
            () =>
            {
                var window = CreateTransferManagementWindow(
                    TransferManagementMode.RecordingTransfer,
                    isUploadPaused: () => false,
                    toggleUploadPause: () => Task.FromResult(true));
                var viewModel = (TransferManagementWindowViewModel)window.DataContext;
                viewModel.ApplySnapshotForPreview(new TransferManagementSnapshot(
                    HostConnectionState.Unknown,
                    129,
                    599,
                    0,
                    null,
                    null,
                    new TransferTaskRow(
                        7,
                        "AUTO_WEB_0136",
                        414 * 1024 * 1024,
                        0,
                        ""),
                    null,
                    [],
                    0,
                    null,
                    []));
                return window;
            });
    }

    private static TransferManagementWindow CreateTransferManagementWindow(
        TransferManagementMode mode,
        Func<bool>? isUploadPaused = null,
        Func<Task<bool>>? toggleUploadPause = null) =>
        new(new TransferManagementSnapshotService(
            mode,
            new AppConfig(),
            new VideoDatabase(":memory:"),
            webServer: null,
            queueStore: null,
            transferService: null),
            isUploadPaused,
            toggleUploadPause);

    private static BitmapSource CreateScreenshotDialogPreview(int width, int height)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            var background = new LinearGradientBrush(
                Color.FromRgb(25, 36, 55),
                Color.FromRgb(51, 72, 103),
                new Point(0, 0),
                new Point(1, 1));
            context.DrawRectangle(background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                null,
                new Rect(width * 0.12, height * 0.16, width * 0.76, height * 0.68));
            context.DrawRectangle(
                new SolidColorBrush(Color.FromRgb(59, 130, 246)),
                null,
                new Rect(width * 0.18, height * 0.26, width * 0.30, 12));
            context.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                null,
                new Rect(width * 0.18, height * 0.36, width * 0.48, 8));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void TryCapture(string name, string outputDirectory, Func<Window> factory)
    {
        Window? window = null;
        try
        {
            window = factory();
            Windows.Add(window);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            ApplyContentHeight(window);
            AttachToSource(window);
            if (window is TransferManagementWindow)
            {
                if (window.FindName("CurrentUploadProgress") is not SmoothProgressBar progress)
                {
                    throw new InvalidOperationException("找不到当前上传进度条");
                }
                if (window.DataContext is TransferManagementWindowViewModel
                    { UploadSlot.Progress: > 0 } viewModel)
                {
                    progress.ApplyTemplate();
                    progress.SetCurrentValue(
                        System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                        viewModel.UploadSlot.Progress);
                    progress.UpdateLayout();
                    if (progress.Value <= 0)
                        throw new InvalidOperationException("当前上传进度条没有显示目标值");
                    if (progress.IsIndeterminate)
                        throw new InvalidOperationException("当前上传进度条错误地处于不确定状态");
                    if (progress.Style != Application.Current?.TryFindResource("RoundedSmoothProgressBar"))
                        throw new InvalidOperationException("当前上传进度条没有使用共享圆角样式");
                    if (progress.Template.FindName("PART_Track", progress)
                        is not FrameworkElement track
                        || track.ActualHeight < 5)
                    {
                        throw new InvalidOperationException("当前上传进度条共享模板没有正确布局");
                    }
                    if (progress.Template.FindName("PART_Indicator", progress)
                        is not FrameworkElement indicator
                        || indicator.ActualWidth <= 0
                        || indicator.ActualHeight < 5)
                    {
                        throw new InvalidOperationException("当前上传进度条没有渲染填充轨道");
                    }
                    if (indicator.Opacity <= 0)
                        throw new InvalidOperationException("当前上传进度条填充轨道透明");
                    if (indicator is not Border
                        {
                            Background: SolidColorBrush indicatorBrush
                        }
                        || indicatorBrush.Color != Color.FromRgb(0x3B, 0x82, 0xF6))
                    {
                        throw new InvalidOperationException(
                            $"当前上传进度条填充色不正确：{indicator}");
                    }
                }
            }
            Save(window, Path.Combine(outputDirectory, $"{name}.png"));
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                Path.Combine(outputDirectory, $"{name}.error.txt"),
                ex.ToString());
            throw;
        }
        finally
        {
            // 不 Close：关闭窗口会让测试进程里的 Application 进入关闭流程，
            // 后续窗口就再也加载不了资源了。
        }
    }

    private static readonly List<System.Windows.Interop.HwndSource> Sources = [];

    private static void ApplyContentHeight(Window window)
    {
        if (window.SizeToContent != SizeToContent.Height
            || window.Content is not FrameworkElement content)
        {
            return;
        }

        double width = window.Width > 0 ? window.Width : 456;
        content.Measure(new Size(width, double.PositiveInfinity));
        window.SizeToContent = SizeToContent.Manual;
        window.Height = Math.Max(
            window.MinHeight,
            Math.Ceiling(content.DesiredSize.Height) + 80);
    }

    private static void AttachToSource(Window window)
    {
        double width = window.Width > 0 ? window.Width : 1000;
        double height = window.Height > 0 ? window.Height : 800;

        if (window.Content is not Visual content)
            return;

        window.Foreground = Brushes.Black;
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        window.UpdateLayout();
        if (content is FrameworkElement contentElement)
        {
            contentElement.Measure(new Size(width, height));
            contentElement.Arrange(new Rect(0, 0, width, height));
            contentElement.UpdateLayout();
        }
        WpfLocalization.LocalizeTree(content);
        try
        {
            Pump(window);
        }
        catch
        {
            DumpInvalidForeground(content);
            throw;
        }
    }

    /// <summary>布局失败时把解析不到 Foreground 的元素找出来，方便定位缺失的资源。</summary>
    private static void DumpInvalidForeground(DependencyObject root)
    {
        var report = new System.Text.StringBuilder();
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        Walk(root);

        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "packingproof-ui-foreground.txt"),
            report.ToString());

        void Walk(DependencyObject node)
        {
            if (!visited.Add(node)) return;

            try
            {
                if (node is TextBlock textBlock)
                    _ = textBlock.Foreground;
                else if (node is Run run)
                    _ = run.Foreground;
            }
            catch (Exception ex)
            {
                string name = node is FrameworkElement element ? element.Name : "";
                string text = node is TextBlock textBlockElement
                    ? textBlockElement.Text
                    : (node as Run)?.Text ?? "";
                report.AppendLine($"{node.GetType().Name} Name={name} Text={text}");
                report.AppendLine($"  {ex.Message}");
            }

            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject dependencyObject)
                    Walk(dependencyObject);
            }
        }
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        // 截图线程没有启动消息循环，Dispatcher.Invoke 会在等不到 Loaded 时永久阻塞。
        // 这里只需要完成布局；本地化由 LocalizeTree 显式执行。
        window.UpdateLayout();
    }

    private static void PumpAnimations(Window window, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            DispatcherPriority.Background,
            window.Dispatcher)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        if (window.Content is not FrameworkElement content) return;
        content.UpdateLayout();
        int width = (int)Math.Ceiling(content.ActualWidth > 0 ? content.ActualWidth : window.Width);
        int height = (int)Math.Ceiling(content.ActualHeight > 0 ? content.ActualHeight : window.Height);
        if (width <= 0 || height <= 0)
            return;

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>找出当前设置页里可滚动的主内容区，用于补一张页底截图。</summary>
    private static ScrollViewer? FindScrollableContent(Window window)
    {
        if (window.Content is not DependencyObject root)
            return null;

        ScrollViewer? best = null;
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        Walk(root);
        return best;

        void Walk(DependencyObject node)
        {
            if (!visited.Add(node)) return;
            if (node is ScrollViewer scroller
                && scroller.ScrollableHeight > 0
                && (best == null || scroller.ScrollableHeight > best.ScrollableHeight))
            {
                best = scroller;
            }

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int index = 0; index < count; index++)
                Walk(VisualTreeHelper.GetChild(node, index));
        }
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var traceListener = new System.Diagnostics.TextWriterTraceListener(
            Path.Combine(Path.GetTempPath(), "packingproof-ui-trace.log"));
        var thread = new Thread(() =>
        {
            try
            {
                System.Diagnostics.PresentationTraceSources.Refresh();
                System.Diagnostics.PresentationTraceSources.ResourceDictionarySource.Switch.Level =
                    System.Diagnostics.SourceLevels.All;
                System.Diagnostics.PresentationTraceSources.ResourceDictionarySource.Listeners.Add(traceListener);

                bool created = Application.Current == null;
                if (created)
                {
                    var app = new ExpressPackingMonitoring.App();
                    app.InitializeComponent();
                }
                Application.Current!.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                object? textPrimary = Application.Current?.TryFindResource("TextPrimary");
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "packingproof-ui-diag.txt"),
                    $"created={created} resources={Application.Current?.Resources?.MergedDictionaries.Count} " +
                    $"textPrimary={textPrimary?.GetType().Name}");
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45)))
        {
            // 测试进程卡死时不能让窗口留在桌面上：直接结束自己。
            try { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
            catch { }
            throw new Xunit.Sdk.XunitException("界面截图超时，已终止测试进程");
        }
        traceListener.Flush();
        traceListener.Close();
        if (failure != null)
            throw new Xunit.Sdk.XunitException("界面截图失败：" + failure);
    }

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

        // 测试进程没有正常启动 Application，TextBlock 的默认 Foreground 可能解析成
        // UnsetValue；补一个隐式样式兜底，显式设置过颜色的控件不受影响。
        var textBlockStyle = new Style(typeof(TextBlock));
        textBlockStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.Black));
        merged[typeof(TextBlock)] = textBlockStyle;

        if (Application.Current != null)
            Application.Current.Resources = merged;
    }

    private static string FindRepositoryRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ExpressPackingMonitoring.sln")))
                return current;
            current = Path.GetDirectoryName(current);
        }
        return AppContext.BaseDirectory;
    }

}
