using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 菜单主题的接线约束。
/// 模板里用的是 StaticResource，键缺失或字典合并顺序不对只会在
/// 运行时弹菜单那一刻抛异常，编译期看不出来，所以用守卫钉住。
/// </summary>
public sealed class MenuThemeResourceTests
{
    private static string ReadProjectFile(string relativePath) =>
        File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring", relativePath),
            Encoding.UTF8);

    [Theory]
    [InlineData("AppMenuItemStyle")]
    [InlineData("AppSeparatorStyle")]
    [InlineData("AppContextMenuStyle")]
    public void MenuStyleKeysExist(string key)
    {
        string theme = ReadProjectFile(Path.Combine("Themes", "MenuTheme.xaml"));

        Assert.Contains($"x:Key=\"{key}\"", theme, StringComparison.Ordinal);
    }

    /// <summary>
    /// App.xaml 的合并顺序不能把 MenuTheme 排到 FluentIcons 前面，
    /// 否则模板里的 FluentCheckIcon 取不到，菜单一弹就抛异常。
    /// </summary>
    [Fact]
    public void AppXamlMergesMenuThemeAfterIcons()
    {
        string appXaml = ReadProjectFile("App.xaml");

        int icons = appXaml.IndexOf("FluentIcons.xaml", StringComparison.Ordinal);
        int menu = appXaml.IndexOf("MenuTheme.xaml", StringComparison.Ordinal);

        Assert.True(icons >= 0, "App.xaml 未合并 FluentIcons.xaml");
        Assert.True(menu >= 0, "App.xaml 未合并 MenuTheme.xaml");
        Assert.True(icons < menu, "MenuTheme 必须排在 FluentIcons 之后");
    }

    /// <summary>菜单主题引用的图标必须真的存在于 FluentIcons 里。</summary>
    [Fact]
    public void MenuThemeIconReferencesResolve()
    {
        string theme = ReadProjectFile(Path.Combine("Themes", "MenuTheme.xaml"));
        string icons = ReadProjectFile(Path.Combine("Themes", "FluentIcons.xaml"));

        string[] referenced = Regex
            .Matches(theme, @"\{StaticResource (Fluent\w+)\}")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToArray();

        Assert.NotEmpty(referenced);
        foreach (string icon in referenced)
            Assert.Contains($"x:Key=\"{icon}\"", icons, StringComparison.Ordinal);
    }

    /// <summary>
    /// 回放行的右键菜单必须复用窗口级的同一个实例。
    /// 在 DataTemplate 里逐行 new 一个，50 行就是 50 份，首次右键会明显卡顿。
    /// </summary>
    [Fact]
    public void PlaybackRowMenu_IsSharedWindowResource()
    {
        string xaml = ReadProjectFile(Path.Combine("UI", "PlaybackWindow.xaml"));

        Assert.Contains("x:Key=\"VideoRowContextMenu\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ContextMenu=\"{StaticResource VideoRowContextMenu}\"", xaml, StringComparison.Ordinal);

        // DataTemplate 里不允许再出现内联的 <ContextMenu> 定义。
        int templateStart = xaml.IndexOf("<ListView.ItemTemplate>", StringComparison.Ordinal);
        Assert.True(templateStart >= 0, "未找到回放列表的 ItemTemplate");
        string template = xaml[templateStart..];
        Assert.DoesNotContain("<Grid.ContextMenu>", template, StringComparison.Ordinal);
    }

    /// <summary>右键菜单与悬浮小窗的设备菜单必须共用同一套外观。</summary>
    [Fact]
    public void PlaybackAndFloatingMenusShareOneStyle()
    {
        string playback = ReadProjectFile(Path.Combine("UI", "PlaybackWindow.xaml"));
        string floating = ReadProjectFile(Path.Combine("UI", "FloatingPreviewWindow.xaml.cs"));

        Assert.Contains("Style=\"{StaticResource AppContextMenuStyle}\"", playback, StringComparison.Ordinal);
        Assert.Contains("AppContextMenuStyle", floating, StringComparison.Ordinal);

        // 旧的私有样式已经并入主题，不应再留在小窗里。
        string floatingXaml = ReadProjectFile(Path.Combine("UI", "FloatingPreviewWindow.xaml"));
        Assert.DoesNotContain("FloatingMenuItemStyle", floatingXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FloatingContextMenuStyle", floatingXaml, StringComparison.Ordinal);
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
