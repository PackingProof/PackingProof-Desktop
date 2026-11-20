using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;
using System.Windows;
using System.Windows.Controls;

namespace ExpressPackingMonitoring.UI;

public partial class SettingsWindow
{
    private bool _windowsDevicePermissionCheckInProgress;
    private bool _windowsDevicePermissionEventsHooked;

    private async void WindowsDevicePermissions_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (!_windowsDevicePermissionEventsHooked)
        {
            _windowsDevicePermissionEventsHooked = true;
            Activated += WindowsDevicePermissions_Activated;
            Closed += WindowsDevicePermissions_Closed;
        }

        await RefreshWindowsDevicePermissionHelpAsync();
    }

    private async void WindowsDevicePermissions_Activated(
        object? sender,
        EventArgs e)
    {
        if (CameraPermissionRecoveryRow.Visibility == Visibility.Visible
            || MicrophonePermissionRecoveryRow.Visibility == Visibility.Visible)
        {
            await RefreshWindowsDevicePermissionHelpAsync();
        }
    }

    private void WindowsDevicePermissions_Closed(object? sender, EventArgs e)
    {
        Activated -= WindowsDevicePermissions_Activated;
        Closed -= WindowsDevicePermissions_Closed;
        _windowsDevicePermissionEventsHooked = false;
    }

    private async Task RefreshWindowsDevicePermissionHelpAsync()
    {
        if (!Capabilities.CanShowWindowsDevicePermissionHelp
            || _windowsDevicePermissionCheckInProgress)
        {
            return;
        }

        _windowsDevicePermissionCheckInProgress = true;
        try
        {
            WindowsDevicePermissionSnapshot snapshot =
                await WindowsDevicePermissionService.CheckAsync();
            if (!IsLoaded)
                return;

            CameraPermissionRecoveryRow.Visibility =
                !string.Equals(
                    Config.CameraSourceKind,
                    "network",
                    StringComparison.OrdinalIgnoreCase)
                && snapshot.CameraNeedsRecovery
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            MicrophonePermissionRecoveryRow.Visibility =
                Config.EnableAudioRecording
                && snapshot.MicrophoneNeedsRecovery
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
        finally
        {
            _windowsDevicePermissionCheckInProgress = false;
        }
    }

    private async void OpenCameraPermissionSettings_Click(
        object sender,
        RoutedEventArgs e)
    {
        await OpenDevicePermissionSettingsAsync(
            sender,
            WindowsDevicePermissionService.OpenCameraSettingsAsync);
    }

    private async void OpenMicrophonePermissionSettings_Click(
        object sender,
        RoutedEventArgs e)
    {
        await OpenDevicePermissionSettingsAsync(
            sender,
            WindowsDevicePermissionService.OpenMicrophoneSettingsAsync);
    }

    private async Task OpenDevicePermissionSettingsAsync(
        object sender,
        Func<Task<bool>> openAsync)
    {
        if (sender is Button button)
            button.IsEnabled = false;
        try
        {
            if (!await openAsync())
            {
                Context.ShowToast?.Invoke(
                    AppLanguage.Get("WindowsDevicePermission.OpenFailed"),
                    ToastSeverity.Error);
            }
        }
        finally
        {
            if (sender is Button buttonToRestore)
                buttonToRestore.IsEnabled = true;
        }
    }
}
