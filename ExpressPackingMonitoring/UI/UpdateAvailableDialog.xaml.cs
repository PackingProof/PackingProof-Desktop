using ExpressPackingMonitoring.Logging;
using System.Windows;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI
{
    public partial class UpdateAvailableDialog : Window
    {
        private readonly UpdateCheckResult _result;
        private readonly AppPatchDownloadService _downloadService;
        private readonly Func<bool> _isRecordingProvider;
        private bool _openFullDownloadPage;

        public bool OpenFullDownloadPageRequested { get; private set; }
        public bool RestartRequested { get; private set; }
        public string DownloadUrl { get; private set; }

        public UpdateAvailableDialog(UpdateCheckResult result, bool isRecording = false)
            : this(result, new AppPatchDownloadService(), () => isRecording)
        {
        }

        internal UpdateAvailableDialog(
            UpdateCheckResult result,
            AppPatchDownloadService downloadService,
            Func<bool>? isRecordingProvider = null)
        {
            InitializeComponent();
            _result = result;
            _downloadService = downloadService;
            _isRecordingProvider = isRecordingProvider ?? (() => false);
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
                    DownloadStatusText.Text = preparation.Message;
                    bool isRecording = _isRecordingProvider();
                    string restartMessage = isRecording
                        ? preparation.Message
                            + "\n\n当前正在录像。立即重启会先结束并保存当前录像，确认保存完成后再安装更新。"
                        : preparation.Message
                            + "\n\n是否立即退出程序，并通过根目录启动器安装更新？";
                    RestartRequested = AppDialog.Confirm(
                        this,
                        restartMessage,
                        "补丁准备完成",
                        isRecording ? "保存录像并重启" : "立即重启更新",
                        "稍后更新",
                        isRecording ? AppDialogSeverity.Warning : AppDialogSeverity.Information,
                        isDangerous: isRecording);
                    DialogResult = RestartRequested;
                    Close();
                    break;

                case AppPatchPreparationStatus.FullPackageRequired:
                    DownloadProgressBar.Visibility = Visibility.Collapsed;
                    DownloadStatusText.Text = preparation.Message + "，请改用完整版本更新";
                    DownloadUrl = string.IsNullOrWhiteSpace(preparation.FullDownloadUrl)
                        ? _result.DownloadUrl
                        : preparation.FullDownloadUrl;
                    if (!string.IsNullOrWhiteSpace(preparation.FullDownloadFallbackUrl)
                        && !string.Equals(
                            preparation.FullDownloadFallbackUrl,
                            DownloadUrl,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        DownloadStatusText.Text += $"\n备用下载页：{preparation.FullDownloadFallbackUrl}";
                    }
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

        private void Later_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
