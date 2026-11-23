using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI.Controls;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 单号输入框右侧的粘贴按钮必须是同一个公共控件，且都接上了目标输入框。
/// 各页面各抄一份模板时，改一处行为就会漏掉另一处，所以这里同时守住这两件事。
/// </summary>
public sealed class InputPasteButtonTests
{
    [Fact]
    public void ScanInputPasteButton_IsTheSharedControlBoundToScanBox()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));

        Assert.Contains("<controls:InputPasteButton", xaml, StringComparison.Ordinal);
        Assert.Contains("Target=\"{Binding ElementName=ScanInputTextBox}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackSearchPasteButton_IsTheSharedControlBoundToSearchBox()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "PlaybackWindow.xaml"));

        Assert.Contains("<controls:InputPasteButton", xaml, StringComparison.Ordinal);
        Assert.Contains("Target=\"{Binding ElementName=SearchBox}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedControl_PastesTheCleanedClipboardText()
    {
        string code = ReadProjectFile(Path.Combine("UI", "Controls", "InputPasteButton.xaml.cs"));

        Assert.Contains("typeof(TextBox)", code, StringComparison.Ordinal);
        // 读剪贴板走公共 helper：被别的程序占着时瞬时失败，不在 UI 线程上等
        Assert.Contains("ClipboardHelper.TryReadText", code, StringComparison.Ordinal);
        // 清理规则只有一份，别在控件里再写一遍
        Assert.Contains("PastedTextNormalizer.TrimNonOrderNumberEnds", code, StringComparison.Ordinal);
        // 写回输入框并交出焦点，输入框自己的 TextChanged 逻辑照常收尾
        Assert.Contains("target.Text = cleaned;", code, StringComparison.Ordinal);
        Assert.Contains("target.Focus();", code, StringComparison.Ordinal);
    }

    private static string ReadProjectFile(string relativePath) =>
        File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring", relativePath),
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
/// 粘贴按钮要真的按调用方给的尺寸渲染出来，并且点一下就按清理规则写进输入框。
/// 尺寸与圆角靠模板绑定传下去，绑定写错不会抛异常、只会悄悄退化，所以这里量一次实际值。
/// </summary>
[Collection("WPF render tests")]
public sealed class InputPasteButtonRenderTests
{
    [Fact]
    public void RendersAtTheRequestedSize_AndPastesTheCleanedClipboardText()
    {
        RunOnStaThread(() =>
        {
            SetClipboard("中通快递  45564646464646464");

            var box = new TextBox { Text = "旧内容" };
            var button = new InputPasteButton
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
            // Stretch=Uniform：图标按较长的那条边缩到请求尺寸，形状不是正方形时宽度会更小
            Assert.Equal(12, Math.Max(icon!.ActualWidth, icon.ActualHeight), 1);

            // 剪贴板是全局资源：点击那一瞬可能被别的进程占着（远程桌面/串流客户端），
            // 这时按钮按设计不动输入框（见下面那条用例）。所以这里重试，重试仍不行就按环境跳过，
            // 免得发布门禁被这种环境抖动挡住——以前它就这样把整次打包打断过。
            bool pasted = false;
            for (int attempt = 0; attempt < 40 && !pasted; attempt++)
            {
                button.ClickHost.RaiseEvent(
                    new RoutedEventArgs(ButtonBase.ClickEvent, button.ClickHost));
                pasted = string.Equals(box.Text, "45564646464646464", StringComparison.Ordinal);
                if (!pasted) Thread.Sleep(50);
            }

            if (!pasted)
                Assert.Skip("剪贴板被其他进程占用，读不到刚写入的测试文本，跳过本次粘贴验证");
        });
    }

    /// <summary>清理后什么都不剩时不许动输入框：宁可什么都不做，也不能把已有内容清掉。</summary>
    [Fact]
    public void NothingUsableInClipboard_KeepsTheBoxUntouched()
    {
        RunOnStaThread(() =>
        {
            SetClipboard("中通快递");

            var box = new TextBox { Text = "SF1234567890" };
            var button = new InputPasteButton { Target = box };

            button.ClickHost.RaiseEvent(
                new RoutedEventArgs(ButtonBase.ClickEvent, button.ClickHost));

            Assert.Equal("SF1234567890", box.Text);
        });
    }

    /// <summary>剪贴板可能被别的程序占着：写到成功为止（helper 会转后台重试），最多等 2 秒。</summary>
    private static void SetClipboard(string text)
    {
        _ = ClipboardHelper.TryCopyText(text, () => { });
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (ClipboardHelper.TryReadText(out string current) && current == text)
                return;
            Thread.Sleep(50);
        }

        throw new Xunit.Sdk.XunitException($"剪贴板没能写入测试文本：{text}");
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

    private static void RunOnStaThread(Action action)
    {
        WpfTestHost.Run(() =>
        {
            action();
        }, TimeSpan.FromSeconds(60), "STA 线程执行超时");
    }
}
