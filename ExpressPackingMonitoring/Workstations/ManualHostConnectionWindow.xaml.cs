using System.Windows;

namespace ExpressPackingMonitoring;

public partial class ManualHostConnectionWindow : Window
{
    public event Action<string>? ConnectionSubmitted;

    public ManualHostConnectionWindow(bool requiresCompleteLink)
    {
        InitializeComponent();
        ConnectionHintText.Text = requiresCompleteLink
            ? "请在保存主机的“手机/电脑连接”中复制完整链接，再粘贴到这里"
            : "输入主机地址，或粘贴完整连接链接";
        Loaded += (_, _) => ConnectionInputTextBox.Focus();
    }

    public void SetBusy(bool busy)
    {
        ConnectionInputTextBox.IsEnabled = !busy;
        ConnectButton.IsEnabled = !busy;
        ConnectButtonText.Text = busy ? "正在连接" : "连接";
        if (busy)
            ValidationText.Visibility = Visibility.Collapsed;
    }

    public void ShowError(string message)
    {
        SetBusy(false);
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
        ConnectionInputTextBox.Focus();
        ConnectionInputTextBox.SelectAll();
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        string input = ConnectionInputTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            ShowError("请输入主机地址或完整连接链接");
            return;
        }

        SetBusy(true);
        ConnectionSubmitted?.Invoke(input);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
