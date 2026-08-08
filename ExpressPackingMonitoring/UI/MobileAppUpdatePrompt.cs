using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using System.Windows;

namespace ExpressPackingMonitoring.UI;

internal static class MobileAppUpdatePrompt
{
    private static MobileAppUpdatePromptWindow? _visiblePrompt;

    internal static void Show(Window owner, MobileAppUpdateAvailableInfo update)
    {
        string deviceName = string.IsNullOrWhiteSpace(update.DeviceName)
            ? "已连接手机"
            : update.DeviceName;
        ShowNonModal(
            owner,
            $"无法确认 {deviceName} 的手机 App 版本\n\n"
            + "请在手机 App 的“设置 - 关于”中检查更新；暂不处理不会影响电脑录像",
            update.LatestRelease.DownloadUrl);
    }

    private static void ShowNonModal(Window owner, string message, string downloadUrl)
    {
        if (_visiblePrompt is { IsLoaded: true })
            _visiblePrompt.Close();

        var prompt = new MobileAppUpdatePromptWindow(
            message,
            () => OpenDownloadPage(owner, downloadUrl))
        {
            Owner = owner
        };
        prompt.Closed += (_, _) =>
        {
            if (ReferenceEquals(_visiblePrompt, prompt))
                _visiblePrompt = null;
        };
        _visiblePrompt = prompt;
        prompt.Show();
    }

    private static void OpenDownloadPage(Window owner, string downloadUrl)
    {
        try
        {
            UpdateCheckService.OpenDownloadPage(downloadUrl);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MobileUpdate", "Open mobile app download page failed", ex);
            AppDialog.Error(
                owner,
                "打开手机版下载页面失败，请稍后重试",
                "手机 App 更新");
        }
    }
}
