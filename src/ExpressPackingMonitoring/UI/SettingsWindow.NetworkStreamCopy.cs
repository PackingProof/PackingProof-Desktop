using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ExpressPackingMonitoring.UI;

public partial class SettingsWindow
{
    private bool _isApplyingNetworkCameraStreamCopyChoice;
    private readonly List<(FrameworkElement Control, bool IsEnabled)>
        _networkCameraStreamCopyControlStates = [];

    private void NetworkCameraStreamCopyToggle_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (_isApplyingNetworkCameraStreamCopyChoice
            || !IsLoaded
            || Config == null
            || NetworkCameraStreamCopyToggle == null)
        {
            return;
        }

        if (NetworkCameraStreamCopyToggle.IsChecked == true
            && !ConfirmNetworkCameraStreamCopyRisk())
        {
            CheckBox checkBox = NetworkCameraStreamCopyToggle;
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.DataBind,
                new Action(() =>
                    ApplyNetworkCameraStreamCopyChoice(checkBox, false)));
            return;
        }

        ApplyNetworkCameraStreamCopyConstraints();
    }

    private void NetworkCameraStreamCopyToggle_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not CheckBox { IsChecked: false } checkBox)
            return;

        e.Handled = true;
        checkBox.Focus();
        ApplyNetworkCameraStreamCopyChoice(
            checkBox,
            ConfirmNetworkCameraStreamCopyRisk());
    }

    private void NetworkCameraStreamCopyToggle_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key != Key.Space
            || sender is not CheckBox { IsChecked: false } checkBox)
        {
            return;
        }

        e.Handled = true;
        ApplyNetworkCameraStreamCopyChoice(
            checkBox,
            ConfirmNetworkCameraStreamCopyRisk());
    }

    private bool ConfirmNetworkCameraStreamCopyRisk() =>
        AppDialog.Confirm(
            this,
            AppLanguage.Get("NetworkCameraStreamCopyUnavailableFeatures")
            + Environment.NewLine
            + Environment.NewLine
            + AppLanguage.Get("NetworkCameraStreamCopySubCameraScanStillAvailable"),
            AppLanguage.Get("以下功能将失效"),
            AppDialogSeverity.Warning,
            confirmText: AppLanguage.Get("启用"),
            cancelText: AppLanguage.Get("取消"));

    private void ApplyNetworkCameraStreamCopyChoice(
        CheckBox checkBox,
        bool enabled)
    {
        _isApplyingNetworkCameraStreamCopyChoice = true;
        try
        {
            Config.EnableNetworkCameraStreamCopy = enabled;
            checkBox.SetCurrentValue(
                System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                enabled);
            checkBox.GetBindingExpression(
                System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)
                ?.UpdateSource();
        }
        finally
        {
            _isApplyingNetworkCameraStreamCopyChoice = false;
        }

        ApplyNetworkCameraStreamCopyConstraints();
    }

    private void ApplyNetworkCameraStreamCopyConstraints()
    {
        if (Config == null || NetworkCameraStreamCopyToggle == null)
            return;

        bool enabled = IsLoaded
            ? NetworkCameraStreamCopyToggle.IsChecked == true
                && NetworkCameraPanel.Visibility == Visibility.Visible
            : Config.EnableNetworkCameraStreamCopy
                && string.Equals(
                    AppConfig.NormalizeCameraSourceKind(
                        Config.CameraSourceKind,
                        Config.NetworkCameraUrl),
                    "network",
                    StringComparison.Ordinal);
        FrameworkElement[] controls =
        [
            CameraRotationRow,
            EventRecordingBufferToggle,
            PreRecordBufferRow,
            WatermarkToggle,
            ThirdPartyWatermarkToggle,
            AudioRecordCheckBox,
            MicSelectionRow,
            AudioSyncOffsetRow,
            DirectAacRecordingCheckBox
        ];
        if (enabled && _networkCameraStreamCopyControlStates.Count == 0)
        {
            foreach (FrameworkElement control in controls)
            {
                if (control != null)
                {
                    _networkCameraStreamCopyControlStates.Add(
                        (control, control.IsEnabled));
                }
            }
        }

        foreach (FrameworkElement control in controls)
        {
            if (control != null)
                control.IsEnabled = !enabled;
        }

        UpdateEventRecordingBufferControlState();
        if (enabled && PreRecordBufferRow != null)
            PreRecordBufferRow.IsEnabled = false;
        else if (!enabled && _networkCameraStreamCopyControlStates.Count > 0)
        {
            foreach ((FrameworkElement control, bool isEnabled) in
                     _networkCameraStreamCopyControlStates)
            {
                control.IsEnabled = isEnabled;
            }
            _networkCameraStreamCopyControlStates.Clear();
        }

        if (OverlayChannelStreamCopyNotice != null)
        {
            OverlayChannelStreamCopyNotice.Visibility = enabled
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }
}
