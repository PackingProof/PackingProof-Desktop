using ExpressPackingMonitoring.Logging;
using System.Windows;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI
{
    public partial class UpdateAvailableDialog : Window
    {
        public string DownloadUrl { get; }

        public UpdateAvailableDialog(UpdateCheckResult result)
        {
            InitializeComponent();
            DownloadUrl = result.DownloadUrl;
            VersionText.Text = $"发现新版本：{result.LatestVersion}";
            TitleText.Text = string.IsNullOrWhiteSpace(result.Title)
                ? "更新标题：未填写"
                : $"更新标题：{result.Title}";
            var document = MarkdownFlowDocumentRenderer.Render(result.Body);
            document.FontFamily = BodyViewer.FontFamily;
            BodyViewer.Document = document;
        }

        private void Download_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Later_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
