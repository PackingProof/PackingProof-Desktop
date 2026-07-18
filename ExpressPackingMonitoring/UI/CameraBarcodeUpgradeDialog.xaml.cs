using System.Windows;

namespace ExpressPackingMonitoring.UI
{
    public partial class CameraBarcodeUpgradeDialog : Window
    {
        public CameraBarcodeUpgradeDialog()
        {
            InitializeComponent();
        }

        private void Enable_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void NotNow_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
