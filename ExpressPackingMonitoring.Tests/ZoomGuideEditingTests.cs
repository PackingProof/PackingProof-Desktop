using System.Text;
using System.Windows;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.ViewModels;
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
