using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class WindowsDevicePermissionServiceTests
{
    [Theory]
    [InlineData(nameof(WindowsDevicePermissionState.DeniedByUser), true)]
    [InlineData(nameof(WindowsDevicePermissionState.DeniedBySystem), true)]
    [InlineData(nameof(WindowsDevicePermissionState.Allowed), false)]
    [InlineData(nameof(WindowsDevicePermissionState.UserPromptRequired), false)]
    [InlineData(nameof(WindowsDevicePermissionState.NotDeclaredByApp), false)]
    [InlineData(nameof(WindowsDevicePermissionState.Unavailable), false)]
    public void OnlyExplicitDenialsShowRecovery(
        string stateName,
        bool expected)
    {
        WindowsDevicePermissionState state =
            Enum.Parse<WindowsDevicePermissionState>(stateName);
        Assert.Equal(
            expected,
            WindowsDevicePermissionService.NeedsRecovery(state));
    }

    [Fact]
    public async Task UnpackagedBuildDoesNotQueryStorePermissions()
    {
        WindowsDevicePermissionSnapshot snapshot =
            await WindowsDevicePermissionService.CheckAsync(
                TestContext.Current.CancellationToken);

        Assert.Equal(
            WindowsDevicePermissionState.Unavailable,
            snapshot.Camera);
        Assert.Equal(
            WindowsDevicePermissionState.Unavailable,
            snapshot.Microphone);
        Assert.False(snapshot.AnyNeedsRecovery);
    }

    [Fact]
    public void StorePermissionApiTypeIsAvailableForDynamicLookup()
    {
        Type? capabilityType =
            WindowsDevicePermissionService.LoadAppCapabilityTypeForTests(
                AppContext.BaseDirectory);

        Assert.NotNull(capabilityType);
        Assert.Equal(
            "Windows.Security.Authorization.AppCapabilityAccess.AppCapability",
            capabilityType!.FullName);
    }

    [Fact]
    public void SettingsExposeDirectCameraAndMicrophoneRecoveryLinks()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml"));
        string code = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.DevicePermissions.cs"));

        Assert.Contains("CameraPermissionRecoveryRow", xaml, StringComparison.Ordinal);
        Assert.Contains("MicrophonePermissionRecoveryRow", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenCameraPermissionSettings_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenMicrophonePermissionSettings_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenCameraSettingsAsync", code, StringComparison.Ordinal);
        Assert.Contains("OpenMicrophoneSettingsAsync", code, StringComparison.Ordinal);
        Assert.Equal(
            "ms-settings:privacy-webcam",
            WindowsDevicePermissionService.CameraSettingsUri);
        Assert.Equal(
            "ms-settings:privacy-microphone",
            WindowsDevicePermissionService.MicrophoneSettingsUri);
    }

    [Fact]
    public void StorePermissionRecoveryDoesNotRaiseSetupPlatformFloor()
    {
        string project = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "ExpressPackingMonitoring.csproj"));
        string service = File.ReadAllText(FindRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "Services",
            "WindowsDevicePermissionService.cs"));

        Assert.Contains(
            "<TargetFramework>net10.0-windows</TargetFramework>",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "using Windows.Security.Authorization.AppCapabilityAccess;",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "Windows.Security.Authorization.AppCapabilityAccess.AppCapability",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "Microsoft.Windows.SDK.NET",
            service,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(
                [current.FullName, .. relativeParts]);
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(
            string.Join(Path.DirectorySeparatorChar, relativeParts));
    }
}
