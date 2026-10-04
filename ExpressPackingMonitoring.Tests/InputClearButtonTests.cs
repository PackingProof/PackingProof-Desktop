using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ExpressPackingMonitoring.UI.Controls;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 输入框右侧的清除叉号必须是同一个公共控件。
/// 三处各抄一份按钮模板时，改一处外观就会漏掉另外两处，
/// 所以这里同时守住"都用了这个控件"和"都接上了目标输入框"。
/// </summary>
public sealed class InputClearButtonTests
{
    [Fact]
    public void ScanInputClearButton_IsTheSharedControlBoundToScanBox()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));

        Assert.Contains("<controls:InputClearButton", xaml, StringComparison.Ordinal);
        Assert.Contains("Target=\"{Binding ElementName=ScanInputTextBox}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackSearchClearButton_IsTheSharedControlBoundToSearchBox()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "PlaybackWindow.xaml"));

        Assert.Contains("<controls:InputClearButton", xaml, StringComparison.Ordinal);
        Assert.Contains("Target=\"{Binding ElementName=SearchBox}\"", xaml, StringComparison.Ordinal);
        // 抄在页面里的那份按钮与点击处理必须已经删掉，避免留下第二套清除逻辑
        Assert.DoesNotContain("BtnClearSearch_Click", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsSearchClearButton_IsTheSharedControlBoundToSearchBox()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "SettingsWindow.xaml"));
        Assert.Contains("<controls:InputClearButton", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SettingsSearchClearButton\"", xaml, StringComparison.Ordinal);

        // 搜索框在 TabControl 的模板里，拿不到 ElementName 绑定，Target 由接线代码接上
        string wiring = ReadProjectFile(Path.Combine("UI", "SettingsWindow.Search.cs"));
        Assert.Contains("SettingsSearchClearButton", wiring, StringComparison.Ordinal);
        Assert.Contains("clearButton.Target = box;", wiring, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedControl_ClearsTheTargetTextBox()
    {
        string code = ReadProjectFile(Path.Combine("UI", "Controls", "InputClearButton.xaml.cs"));

        Assert.Contains("typeof(TextBox)", code, StringComparison.Ordinal);
        Assert.Contains("target.Text = string.Empty;", code, StringComparison.Ordinal);
        // 清空后仍要走输入框自己的 TextChanged：过滤/占位/去抖都挂在那上面
        Assert.Contains("target.Focus();", code, StringComparison.Ordinal);
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
/// 公共清除叉号必须真的按调用方给的尺寸渲染出来。
/// 尺寸与圆角都靠模板里的绑定传下去，绑定写错不会抛异常、只会悄悄退化，所以这里量一次实际值。
/// </summary>
[Collection("WPF render tests")]
public sealed class InputClearButtonRenderTests
{
    [Fact]
    public void RendersAtTheRequestedSize_AndClearsTheTarget()
    {
        RunOnStaThread(() =>
        {
            LoadAppResources();

            var box = new TextBox { Text = "SF1234567890" };
            var button = new InputClearButton
            {
                Target = box,
                ButtonSize = 22,
                IconSize = 12,
                ButtonCornerRadius = new CornerRadius(11)
            };
            button.Measure(new Size(120, 60));
            button.Arrange(new Rect(0, 0, 120, 60));
            button.UpdateLayout();

            Assert.Equal(22, button.ActualWidth, 1);
            Assert.Equal(22, button.ActualHeight, 1);

            var chrome = button.ClickHost.Template.FindName("Chrome", button.ClickHost) as Border;
            Assert.NotNull(chrome);
            Assert.Equal(new CornerRadius(11), chrome!.CornerRadius);
            var icon = chrome.Child as System.Windows.Shapes.Path;
            Assert.NotNull(icon);
            Assert.Equal(12, icon!.ActualWidth, 1);

            button.ClickHost.RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent, button.ClickHost));

            Assert.Equal(string.Empty, box.Text);
        });
    }

    /// <summary>与 App.xaml 一致的合并顺序，模板里的 StaticResource 才解析得到。</summary>
    private static void LoadAppResources()
    {
        var merged = new ResourceDictionary();
        foreach (string file in new[]
                 {
                     "ColorTokens.xaml", "LightTheme.xaml", "TextBoxTheme.xaml",
                     "ButtonTheme.xaml", "FluentIcons.xaml"
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

    /// <summary>WPF 控件必须在 STA 线程上创建，测试宿主默认 MTA。</summary>
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA 线程执行超时");
        if (failure != null)
        {
            var detail = new System.Text.StringBuilder();
            for (Exception? current = failure; current != null; current = current.InnerException)
                detail.AppendLine($"{current.GetType().Name}: {current.Message}");
            throw new Xunit.Sdk.XunitException($"清除叉号渲染失败：{detail}");
        }
    }
}
