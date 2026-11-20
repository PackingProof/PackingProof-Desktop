using ExpressPackingMonitoring.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ExpressPackingMonitoring.UI;

public partial class SettingsWindow
{
    private bool _isApplyingDirectAacRecordingChoice;

    private void DirectAacRecordingCheckBox_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (!IsLoaded
            || _isApplyingDirectAacRecordingChoice
            || sender is not CheckBox checkBox)
        {
            return;
        }

        ApplyDirectAacRecordingChoice(
            checkBox,
            ConfirmDirectAacRecordingRisk());
    }

    private void DirectAacRecordingCheckBox_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not CheckBox { IsChecked: false } checkBox)
            return;

        e.Handled = true;
        checkBox.Focus();
        ApplyDirectAacRecordingChoice(
            checkBox,
            ConfirmDirectAacRecordingRisk());
    }

    private void DirectAacRecordingCheckBox_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key != Key.Space
            || sender is not CheckBox { IsChecked: false } checkBox)
        {
            return;
        }

        e.Handled = true;
        ApplyDirectAacRecordingChoice(
            checkBox,
            ConfirmDirectAacRecordingRisk());
    }

    private bool ConfirmDirectAacRecordingRisk()
    {
        return AppDialog.Confirm(
            this,
            AppLanguage.Get("实时封装时如果麦克风断开或音频设备异常被占用，可能导致 FFmpeg 录制中断，从而造成视频异常或录制失败"),
            AppLanguage.Get("开启音频直接写入 MKV？"),
            AppDialogSeverity.Warning,
            confirmText: AppLanguage.Get("了解风险并开启"),
            cancelText: AppLanguage.Get("保持关闭"));
    }

    private void ApplyDirectAacRecordingChoice(
        CheckBox checkBox,
        bool enabled)
    {
        _isApplyingDirectAacRecordingChoice = true;
        try
        {
            Config.EnableDirectAacRecording = enabled;
            checkBox.SetCurrentValue(
                System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                enabled);
            checkBox.GetBindingExpression(
                System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)
                ?.UpdateSource();
        }
        finally
        {
            _isApplyingDirectAacRecordingChoice = false;
        }
    }
}
