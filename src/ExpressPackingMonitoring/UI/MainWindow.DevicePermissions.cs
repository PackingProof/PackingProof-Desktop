using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;
using System.Windows;

namespace ExpressPackingMonitoring.UI;

public partial class MainWindow
{
    private bool _windowsDevicePermissionPromptChecked;

    private async Task ShowWindowsDevicePermissionRecoveryIfNeededAsync(
        MainViewModel viewModel)
    {
        if (_windowsDevicePermissionPromptChecked)
            return;
        _windowsDevicePermissionPromptChecked = true;

        SettingsCapabilities capabilities =
            SettingsCapabilities.ForPreset(viewModel.Config.DeploymentPreset);
        if (!capabilities.CanShowWindowsDevicePermissionHelp)
            return;

        WindowsDevicePermissionSnapshot snapshot =
            await WindowsDevicePermissionService.CheckAsync();
        bool cameraBlocked =
            !string.Equals(
                viewModel.Config.CameraSourceKind,
                "network",
                StringComparison.OrdinalIgnoreCase)
            && snapshot.CameraNeedsRecovery;
        bool microphoneBlocked =
            viewModel.Config.EnableAudioRecording
            && snapshot.MicrophoneNeedsRecovery;
        if (!cameraBlocked && !microphoneBlocked)
            return;

        string message = cameraBlocked && microphoneBlocked
                ? AppLanguage.Get("WindowsDevicePermission.BothBlocked")
                : cameraBlocked
                    ? AppLanguage.Get("WindowsDevicePermission.CameraBlocked")
                    : AppLanguage.Get("WindowsDevicePermission.MicrophoneBlocked");
        bool openSettings = AppDialog.Confirm(
            this,
            message,
            AppLanguage.Get("WindowsDevicePermission.DialogTitle"),
            AppDialogSeverity.Warning,
            confirmText: AppLanguage.Get("WindowsDevicePermission.OpenSettings"),
            cancelText: AppLanguage.Get("稍后"));
        if (openSettings
            && !await WindowsDevicePermissionService.OpenPrivacySettingsAsync())
        {
            AppDialog.Warning(
                this,
                AppLanguage.Get("WindowsDevicePermission.OpenFailed"),
                AppLanguage.Get("WindowsDevicePermission.DialogTitle"));
        }
    }
}
