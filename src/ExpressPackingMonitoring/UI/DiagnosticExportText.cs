using ExpressPackingMonitoring.Localization;

namespace ExpressPackingMonitoring.UI;

internal static class DiagnosticExportText
{
    internal static string BuildMessage(
        string zipPath,
        long archiveBytes,
        string feedbackEmail,
        IReadOnlyList<string> warnings)
    {
        string message = string.Join(
            "\n\n",
            AppLanguage.Format("诊断包已生成：{0}", zipPath),
            AppLanguage.Format("大小：{0}", FormatBytes(archiveBytes)),
            AppLanguage.Get("已复制路径并打开所在文件夹"),
            AppLanguage.Format(
                "点击“复制邮箱地址”会复制反馈邮箱 {0}，把刚生成的压缩包作为附件发过去即可",
                feedbackEmail),
            AppLanguage.Get("诊断包隐私提示"));
        if (warnings.Count > 0)
            message += "\n\n" + AppLanguage.Get("提示：") + "\n" + string.Join("\n", warnings.Take(10));
        return message;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
