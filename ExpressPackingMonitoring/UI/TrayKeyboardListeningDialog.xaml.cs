using System.Windows;

namespace ExpressPackingMonitoring.UI;

public enum TrayKeyboardListeningChoice
{
    None,
    ContinueListening,
    PauseListening
}

public partial class TrayKeyboardListeningDialog : Window
{
    public TrayKeyboardListeningDialog()
    {
        InitializeComponent();
    }

    public TrayKeyboardListeningChoice Choice { get; private set; }
    public bool RememberChoice => RememberChoiceCheckBox.IsChecked == true;

    private void ContinueListening_Click(object sender, RoutedEventArgs e)
    {
        Choice = TrayKeyboardListeningChoice.ContinueListening;
        DialogResult = true;
    }

    private void PauseListening_Click(object sender, RoutedEventArgs e)
    {
        Choice = TrayKeyboardListeningChoice.PauseListening;
        DialogResult = true;
    }
}
