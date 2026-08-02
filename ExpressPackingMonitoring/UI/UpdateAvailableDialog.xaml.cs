using ExpressPackingMonitoring.Logging;
using System.Windows;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI
{
    public partial class UpdateAvailableDialog : Window
    {
        private readonly UpdateCheckResult _result;
        private readonly AppPatchDownloadService _downloadService;
        private bool _openFullDownloadPage;

        public bool OpenFullDownloadPageRequested { get; private set; }
        public string DownloadUrl { get; private set; }

        public UpdateAvailableDialog(UpdateCheckResult result)
            : this(result, new AppPatchDownloadService())
        {
        }

        internal UpdateAvailableDialog(
            UpdateCheckResult result,
            AppPatchDownloadService downloadService)
        {
            InitializeComponent();
            _result = result;
            _downloadService = downloadService;
            DownloadUrl = result.DownloadUrl;
            VersionText.Text = $"发现新版本：{result.LatestVersion}";
            TitleText.Text = string.IsNullOrWhiteSpace(result.Title)
                ? "更新标题：未填写"
                : $"更新标题：{result.Title}";
            var document = MarkdownFlowDocumentRenderer.Render(result.Body);
            document.FontFamily = BodyViewer.FontFamily;
            BodyViewer.Document = document;
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (_openFullDownloadPage)
            {
                OpenFullDownloadPageRequested = true;
                DialogResult = true;
                Close();
                return;
            }

            DownloadButton.IsEnabled = false;
            LaterButton.IsEnabled = false;
            DownloadStatusText.Visibility = Visibility.Visible;
            DownloadProgressBar.Visibility = Visibility.Visible;
            DownloadProgressBar.IsIndeterminate = true;
            var progress = new Progress<AppPatchDownloadProgress>(value =>
            {
                DownloadStatusText.Text = value.Message;
                if (value.TotalBytes > 0)
                {
                    DownloadProgressBar.IsIndeterminate = false;
                    DownloadProgressBar.Value = Math.Min(
                        100,
                        value.BytesReceived * 100d / value.TotalBytes);
                }
            });

            AppPatchPreparationResult preparation = await _downloadService.PrepareAsync(
                _result,
                progress);
            switch (preparation.Status)
            {
                case AppPatchPreparationStatus.Ready:
                case AppPatchPreparationStatus.AlreadyReady:
                    DownloadProgressBar.IsIndeterminate = false;
                    DownloadProgressBar.Value = 100;
                    DownloadStatusText.Text = preparation.Message
                        + "，完全退出程序后，下次从根目录启动器启动时自动安装";
                    DownloadButton.Content = "完成";
                    DownloadButton.IsEnabled = true;
                    DownloadButton.Click -= Download_Click;
                    DownloadButton.Click += Complete_Click;
                    LaterButton.Visibility = Visibility.Collapsed;
                    break;

                case AppPatchPreparationStatus.FullPackageRequired:
                    DownloadProgressBar.Visibility = Visibility.Collapsed;
                    DownloadStatusText.Text = preparation.Message + "，请改用完整版本更新";
                    DownloadUrl = string.IsNullOrWhiteSpace(preparation.FullDownloadUrl)
                        ? _result.DownloadUrl
                        : preparation.FullDownloadUrl;
                    _openFullDownloadPage = true;
                    DownloadButton.Content = "打开完整更新页面";
                    DownloadButton.IsEnabled = true;
                    LaterButton.IsEnabled = true;
                    break;

                default:
                    DownloadProgressBar.Visibility = Visibility.Collapsed;
                    DownloadStatusText.Text = preparation.Message;
                    DownloadButton.Content = "重试下载";
                    DownloadButton.IsEnabled = true;
                    LaterButton.IsEnabled = true;
                    break;
            }
        }

        private void Complete_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Later_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
