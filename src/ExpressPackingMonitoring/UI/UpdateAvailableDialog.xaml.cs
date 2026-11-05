using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Localization;
using System.Windows;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI
{
    public partial class UpdateAvailableDialog : Window
    {
        private readonly UpdateCheckResult _result;
        private readonly AppPatchDownloadService _downloadService;
        private readonly Func<bool> _isRecordingProvider;
        private readonly CancellationTokenSource _downloadCancellation = new();
        private bool _openFullDownloadPage;
        private bool _restartForLauncherUpdate;
        private bool _isShownAsDialog;
        private bool _isClosed;

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
            Closed += (_, _) =>
            {
                _isClosed = true;
                _downloadCancellation.Cancel();
            };
            DownloadUrl = result.DownloadUrl;
            VersionText.Text = AppLanguage.Format("发现新版本：{0}", result.LatestVersion);
            TitleText.Text = string.IsNullOrWhiteSpace(result.Title)
                ? AppLanguage.Get("更新标题：未填写")
                : AppLanguage.Format("更新标题：{0}", result.Title);
            var document = MarkdownFlowDocumentRenderer.Render(result.Body);
            document.FontFamily = BodyViewer.FontFamily;
            BodyViewer.Document = document;
        }

        /// <summary>
        /// 由设置窗口以模态方式显示，并记录对话框是否仍处于 ShowDialog 生命周期内。
        /// 下载是异步的，用户可能在下载过程中关闭窗口；关闭后不能再设置 DialogResult。
        /// </summary>
        internal bool? ShowAsDialog(Window owner)
        {
            Owner = owner;
            _isShownAsDialog = true;
            try
            {
                return ShowDialog();
            }
            finally
            {
                _isShownAsDialog = false;
                _downloadCancellation.Cancel();
            }
        }

        internal static bool CanSetDialogResult(bool isShownAsDialog, bool isClosed)
            => isShownAsDialog && !isClosed;

        private void CompleteDialog(bool result)
        {
            if (CanSetDialogResult(_isShownAsDialog, _isClosed))
            {
                DialogResult = result;
                return;
            }

            if (!_isClosed)
                Close();
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (_openFullDownloadPage)
            {
                OpenFullDownloadPageRequested = true;
                CompleteDialog(true);
                return;
            }

            if (_restartForLauncherUpdate)
            {
                // 启动器已经换好了，但只有它下次启动才会应用新载荷：直接走"重启升级"，别在这里空等。
                RestartRequested = true;
                CompleteDialog(true);
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
                    DownloadProgressBar.TargetValue = Math.Min(
                        100,
                        value.BytesReceived * 100d / value.TotalBytes);
                }
            });

            AppPatchPreparationResult preparation;
            try
            {
                preparation = await _downloadService.PrepareAsync(
                    _result,
                    progress,
                    _downloadCancellation.Token);
            }
            catch (OperationCanceledException) when (_downloadCancellation.IsCancellationRequested)
            {
                return;
            }

            // 用户关闭窗口后，下载可能刚好完成；此时不再弹二次确认或触碰 DialogResult。
            if (!CanSetDialogResult(_isShownAsDialog, _isClosed))
                return;

            switch (preparation.Status)
            {
                case AppPatchPreparationStatus.Ready:
                case AppPatchPreparationStatus.AlreadyReady:
                    DownloadProgressBar.IsIndeterminate = false;
                    DownloadProgressBar.TargetValue = 100;
                    DownloadStatusText.Text = preparation.Message;
                    bool isRecording = _isRecordingProvider();
                    string restartMessage = isRecording
                        ? preparation.Message
                            + "\n\n当前正在录像。立即重启会先结束并保存当前录像，确认保存完成后再安装更新"
                        : preparation.Message
                            + "\n\n是否立即退出程序，并通过根目录启动器安装更新？";
                    RestartRequested = AppDialog.Confirm(
                        this,
                        restartMessage,
                        "补丁准备完成",
                        isRecording ? AppDialogSeverity.Warning : AppDialogSeverity.Information,
                        confirmText: isRecording ? "保存录像并重启" : "立即重启更新",
                        cancelText: "稍后更新",
                        isDangerous: isRecording);
                    CompleteDialog(RestartRequested);
                    break;

                case AppPatchPreparationStatus.FullPackageRequired:
                    DownloadProgressBar.Visibility = Visibility.Collapsed;
                    DownloadStatusText.Text = AppLanguage.Format(
                        "{0}，请改用完整版本更新",
                        preparation.Message);
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

                case AppPatchPreparationStatus.LauncherUpdateRequired:
                    DownloadProgressBar.Visibility = Visibility.Collapsed;
                    DownloadStatusText.Text = preparation.Message;
                    DownloadButton.Content = "立即重启升级";
                    DownloadButton.IsEnabled = true;
                    LaterButton.IsEnabled = true;
                    _restartForLauncherUpdate = true;
                    break;

                default:
                    DownloadProgressBar.Visibility = Visibility.Collapsed;
                    DownloadStatusText.Text = preparation.Message;
                    DownloadButton.Content = "重试下载";
                    DownloadButton.IsEnabled = true;
                    LaterButton.IsEnabled = true;
                    if (preparation.Status == AppPatchPreparationStatus.Failed)
                        ShowFallbackDownloadEntry(preparation);
                    break;
            }
        }

        /// <summary>
        /// 补丁彻底失败时给一条人工出路：把"打开完整更新页面/备用网盘"亮出来。
        /// 现场就出现过"检查得到新版本、下载一路失败"，用户只能反复点重试的死胡同。
        /// </summary>
        private void ShowFallbackDownloadEntry(AppPatchPreparationResult preparation)
        {
            string fallbackUrl = ResolveFallbackDownloadUrl(_result, preparation);
            if (fallbackUrl.Length == 0)
                return;

            DownloadUrl = fallbackUrl;
            FallbackDownloadButton.Visibility = Visibility.Visible;
            if (!DownloadStatusText.Text.Contains(fallbackUrl, StringComparison.Ordinal))
                DownloadStatusText.Text += $"\n完整更新页面：{fallbackUrl}";
        }

        /// <summary>
        /// 人工出口用哪个地址：检查结果里的下载地址优先（发布说明里写了百度网盘时给的就是网盘链接），
        /// 其次是清单里的完整下载页与备用下载页。都没有就返回空串，不给按钮。
        /// </summary>
        internal static string ResolveFallbackDownloadUrl(
            UpdateCheckResult result,
            AppPatchPreparationResult preparation)
        {
            foreach (string candidate in new[]
            {
                result.DownloadUrl,
                preparation.FullDownloadUrl,
                preparation.FullDownloadFallbackUrl
            })
            {
                string url = candidate?.Trim() ?? "";
                if (url.Length > 0)
                    return url;
            }

            return "";
        }

        private void FallbackDownload_Click(object sender, RoutedEventArgs e)
        {
            OpenFullDownloadPageRequested = true;
            CompleteDialog(true);
        }

        private void Later_Click(object sender, RoutedEventArgs e)
        {
            CompleteDialog(false);
        }
    }
}
