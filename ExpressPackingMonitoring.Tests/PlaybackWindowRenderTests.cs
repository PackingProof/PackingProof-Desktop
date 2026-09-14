using System.Windows;
using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 回放窗口必须能真正构造并走完一次布局。
///
/// 顶栏改成筛选按钮后出现过"打开回放窗口失败"，而编译与纯文本守卫都发现不了：
/// 模板展开、资源键解析、构造函数里访问模板内元素这些错误
/// 只在真正创建窗口的那一刻才抛。
/// </summary>
[Collection("WPF render tests")]
public sealed class PlaybackWindowRenderTests
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
            throw new Xunit.Sdk.XunitException($"回放窗口构造失败：{detail}");
        }
    }

    /// <summary>与 App.xaml 相同的合并顺序，否则窗口里的资源键解析不到。</summary>
    private static void LoadAppResources()
    {
        string[] files =
        [
            "ColorTokens.xaml", "LightTheme.xaml", "ComboBoxTheme.xaml", "DatePickerTheme.xaml",
            "SpinBoxTheme.xaml", "TextBoxTheme.xaml", "ButtonTheme.xaml", "ScrollBarTheme.xaml",
            "FluentIcons.xaml", "MenuTheme.xaml"
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

    /// <summary>
    /// 没有数据库时也要能打开：来源下拉、筛选角标都不能因为数据库为空而崩。
    /// </summary>
    [Fact]
    public void Constructor_WithoutDatabase_DoesNotThrow()
    {
        RunOnStaThread(() =>
        {
            var window = new PlaybackWindow(Path.GetTempPath(), null, true, "本机");
            window.Measure(new Size(1400, 900));
            window.Arrange(new Rect(0, 0, 1400, 900));
            window.UpdateLayout();
        });
    }
}
