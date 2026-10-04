using System.Windows.Controls;
using System.Text;
using System.Windows;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.ViewModels;
using ExpressPackingMonitoring.Config;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 放大取景框：主画面上一块独立于识别框的区域，录制触发时按它放大主画面。
/// 这一组守卫盯住三件事——位置只认这个框（不看识别结果）、编辑态能无遮挡地摆框、
/// 编辑期间放大与副画面合成都要让位。
/// </summary>
public sealed class ZoomGuideEditingTests
{
    [Fact]
    public void ZoomBranch_NoLongerUsesRecognizedBarcodeGeometry()
    {
        string camera = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Camera.cs"));

        Assert.Contains("ZoomCropPolicy.ResolveScale", camera, StringComparison.Ordinal);
        Assert.Contains("ZoomGuideGeometry", camera, StringComparison.Ordinal);
        Assert.Contains("ZoomCropPolicy.CreateCropRect", camera, StringComparison.Ordinal);
        // 智能放大那套坐标已经删干净：放大分支不得再引用识别到的条码几何
        Assert.DoesNotContain("_lastBarcodeGeometry", camera, StringComparison.Ordinal);
        Assert.DoesNotContain("SmartZoomPolicy", camera, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoomGuideEditing_SuspendsZoomAndHidesRecognitionGuide()
    {
        string guide = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.CameraGuide.cs"));

        Assert.Contains("public bool IsEditingZoomGuide", guide, StringComparison.Ordinal);
        Assert.Contains("IsEditingZoomGuide", guide, StringComparison.Ordinal);
        Assert.Contains("internal void EnterZoomGuideEdit()", guide, StringComparison.Ordinal);
        Assert.Contains("internal void ExitZoomGuideEdit()", guide, StringComparison.Ordinal);
        // 编辑屏里框一定可拖，且写回的是放大取景框那一组配置
        Assert.Contains("ApplyZoomGuideGeometry", guide, StringComparison.Ordinal);
        Assert.Contains("Config.ZoomGuideWidthRatio", guide, StringComparison.Ordinal);
        // 暂停放大用同一个策略入口，编辑态不能漏
        Assert.Contains("IsEditingZoomGuide);", guide, StringComparison.Ordinal);
        Assert.Contains("nameof(IsCameraBarcodeGuideLockVisible)", guide, StringComparison.Ordinal);
        Assert.Contains("nameof(IsCameraBarcodeGuideEditable)", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoomGuideEditing_SuppressesOverlayComposition()
    {
        string channels = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.OverlayChannels.cs"));

        // 副画面小窗会挡住主画面，摆放大位置时先不合成；退出后自动恢复
        Assert.Contains("if (IsEditingZoomGuide)", channels, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ShowsZoomGuideBoxAndHidesItWhileEditing()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));

        Assert.Contains("x:Name=\"ZoomGuideBoxHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ZoomGuideBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{StaticResource ZoomGuideStroke}", xaml, StringComparison.Ordinal);
        // 完成按钮两种预览编辑态都要显示
        Assert.Contains("{Binding IsPreviewGuideEditing}", xaml, StringComparison.Ordinal);
        // 编辑态下这一屏显示的是放大取景框
        Assert.Contains("{Binding IsEditingZoomGuide}", xaml, StringComparison.Ordinal);

        string code = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml.cs"));
        Assert.Contains("UpdateZoomGuideBox", code, StringComparison.Ordinal);
        Assert.Contains("!vm.IsEditingZoomGuide", code, StringComparison.Ordinal);
        Assert.Contains("ExitPreviewGuideEditing", code, StringComparison.Ordinal);
        // Esc 与"完成"等效，两种编辑态都要能退出
        Assert.Contains("vm.IsEditingOverlayPreview || vm.IsEditingZoomGuide", code, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayDragBoxes_AreHiddenWhileEditingAnyPreviewGuide()
    {
        string boxes = ReadProjectFile(Path.Combine("UI", "MainWindow.OverlayBoxes.cs"));

        Assert.Contains("if (vm.IsPreviewGuideEditing)", boxes, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoomGuideColors_AreDeclaredAsTokens()
    {
        string tokens = ReadProjectFile(Path.Combine("Themes", "ColorTokens.xaml"));

        Assert.Contains("x:Key=\"ZoomGuideStroke\"", tokens, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ZoomGuideFill\"", tokens, StringComparison.Ordinal);
    }

    /// <summary>
    /// 设置页的入口与交接：有主画面才显示"调整放大位置"，点它先应用再关窗口，
    /// 框选完成自动回到"面单放大"那一栏。
    /// </summary>
    [Fact]
    public void SettingsPage_ProvidesZoomGuideEntryForMainPreviewHostsOnly()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "SettingsWindow.xaml"));
        Assert.Contains("x:Name=\"ZoomTabItem\"", xaml, StringComparison.Ordinal);
        Assert.Contains("调整放大位置…", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ShowZoomGuideBoxCheckBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding Config.ShowZoomGuideBox}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding CanAdjustZoomGuide", xaml, StringComparison.Ordinal);
        // 智能那套文案不得残留
        Assert.DoesNotContain("面单智能特写", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("识别面单位置后自动平移特写", xaml, StringComparison.Ordinal);

        string wiring = ReadProjectFile(Path.Combine("UI", "SettingsWindow.ZoomGuide.cs"));
        Assert.Contains("CanAdjustZoomGuide", wiring, StringComparison.Ordinal);
        Assert.Contains("await SaveAndApplyAsync()", wiring, StringComparison.Ordinal);
        Assert.Contains("RequestZoomGuideEdit", wiring, StringComparison.Ordinal);
        Assert.Contains("public void SelectZoomTab()", wiring, StringComparison.Ordinal);

        string context = ReadProjectFile(Path.Combine("UI", "SettingsContext.cs"));
        Assert.Contains("RequestZoomGuideEdit = mainViewModel.RequestZoomGuideEdit", context, StringComparison.Ordinal);

        string viewModel = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Settings.cs"));
        Assert.Contains("_zoomGuideEditRequested", viewModel, StringComparison.Ordinal);
        Assert.Contains("EnterZoomGuideEdit();", viewModel, StringComparison.Ordinal);
        Assert.Contains("settingsWin.SelectZoomTab();", viewModel, StringComparison.Ordinal);

        // 打印工位没有主画面：不给这个 action，入口自然隐藏
        string printWorkstation = ReadProjectFile(Path.Combine("Workstations", "PrintWorkstationWindow.xaml.cs"));
        Assert.DoesNotContain("RequestZoomGuideEdit", printWorkstation, StringComparison.Ordinal);

        // 退出放大框编辑后自动回设置页
        string window = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml.cs"));
        Assert.Contains("vm.OpenZoomGuideSettings()", window, StringComparison.Ordinal);
    }

    private static string ReadProjectFile(string relativePath) =>
        File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring", relativePath),
            Encoding.UTF8);

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }
}

/// <summary>
/// 进放大取景框编辑态的真实窗口行为：框要切到"放大取景框"几何、小锁要让开、
/// 提示要换成"拖动框调整放大位置"，退出后恢复到主摄识别框那一套。
/// </summary>
[Collection("WPF render tests")]
public sealed class ZoomGuideEditingRenderTests
{
    [Fact]
    public void EnteringZoomGuideEdit_SwitchesGuideBoxAndHidesLock()
    {
        RunOnStaThread(() =>
        {
            LoadAppResources();

            var window = new MainWindow(enableCloseBehaviorPrompt: false);
            window.Measure(new Size(1400, 900));
            window.Arrange(new Rect(0, 0, 1400, 900));
            window.UpdateLayout();

            var vm = Assert.IsType<MainViewModel>(window.DataContext);
            Assert.False(vm.IsEditingZoomGuide);
            Assert.True(vm.IsCameraBarcodeGuideLockVisible);

            vm.EnterZoomGuideEdit();
            window.UpdateLayout();

            Assert.True(vm.IsEditingZoomGuide);
            Assert.True(vm.IsPreviewGuideEditing);
            Assert.False(vm.IsCameraBarcodeGuideLockVisible);
            Assert.Equal(vm.ZoomGuideGeometry, vm.CurrentCameraBarcodeGuideGeometry);
            Assert.Contains("放大位置", vm.CameraBarcodeStatusText, StringComparison.Ordinal);
            Assert.False(vm.IsZoomingActive);

            vm.ExitZoomGuideEdit();
            window.UpdateLayout();

            Assert.False(vm.IsEditingZoomGuide);
            Assert.True(vm.IsCameraBarcodeGuideLockVisible);
            window.Close();
        });
    }

    /// <summary>
    /// "调整放大位置"这一行只在有主画面的宿主里出现：打印工位的上下文没有这个 action，
    /// 入口必须收起来，点了也没有主画面可去。
    /// </summary>
    [Fact]
    public void SettingsPage_ShowsZoomGuideRowOnlyWithMainPreviewHost()
    {
        RunOnStaThread(() =>
        {
            LoadAppResources();

            var withMainPreview = new SettingsWindow(
                new MainViewModel(),
                new AppConfig { DeploymentPreset = DeploymentPresets.RecordingWorkstation },
                12d,
                "12%");
            withMainPreview.SelectZoomTab();
            Layout(withMainPreview);
            var withMainRow = FindZoomGuideRow(withMainPreview);
            Assert.Equal(Visibility.Visible, withMainRow.Visibility);
            Assert.True(withMainPreview.CanAdjustZoomGuide);
            withMainPreview.Close();

            // 打印/查看端那类宿主只给一份最小上下文：没有 RequestZoomGuideEdit
            var noMainPreviewContext = new SettingsContext
            {
                Capabilities = SettingsCapabilities.ForPreset(DeploymentPresets.ViewerClient),
                ApplyAsync = _ => Task.FromResult(true)
            };
            var withoutMainPreview = new SettingsWindow(
                noMainPreviewContext,
                new AppConfig { DeploymentPreset = DeploymentPresets.ViewerClient },
                12d,
                "12%");
            Layout(withoutMainPreview);
            var withoutMainRow = FindZoomGuideRow(withoutMainPreview);
            // 这栏本身对查看端是隐藏的（CanRecordPcVideo=false），所以这里断言驱动显隐的属性
            Assert.False(withoutMainPreview.CanAdjustZoomGuide);
            withoutMainPreview.Close();
        });
    }

    private static Grid FindZoomGuideRow(SettingsWindow window)
    {
        var button = Assert.IsType<Button>(window.FindName("BtnAdjustZoomGuide"));
        return Assert.IsType<Grid>(button.Parent);
    }

    private static void Layout(Window window)
    {
        window.Measure(new Size(1200, 900));
        window.Arrange(new Rect(0, 0, 1200, 900));
        window.UpdateLayout();
    }

    /// <summary>与 App.xaml 一致的合并顺序，模板里的 StaticResource 才解析得到。</summary>
    private static void LoadAppResources()
    {
        var merged = new ResourceDictionary();
        foreach (string file in new[]
                 {
                     "ColorTokens.xaml", "LightTheme.xaml", "ComboBoxTheme.xaml", "DatePickerTheme.xaml",
                     "SpinBoxTheme.xaml", "TextBoxTheme.xaml", "ButtonTheme.xaml", "ScrollBarTheme.xaml",
                     "FluentIcons.xaml", "SliderTheme.xaml", "MenuTheme.xaml"
                 })
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

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current == null)
                    _ = new Application();
                action();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "STA 线程执行超时");
        if (failure != null)
        {
            var detail = new System.Text.StringBuilder();
            for (Exception? current = failure; current != null; current = current.InnerException)
                detail.AppendLine($"{current.GetType().Name}: {current.Message}");
            throw new Xunit.Sdk.XunitException($"放大取景框编辑态渲染失败：{detail}");
        }
    }
}
