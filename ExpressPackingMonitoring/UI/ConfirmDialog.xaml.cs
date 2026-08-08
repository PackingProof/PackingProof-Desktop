using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ExpressPackingMonitoring.UI;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(
        string message,
        string caption,
        string confirmText = "确定",
        string cancelText = "取消",
        bool isDangerous = true,
        bool showCancelButton = true,
        AppDialogSeverity severity = AppDialogSeverity.Warning)
    {
        InitializeComponent();
        MessageText.Text = message;
        Title = caption;
        TitleText.Text = caption;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
        CancelButton.Visibility = showCancelButton ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.Margin = showCancelButton ? new Thickness(0, 0, 10, 0) : new Thickness();
        if (!isDangerous || !showCancelButton)
            ConfirmButton.SetResourceReference(StyleProperty, "PrimaryButtonStyle");

        string iconKey = severity switch
        {
            AppDialogSeverity.Error => "FluentDismissIcon",
            AppDialogSeverity.Warning => "FluentWarningIcon",
            _ => "FluentInfoIcon"
        };
        string brushKey = severity switch
        {
            AppDialogSeverity.Error => "AccentRed",
            AppDialogSeverity.Warning => "AccentOrange",
            _ => "AccentBlue"
        };
        if (TryFindResource(iconKey) is Geometry icon)
            SeverityIcon.Data = icon;
        SeverityIcon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
    }

    private void btnOk_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void btnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        DialogResult = false;
        Close();
    }
}
