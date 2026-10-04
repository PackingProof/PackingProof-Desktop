using System.Windows;
using ExpressPackingMonitoring.Themes;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

[Collection("WPF render tests")]
public sealed class ThemeManagerTests
{
    [Theory]
    [InlineData("Auto", AppTheme.Auto)]
    [InlineData("Light", AppTheme.Light)]
    [InlineData("Dark", AppTheme.Dark)]
    [InlineData("invalid", AppTheme.Auto)]
    [InlineData(null, AppTheme.Auto)]
    public void ResolveConfiguredThemeUsesSavedThemeOrAutoFallback(string? configured, AppTheme expected)
    {
        Assert.Equal(expected, ThemeManager.ResolveConfiguredTheme(configured!));
    }

    /// <summary>
    /// 主题没变时不能再换一次资源字典：设置页一打开，"外观主题"下拉的绑定就会调到
    /// ApplyTheme，白刷一遍会让主窗口整棵界面重新解析资源，打开设置就慢了。
    /// 真换了主题还是得换字典。
    /// </summary>
    [Fact]
    public void ApplyingUnchangedThemeKeepsResourceDictionary()
    {
        RunOnStaThread(() =>
        {
            if (Application.Current == null)
                _ = new Application();
            Application.Current!.Resources = new ResourceDictionary();

            ThemeManager.ApplyTheme(AppTheme.Light);
            ResourceDictionary light = Assert.Single(ThemeDictionaries(useDarkTheme: false));
            ThemeManager.ApplyTheme(AppTheme.Light);
            Assert.Same(light, Assert.Single(ThemeDictionaries(useDarkTheme: false)));

            ThemeManager.ApplyTheme(AppTheme.Dark);
            Assert.Empty(ThemeDictionaries(useDarkTheme: false));
            Assert.Single(ThemeDictionaries(useDarkTheme: true));
        });
    }

    private static IReadOnlyList<ResourceDictionary> ThemeDictionaries(bool useDarkTheme)
    {
        string fileName = useDarkTheme ? "DarkTheme.xaml" : "LightTheme.xaml";
        return (Application.Current?.Resources.MergedDictionaries ?? [])
            .Where(dictionary => dictionary.Source?.OriginalString.Contains(
                fileName,
                StringComparison.Ordinal) == true)
            .ToList();
    }

    /// <summary>
    /// 主题资源要真的加载进 WPF 资源体系，测试宿主得自己起 STA 线程。
    /// </summary>
    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA 线程执行超时");
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
