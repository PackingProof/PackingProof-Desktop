using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 菜单主题必须能真正渲染，而不只是"文件里有这个键"。
///
/// 这里加载的是编译进程序集的 BAML 并强制走一次布局，因为
/// Setter 的属性解析、模板展开这些错误只在渲染那一刻才抛：
/// 例如给 CLR 属性 Resources 写 Setter 会抛 ArgumentNullException(property)，
/// 编译和纯文本守卫都发现不了，直到用户右键才崩。
/// </summary>
[Collection("WPF render tests")]
public sealed class MenuThemeRenderTests
{
    /// <summary>
    /// WPF 控件必须在 STA 线程上创建，测试宿主默认 MTA。
    /// 异常带回主线程断言，避免测试假通过。
    /// </summary>
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
            throw new Xunit.Sdk.XunitException($"菜单主题渲染失败：{detail}");
        }
    }

    /// <summary>
    /// 用与 App.xaml 相同的方式合并：让 MenuTheme 作为
    /// MergedDictionaries 的一员加载，才能解析到 FluentIcons 里的键。
    /// </summary>
    private static ResourceDictionary LoadMergedTheme()
    {
        var merged = new ResourceDictionary();
        foreach (string file in new[] { "ColorTokens.xaml", "LightTheme.xaml", "FluentIcons.xaml", "MenuTheme.xaml" })
        {
            merged.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/ExpressPackingMonitoring;component/themes/{file.ToLowerInvariant()}",
                    UriKind.Absolute)
            });
        }

        // 应用级资源必须挂上去，模板里的 DynamicResource 才有得查。
        if (Application.Current != null)
            Application.Current.Resources = merged;
        return merged;
    }

    /// <summary>
    /// 真正套上样式并强制布局：这一步会展开 ControlTemplate 并解析
    /// 里面所有 StaticResource，是能复现"右键就崩"的最小场景。
    /// </summary>
    [Fact]
    public void ContextMenuWithStyledItems_RendersWithoutThrowing()
    {
        RunOnStaThread(() =>
        {
            ResourceDictionary theme = LoadMergedTheme();

            var menu = new ContextMenu { Style = (Style)theme["AppContextMenuStyle"] };
            menu.Resources.MergedDictionaries.Add(theme);
            menu.Items.Add(new MenuItem
            {
                Header = "复制单号",
                Style = (Style)theme["AppMenuItemStyle"]
            });
            menu.Items.Add(new Separator { Style = (Style)theme["AppSeparatorStyle"] });
            menu.Items.Add(new MenuItem
            {
                Header = "在资源管理器中定位",
                IsEnabled = false,
                Style = (Style)theme["AppMenuItemStyle"]
            });

            menu.ApplyTemplate();
            menu.Measure(new Size(400, 400));
            menu.Arrange(new Rect(0, 0, 400, 400));
            menu.UpdateLayout();
        });
    }

    /// <summary>选中态会显示对勾图标，单独渲染一次确保图标资源解析得到。</summary>
    [Fact]
    public void CheckedMenuItem_RendersWithoutThrowing()
    {
        RunOnStaThread(() =>
        {
            ResourceDictionary theme = LoadMergedTheme();
            var menu = new ContextMenu { Style = (Style)theme["AppContextMenuStyle"] };
            menu.Resources.MergedDictionaries.Add(theme);
            menu.Items.Add(new MenuItem
            {
                Header = "跟随系统默认",
                IsCheckable = true,
                IsChecked = true,
                Style = (Style)theme["AppMenuItemStyle"]
            });

            menu.ApplyTemplate();
            menu.Measure(new Size(400, 400));
            menu.Arrange(new Rect(0, 0, 400, 400));
            menu.UpdateLayout();
        });
    }

    /// <summary>
    /// Setter 只能写依赖属性。给 Resources 这类只读 CLR 属性写 Setter
    /// 会在渲染时抛 ArgumentNullException(property)，这里直接禁掉。
    /// </summary>
    [Fact]
    public void MenuTheme_DoesNotSetClrOnlyProperties()
    {
        string theme = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "ExpressPackingMonitoring", "Themes", "MenuTheme.xaml"));

        // 注释里会写明"不能这样用"，先去掉注释再查，避免误判。
        string markup = Regex.Replace(theme, "<!--.*?-->", "", RegexOptions.Singleline);

        foreach (string clrOnly in new[] { "Resources", "Items", "Inlines" })
        {
            Assert.DoesNotContain(
                $"<Setter Property=\"{clrOnly}\"",
                markup,
                StringComparison.Ordinal);
        }
    }

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
