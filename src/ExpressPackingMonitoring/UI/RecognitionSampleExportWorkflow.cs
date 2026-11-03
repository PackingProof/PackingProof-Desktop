using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ExpressPackingMonitoring.UI;

internal static class RecognitionSampleExportWorkflow
{
    internal static void Run(Window owner, string userDataDir, string contributorNodeId)
    {
        try
        {
            var dialog = new RecognitionSampleExportProgressDialog(
                userDataDir,
                contributorNodeId)
            {
                Owner = owner
            };
            dialog.ShowDialog();
            if (dialog.Outcome == RecognitionSampleExportOutcome.Failed)
            {
                AppDialog.Error(
                    owner,
                    AppLanguage.Format("样本包生成失败：{0}", dialog.FailureMessage),
                    AppLanguage.Get("导出样本包"));
                return;
            }
            if (dialog.Outcome != RecognitionSampleExportOutcome.Success
                || dialog.Result is not RecordingSamplePackageResult result)
            {
                return;
            }

            ShowCompleted(owner, result);
        }
        catch (Exception ex)
        {
            AppDialog.Error(
                owner,
                AppLanguage.Format("样本包生成失败：{0}", ex.Message),
                AppLanguage.Get("导出样本包"));
        }
    }

    private static void ShowCompleted(Window owner, RecordingSamplePackageResult result)
    {
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
            AppLanguage.Get("已复制路径并打开所在文件夹") + "\n\n" +
            AppLanguage.Format(
                "请检查画面内容后，通过微信发送给 PackingProof 维护者，或发送到邮箱 {0}",
                FeedbackContact.Email);
        bool copyEmail = AppDialog.Confirm(
            owner,
            message,
            AppLanguage.Get("导出样本包"),
            AppDialogSeverity.Information,
            confirmText: AppLanguage.Get("复制邮箱地址"),
            cancelText: AppLanguage.Get("关闭"),
            isDangerous: false);
        if (!copyEmail)
            return;

        bool copied = ClipboardHelper.TryCopyText(FeedbackContact.Email, () => { });
        if (copied)
        {
            AppDialog.Information(
                owner,
                AppLanguage.Format("已复制反馈邮箱：{0}", FeedbackContact.Email) + "\n\n" +
                AppLanguage.Get("请把刚生成的样本包作为附件发送") + "\n" +
                Path.GetFileName(result.ZipPath),
                AppLanguage.Get("导出样本包"),
                AppLanguage.Get("知道了"));
        }
        else
        {
            AppDialog.Error(
                owner,
                AppLanguage.Format(
                    "没能写入剪贴板，请手动记录反馈邮箱：{0}",
                    FeedbackContact.Email),
                AppLanguage.Get("导出样本包"));
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
