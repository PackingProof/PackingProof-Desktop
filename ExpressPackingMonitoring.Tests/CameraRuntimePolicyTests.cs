using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class CameraRuntimePolicyTests
{
    [Fact]
    public void ShouldRunPcCameraRuntime_RequiresEnabledModule()
    {
        Assert.True(MainViewModel.ShouldRunPcCameraRuntime(
            isEnabled: true,
            isSetupWizardActive: false,
            isDisposed: false,
            isShutdownRequested: false));

        Assert.False(MainViewModel.ShouldRunPcCameraRuntime(
            isEnabled: false,
            isSetupWizardActive: false,
            isDisposed: false,
            isShutdownRequested: false));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ShouldRunPcCameraRuntime_RejectsOwnedOrClosingRuntime(
        bool isSetupWizardActive,
        bool isDisposed,
        bool isShutdownRequested)
    {
        Assert.False(MainViewModel.ShouldRunPcCameraRuntime(
            isEnabled: true,
            isSetupWizardActive,
            isDisposed,
            isShutdownRequested));
    }
}
