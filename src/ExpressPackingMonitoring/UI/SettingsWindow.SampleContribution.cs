using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ExpressPackingMonitoring.UI
{
    public partial class SettingsWindow
    {
        private bool _recognitionSamplePackageRunning;

        private void ExportRecognitionSamples_Click(object sender, RoutedEventArgs e)
        {
            if (_recognitionSamplePackageRunning)
                return;

            bool confirmed = AppDialog.Confirm(
                this,
                AppLanguage.Get("将只读取本机录像并抽取少量画面，生成 ZIP 样本包。包内不含录像、数据库、配置或日志，但画面可能包含面单和人物信息，请确认后再发送"),
                AppLanguage.Get("导出样本包"),
                AppDialogSeverity.Warning,
                confirmText: AppLanguage.Get("开始抓取"),
                cancelText: AppLanguage.Get("取消"),
                isDangerous: false);
            if (!confirmed)
                return;

            _recognitionSamplePackageRunning = true;
            ExportRecognitionSamplesButton.IsEnabled = false;
            ExportRecognitionSamplesButton.Content = AppLanguage.Get("正在生成样本包");
            try
            {
                var dialog = new RecognitionSampleExportProgressDialog(
                    AppPaths.UserDataDir,
                    Config.NodeId)
                {
                    Owner = this
                };
                dialog.ShowDialog();
                if (dialog.Outcome == RecognitionSampleExportOutcome.Failed)
                {
                    AppDialog.Error(
                        this,
                        AppLanguage.Format("样本包生成失败：{0}", dialog.FailureMessage),
                        AppLanguage.Get("导出样本包"));
                    return;
                }
                if (dialog.Outcome != RecognitionSampleExportOutcome.Success
                    || dialog.Result is not RecordingSamplePackageResult result)
                {
                    return;
                }

                try { Clipboard.SetText(result.ZipPath); } catch { }
                try
                {
                    Process.Start(new ProcessStartInfo(
                        "explorer.exe",
                        $"/select,\"{result.ZipPath}\"") { UseShellExecute = true });
                }
                catch
                {
                }

                var archive = new FileInfo(result.ZipPath);
                string message =
                    AppLanguage.Get("样本包已生成") + "\n" +
                    result.ZipPath + "\n\n" +
                    AppLanguage.Format("图片：{0} 张", result.ImageCount) + "\n" +
                    AppLanguage.Format("大小：{0}", FormatBytes(archive.Length)) + "\n\n" +
                    AppLanguage.Get("已复制路径并打开所在文件夹");

                message += "\n\n" + AppLanguage.Format(
                    "请检查画面内容后，把压缩包发送到 {0}",
                    FeedbackEmail);
                bool copyEmail = AppDialog.Confirm(
                    this,
                    message,
                    AppLanguage.Get("导出样本包"),
                    AppDialogSeverity.Information,
                    confirmText: AppLanguage.Get("复制邮箱地址"),
                    cancelText: AppLanguage.Get("关闭"),
                    isDangerous: false);
                if (copyEmail)
                {
                    bool copied = ClipboardHelper.TryCopyText(FeedbackEmail, () => { });
                    if (copied)
                    {
                        AppDialog.Confirm(
                            this,
                            AppLanguage.Format("已复制反馈邮箱：{0}", FeedbackEmail) + "\n\n" +
                            AppLanguage.Get("请把刚生成的样本包作为附件发送") + "\n" +
                            Path.GetFileName(result.ZipPath),
                            AppLanguage.Get("导出样本包"),
                            AppDialogSeverity.Information,
                            confirmText: AppLanguage.Get("知道了"),
                            cancelText: AppLanguage.Get("关闭"),
                            isDangerous: false);
                    }
                    else
                    {
                        AppDialog.Error(
                            this,
                            AppLanguage.Format("没能写入剪贴板，请手动记录反馈邮箱：{0}", FeedbackEmail),
                            AppLanguage.Get("导出样本包"));
                    }
                }
            }
            catch (Exception ex)
            {
                AppDialog.Error(
                    this,
                    AppLanguage.Format("样本包生成失败：{0}", ex.Message),
                    AppLanguage.Get("导出样本包"));
            }
            finally
            {
                _recognitionSamplePackageRunning = false;
                ExportRecognitionSamplesButton.IsEnabled = true;
                ExportRecognitionSamplesButton.Content = AppLanguage.Get("导出样本包");
            }
        }
    }
}
