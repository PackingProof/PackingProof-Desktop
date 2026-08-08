using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.ViewModels;
using System.Globalization;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ScanInputPlaceholderTests
{
    [Fact]
    public void PlaceholderResources_HaveEnglishAndChineseValues()
    {
        CultureInfo en = CultureInfo.GetCultureInfo("en-US");
        CultureInfo zh = CultureInfo.GetCultureInfo("zh-Hans");

        Assert.Equal(
            "Supports background scanning; you can also type manually",
            AppLanguage.Get("ScanInput.PlaceholderGlobal", en));
        Assert.Equal(
            "支持后台扫码，也可手动输入",
            AppLanguage.Get("ScanInput.PlaceholderGlobal", zh));
        Assert.Equal(
            "Scan within the app or type manually",
            AppLanguage.Get("ScanInput.PlaceholderLocal", en));
        Assert.Equal(
            "仅软件内扫码或手动输入",
            AppLanguage.Get("ScanInput.PlaceholderLocal", zh));
    }

    [Fact]
    public void ResolveScanInputPlaceholder_SwitchesByGlobalKeyboardSetting()
    {
        string global = MainViewModel.ResolveScanInputPlaceholder(enableGlobalKeyboard: true);
        string local = MainViewModel.ResolveScanInputPlaceholder(enableGlobalKeyboard: false);

        Assert.False(string.IsNullOrWhiteSpace(global));
        Assert.False(string.IsNullOrWhiteSpace(local));
        Assert.NotEqual(global, local);
    }
}
