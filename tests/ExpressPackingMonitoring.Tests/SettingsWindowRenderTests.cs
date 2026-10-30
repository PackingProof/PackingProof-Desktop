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
    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // pack:// 方案要有 Application 实例才注册得上；
                // 测试宿主不是 WPF 程序，这里自己建一个。
                if (Application.Current == null)
                    _ = new Application();
                LoadAppResources();
                action();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA 线程执行超时");
        if (failure != null)
        {
            var detail = new System.Text.StringBuilder();
            for (Exception? current = failure; current != null; current = current.InnerException)
                detail.AppendLine($"{current.GetType().Name}: {current.Message}");
            detail.AppendLine(failure.StackTrace);
            throw new Xunit.Sdk.XunitException($"设置窗口构造失败：{detail}");
        }
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
                window.Measure(new Size(1200, 900));
                window.Arrange(new Rect(0, 0, 1200, 900));
                window.UpdateLayout();

                // 预留只能从磁盘右键改：保存位置与备份位置的行样式都要挂上"设置预留空间…"菜单
                AssertStorageReserveMenu(Assert.IsType<DataGrid>(window.FindName("StorageDataGrid")));
                AssertStorageReserveMenu(Assert.IsType<DataGrid>(window.FindName("BackupStorageDataGrid")));

                window.Close();
            }
            finally
            {
                CultureInfo.CurrentUICulture = original;
            }
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
