using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 打开“可直接发送”的反馈邮件。优先用经典 Outlook 创建带附件的新邮件；
/// 没有 Outlook 时退回 .eml 草稿，再退回 mailto 普通邮件。
/// </summary>
internal static class FeedbackMailLauncher
{
    internal static bool TryOpenOutlookDraft(
        string recipientEmail,
        string subject,
        string body,
        string attachmentPath)
    {
        try
        {
            Type? outlookType = Type.GetTypeFromProgID("Outlook.Application");
            if (outlookType == null) return false;

            object? created = Activator.CreateInstance(outlookType);
            if (created == null) return false;
            dynamic outlook = created;
            dynamic mail = outlook.CreateItem(0); // olMailItem
            mail.To = recipientEmail;
            mail.Subject = subject;
            mail.Body = body;
            mail.Attachments.Add(attachmentPath);
            mail.Display(false);
            try { Marshal.FinalReleaseComObject(mail); } catch { }
            try { Marshal.FinalReleaseComObject(outlook); } catch { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryOpenEmlDraft(string emlPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(emlPath) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryOpenMailto(string recipientEmail, string subject, string body)
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                $"mailto:{recipientEmail}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}")
            {
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
