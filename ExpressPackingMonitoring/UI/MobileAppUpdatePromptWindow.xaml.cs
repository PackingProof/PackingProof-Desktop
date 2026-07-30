using System.Windows;

namespace ExpressPackingMonitoring.UI;

public partial class MobileAppUpdatePromptWindow : Window
{
    private readonly Action _openDownload;

    internal MobileAppUpdatePromptWindow(string message, Action openDownload)
    {
        InitializeComponent();
        MessageText.Text = message;
        _openDownload = openDownload;
        Loaded += (_, _) => PositionNearOwner();
    }

    private void PositionNearOwner()
    {
        Window? owner = Owner;
        if (owner is not { IsLoaded: true })
        {
            Left = Math.Max(12, SystemParameters.WorkArea.Right - ActualWidth - 20);
            Top = Math.Max(12, SystemParameters.WorkArea.Top + 20);
            return;
        }

        Left = Math.Max(
            SystemParameters.WorkArea.Left + 12,
            Math.Min(
                owner.Left + owner.ActualWidth - ActualWidth - 20,
                SystemParameters.WorkArea.Right - ActualWidth - 12));
        Top = Math.Max(
            SystemParameters.WorkArea.Top + 12,
            Math.Min(
                owner.Top + 20,
                SystemParameters.WorkArea.Bottom - ActualHeight - 12));
    }

    private void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _openDownload();
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
