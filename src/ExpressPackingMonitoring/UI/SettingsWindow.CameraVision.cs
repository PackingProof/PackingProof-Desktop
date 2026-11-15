using System.Windows;

namespace ExpressPackingMonitoring.UI;

public partial class SettingsWindow
{
    private void CameraBarcodeEnhancedRecognitionCheckBox_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (CameraBarcodeEnhancedRecognitionCheckBox.IsChecked != true)
            return;

        Config.EnableCameraBarcodeQrCode = false;
        CameraBarcodeQrCodeCheckBox.IsChecked = false;
    }

    private void CameraBarcodeQrCodeCheckBox_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (CameraBarcodeQrCodeCheckBox.IsChecked != true)
            return;

        Config.EnableCameraBarcodeEnhancedRecognition = false;
        CameraBarcodeEnhancedRecognitionCheckBox.IsChecked = false;
    }
}
