using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace ExpressPackingMonitoring.UI;

internal enum PlaybackScreenshotDestination
{
    Cancel,
    CopyToClipboard,
    SaveAs
}

public partial class PlaybackScreenshotDialog : Window
{
    internal PlaybackScreenshotDestination Destination { get; private set; } =
        PlaybackScreenshotDestination.Cancel;

    internal PlaybackScreenshotDialog(BitmapSource preview)
    {
        InitializeComponent();
        PlaybackScreenshotDialogSize size =
            PlaybackInteractionPolicy.CalculateScreenshotDialogSize(
                preview.PixelWidth,
                preview.PixelHeight);
        Width = size.WindowWidth;
        PreviewFrame.Width = size.PreviewWidth;
        PreviewFrame.Height = size.PreviewHeight;
        PreviewImage.Width = size.PreviewWidth;
        PreviewImage.Height = size.PreviewHeight;
        PreviewImage.Source = preview;
        Loaded += (_, _) => CopyButton.Focus();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        Destination = PlaybackScreenshotDestination.CopyToClipboard;
        DialogResult = true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Destination = PlaybackScreenshotDestination.SaveAs;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Destination = PlaybackScreenshotDestination.Cancel;
        DialogResult = false;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        Destination = PlaybackScreenshotDestination.Cancel;
        DialogResult = false;
    }
}
