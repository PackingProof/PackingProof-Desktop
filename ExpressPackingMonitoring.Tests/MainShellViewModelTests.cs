using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public class MainShellViewModelTests
{
    [Fact]
    public void UnknownStartupModule_FallsBackToOverview()
    {
        var shell = new MainShellViewModel("unknown");

        Assert.Equal(AppModules.Overview, shell.CurrentModule);
        Assert.True(shell.IsOverviewActive);
    }

    [Theory]
    [InlineData(AppModules.Overview)]
    [InlineData(AppModules.PcRecording)]
    [InlineData(AppModules.MobileBackup)]
    [InlineData(AppModules.OrderIntegration)]
    [InlineData(AppModules.VideoLibrary)]
    [InlineData(AppModules.Settings)]
    public void Navigate_UpdatesSingleActiveModule(string module)
    {
        var shell = new MainShellViewModel();

        shell.Navigate(module);

        Assert.Equal(module, shell.CurrentModule);
        Assert.Equal(module == AppModules.Overview, shell.IsOverviewActive);
        Assert.Equal(module == AppModules.PcRecording, shell.IsPcRecordingActive);
        Assert.Equal(module == AppModules.MobileBackup, shell.IsMobileBackupActive);
        Assert.Equal(module == AppModules.OrderIntegration, shell.IsOrderIntegrationActive);
        Assert.Equal(module == AppModules.VideoLibrary, shell.IsVideoLibraryActive);
        Assert.Equal(module == AppModules.Settings, shell.IsSettingsActive);
    }

    [Theory]
    [InlineData(0, "", "", "", "", "0", "秒")]
    [InlineData(59, "", "", "", "", "59", "秒")]
    [InlineData(60, "", "", "1", "分", "0", "秒")]
    [InlineData(65, "", "", "1", "分", "5", "秒")]
    [InlineData(3665, "1", "时", "1", "分", "", "")]
    public void AverageDurationDisplay_OmitsZeroMinuteAndHourSegments(
        int totalSeconds,
        string hourValue,
        string hourUnit,
        string minuteValue,
        string minuteUnit,
        string secondValue,
        string secondUnit)
    {
        MainViewModel.DurationDisplayText display = MainViewModel.FormatAverageDurationDisplay(TimeSpan.FromSeconds(totalSeconds));

        Assert.Equal(hourValue, display.HourValue);
        Assert.Equal(hourUnit, display.HourUnit);
        Assert.Equal(minuteValue, display.MinuteValue);
        Assert.Equal(minuteUnit, display.MinuteUnit);
        Assert.Equal(secondValue, display.SecondValue);
        Assert.Equal(secondUnit, display.SecondUnit);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void MobileConnectionIndicator_DependsOnUniqueDeviceCount(int deviceCount, bool expected) =>
        Assert.Equal(expected, MobileBackupViewModel.IsMobileConnectionActive(deviceCount));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void OrderIntegrationIndicator_RequiresConfiguredAndEnabled(bool configured, bool enabled, bool expected) =>
        Assert.Equal(expected, OrderIntegrationViewModel.IsOrderIntegrationRunning(configured, enabled));

    [Theory]
    [InlineData("userscript", true)]
    [InlineData("print-station", true)]
    [InlineData("web-mobile", false)]
    [InlineData("mobile-app", false)]
    [InlineData("", false)]
    public void PrintWorkstationIndicator_UsesPrintClientTypes(string clientType, bool expected) =>
        Assert.Equal(expected, MainViewModel.IsPrintWorkstationClientType(clientType));
}
