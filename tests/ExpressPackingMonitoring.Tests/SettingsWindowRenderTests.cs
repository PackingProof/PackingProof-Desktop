using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.UI.Controls;
using System.Globalization;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 设置窗口必须能真正构造并走完一次布局。
///
/// 存储表格从"就地编辑容量上限"改成"右键设置预留空间"之后出现过
/// "设置错误: 设置 connectionId 时引发了异常"：编译与纯文本守卫都看不出问题，
/// 只有真正创建窗口的那一刻才抛。
/// </summary>
[Collection("WPF render tests")]
public sealed class SettingsWindowRenderTests
{
    /// <summary>WPF 控件必须在 STA 线程上创建，测试宿主默认 MTA。</summary>
    private static void RunOnStaThread(Action action) =>
        WpfTestHost.Run(action, TimeSpan.FromSeconds(60), "STA 线程执行超时");

    [Fact]
    public void Constructor_WithStorageAndBackupLocations_DoesNotThrow()
    {
        RunOnStaThread(() =>
        {
            CultureInfo original = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
                var config = new AppConfig
                {
                    DeploymentPreset = DeploymentPresets.RecordingWorkstation,
                    StorageLocations =
                    [
                        new StorageLocation { Path = Path.Combine(Path.GetTempPath(), "PackingProofVideos"), Priority = 0 },
                        new StorageLocation
                        {
                            Path = @"\\192.168.1.249\打包视频",
                            Priority = 1,
                            IsBackupTarget = true
                        }
                    ]
                };
                AppConfig.NormalizeAfterLoad(config);

                var context = new SettingsContext
                {
                    Capabilities = SettingsCapabilities.ForPreset(config.DeploymentPreset),
                    ApplyAsync = _ => Task.FromResult(true)
                };

                var window = new SettingsWindow(context, config, 12d, "12%");
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -20000;
                window.Top = -20000;
                window.ShowInTaskbar = false;
                window.Show();
                window.Measure(new Size(1200, 900));
                window.Arrange(new Rect(0, 0, 1200, 900));
                window.UpdateLayout();

                // 预留只能从磁盘右键改：保存位置与备份位置的行样式都要挂上"设置预留空间…"菜单
                AssertStorageReserveMenu(Assert.IsType<DataGrid>(window.FindName("StorageDataGrid")));
                AssertStorageReserveMenu(Assert.IsType<DataGrid>(window.FindName("BackupStorageDataGrid")));
                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<Border>(window.FindName("BackupStorageCard")).Visibility);

                window.Close();
            }
            finally
            {
                CultureInfo.CurrentUICulture = original;
            }
        });
    }

    [Fact]
    public void SingleAvailablePurposeHidesPurposeSelector()
    {
        RunOnStaThread(() =>
        {
            var config = new AppConfig
            {
                DeploymentPreset = DeploymentPresets.RecordingHost
            };
            var context = new SettingsContext
            {
                Capabilities = SettingsCapabilities.ForPreset(
                    DeploymentPresets.RecordingHost),
                AvailablePurposes =
                [
                    new DeploymentPurposeOption(
                        DeploymentPresets.RecordingHost,
                        DeploymentPresets.GetDisplayName(
                            DeploymentPresets.RecordingHost))
                ],
                ApplyAsync = _ => Task.FromResult(true)
            };
            var window = new SettingsWindow(context, config, 12d, "12%");
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.ShowInTaskbar = false;
            window.Show();
            window.Measure(new Size(1200, 900));
            window.Arrange(new Rect(0, 0, 1200, 900));
            window.UpdateLayout();

            Assert.Equal(
                Visibility.Collapsed,
                Assert.IsType<Grid>(window.FindName("PurposeRow")).Visibility);
            Assert.Equal(
                Visibility.Visible,
                Assert.IsType<Border>(window.FindName("BackupStorageCard")).Visibility);
            window.Content = null;
        });
    }

    /// <summary>
    /// 「识别二维码」必须挂在配置项上并保留能力门控。未显示的窗口里页签内容不会求值，
    /// 所以这里校验绑定本身——绑定路径写错时 XAML 不会报错，只有检查绑定才抓得到。
    /// </summary>
    [Fact]
    public void Constructor_QrCodeSwitchIsBoundToConfigAndStaysGated()
    {
        RunOnStaThread(() =>
        {
            var config = new AppConfig
            {
                DeploymentPreset = DeploymentPresets.RecordingWorkstation,
                EnableCameraBarcodeRecognition = true
            };
            AppConfig.NormalizeAfterLoad(config);

            var context = new SettingsContext
            {
                Capabilities = SettingsCapabilities.ForPreset(config.DeploymentPreset),
                ApplyAsync = _ => Task.FromResult(true)
            };

            var window = new SettingsWindow(context, config, 12d, "12%");
            window.Measure(new Size(1200, 900));
            window.Arrange(new Rect(0, 0, 1200, 900));
            window.UpdateLayout();

            var checkBox = Assert.IsType<CheckBox>(window.FindName("CameraBarcodeQrCodeCheckBox"));
            Assert.Equal(
                "Config.EnableCameraBarcodeQrCode",
                BindingOperations.GetBinding(checkBox, ToggleButton.IsCheckedProperty)?.Path.Path);

            var row = Assert.IsType<Grid>(window.FindName("CameraBarcodeQrCodeRow"));
            // 门控不能被摘掉：录像主机/从机之外的工位不该看到这个开关
            Assert.Equal(
                "Capabilities.CanUseCameraBarcode",
                BindingOperations.GetBinding(row, UIElement.VisibilityProperty)?.Path.Path);
            Assert.Null(window.FindName("CameraVisionModelPathBox"));

            window.Close();
        });
    }

    [Fact]
    public void Constructor_EnhancedRecognitionSwitchIsBoundAndGated()
    {
        RunOnStaThread(() =>
        {
            var config = new AppConfig
            {
                DeploymentPreset = DeploymentPresets.RecordingWorkstation,
                EnableCameraBarcodeRecognition = true
            };
            AppConfig.NormalizeAfterLoad(config);
            var context = new SettingsContext
            {
                Capabilities = SettingsCapabilities.ForPreset(config.DeploymentPreset),
                ApplyAsync = _ => Task.FromResult(true)
            };

            var window = new SettingsWindow(context, config, 12d, "12%");
            window.Measure(new Size(1200, 900));
            window.Arrange(new Rect(0, 0, 1200, 900));
            window.UpdateLayout();

            var checkBox = Assert.IsType<CheckBox>(
                window.FindName("CameraBarcodeEnhancedRecognitionCheckBox"));
            Assert.Equal(
                "Config.EnableCameraBarcodeEnhancedRecognition",
                BindingOperations.GetBinding(checkBox, ToggleButton.IsCheckedProperty)?.Path.Path);

            var row = Assert.IsType<Grid>(
                window.FindName("CameraBarcodeEnhancedRecognitionRow"));
            Assert.Equal(
                "Capabilities.CanUseVisionObb",
                BindingOperations.GetBinding(row, UIElement.VisibilityProperty)?.Path.Path);
            Assert.Equal(
                nameof(SettingsWindow.CanUseEnhancedRecognitionForSelectedBarcodeSource),
                BindingOperations.GetBinding(row, UIElement.IsEnabledProperty)?.Path.Path);

            window.Close();
        });
    }

    [Fact]
    public void Constructor_RecognitionSampleExportControlsAreAvailable()
    {
        RunOnStaThread(() =>
        {
            var config = new AppConfig
            {
                DeploymentPreset = DeploymentPresets.RecordingWorkstation
            };
            AppConfig.NormalizeAfterLoad(config);
            var context = new SettingsContext
            {
                Capabilities = SettingsCapabilities.ForPreset(config.DeploymentPreset),
                ApplyAsync = _ => Task.FromResult(true)
            };

            var window = new SettingsWindow(context, config, 12d, "12%");
            window.Measure(new Size(1200, 900));
            window.Arrange(new Rect(0, 0, 1200, 900));
            window.UpdateLayout();

            var button = Assert.IsType<Button>(window.FindName("ExportRecognitionSamplesButton"));
            Assert.Equal(AppLanguage.Get("导出样本包"), button.Content);

            window.Close();
        });
    }

    [Fact]
    public void Constructor_RecognitionSampleProgressDialogIsAvailable()
    {
        RunOnStaThread(() =>
        {
            var dialog = new RecognitionSampleExportProgressDialog(
                Path.GetTempPath(),
                Guid.NewGuid().ToString());
            dialog.Measure(new Size(520, 320));
            dialog.Arrange(new Rect(0, 0, 520, 320));
            dialog.UpdateLayout();

            Assert.Equal(AppLanguage.Get("正在导出样本包"), dialog.Title);
            Assert.IsType<SmoothProgressBar>(
                dialog.FindName("ExportProgressBar"));
            Assert.IsType<Button>(dialog.FindName("CancelExportButton"));
            dialog.Content = null;
        });
    }

    [Fact]
    public void Constructor_NetworkCameraStreamCopyRowUsesStandardSettingsSpacing()
    {
        RunOnStaThread(() =>
        {
            var config = new AppConfig
            {
                DeploymentPreset = DeploymentPresets.RecordingWorkstation,
                CameraSourceKind = "network",
                NetworkCameraUrl = "rtsp://192.0.2.10/live"
            };
            AppConfig.NormalizeAfterLoad(config);
            var context = new SettingsContext
            {
                Capabilities = SettingsCapabilities.ForPreset(config.DeploymentPreset),
                ApplyAsync = _ => Task.FromResult(true)
            };

            var window = new SettingsWindow(context, config, 12d, "12%");
            window.Measure(new Size(1200, 900));
            window.Arrange(new Rect(0, 0, 1200, 900));
            window.UpdateLayout();

            var row = Assert.IsType<Grid>(
                window.FindName("NetworkCameraStreamCopyRow"));
            Assert.Same(window.FindResource("SettingRowStyle"), row.Style);
            Assert.Equal(
                (Thickness)window.FindResource("SettingRowMargin"),
                row.Margin);

            window.Close();
        });
    }

    [Fact]
    public void NetworkCameraStreamCopy_KeepsSubCameraSettingsAndUsesStandardNoticeSpacing()
    {
        RunOnStaThread(() =>
        {
            var config = new AppConfig
            {
                DeploymentPreset = DeploymentPresets.RecordingWorkstation,
                CameraSourceKind = "network",
                NetworkCameraUrl = "rtsp://192.0.2.10/live",
                EnableNetworkCameraStreamCopy = true
            };
            AppConfig.NormalizeAfterLoad(config);
            var context = new SettingsContext
            {
                Capabilities = SettingsCapabilities.ForPreset(config.DeploymentPreset),
                ApplyAsync = _ => Task.FromResult(true)
            };

            var window = new SettingsWindow(context, config, 12d, "12%");
            window.Measure(new Size(1200, 900));
            window.Arrange(new Rect(0, 0, 1200, 900));
            window.UpdateLayout();

            var overlayPanel = Assert.IsType<ItemsControl>(
                window.FindName("OverlayCameraChannelsPanel"));
            var overlayCountRow = Assert.IsType<Grid>(
                window.FindName("OverlayChannelCountRow"));
            var notice = Assert.IsType<Border>(
                window.FindName("OverlayChannelStreamCopyNotice"));
            Assert.True(overlayPanel.IsEnabled);
            Assert.True(overlayCountRow.IsEnabled);
            Assert.Equal(Visibility.Visible, notice.Visibility);
            Assert.Same(window.FindResource("SectionCardStyle"), notice.Style);
            Assert.Equal(
                (Thickness)window.FindResource("SectionCardMargin"),
                notice.Margin);

            window.Close();
        });
    }

    /// <summary>行样式里的右键菜单必须是"设置预留空间…"。</summary>
    private static void AssertStorageReserveMenu(DataGrid grid)
    {
        Assert.NotNull(grid.RowStyle);
        Setter? setter = grid.RowStyle!.Setters
            .OfType<Setter>()
            .FirstOrDefault(candidate => candidate.Property == FrameworkElement.ContextMenuProperty);
        Assert.NotNull(setter);
        var menu = Assert.IsType<ContextMenu>(setter!.Value);
        var item = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
        Assert.Equal(AppLanguage.Get("设置预留空间…"), item.Header);
        // 右键菜单必须走应用统一的菜单外观，否则会退回系统默认样式
        Assert.NotNull(menu.Style);
        Assert.Same(Application.Current?.TryFindResource("AppContextMenuStyle"), menu.Style);
        Assert.NotNull(item.Style);
        Assert.Same(Application.Current?.TryFindResource("AppMenuItemStyle"), item.Style);
    }
}
