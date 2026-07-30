using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using System.Windows;

namespace ExpressPackingMonitoring.UI;

internal static class MobileAppUpdatePrompt
{
    private static MobileAppUpdatePromptWindow? _visiblePrompt;

    internal static void ShowLatest(Window owner, MobileAppReleaseInfo release)
    {
        ShowNonModal(
            owner,
            $"手机版最新版本为 {release.Version}（内部版本 {release.BuildNumber}）\n\n"
            + "可前往手机版仓库下载更新",
            release.DownloadUrl);
    }

    internal static void Show(Window owner, MobileAppUpdateAvailableInfo update)
    {
        string deviceName = string.IsNullOrWhiteSpace(update.DeviceName)
            ? "已连接手机"
            : update.DeviceName;
        string currentVersion = string.IsNullOrWhiteSpace(update.CurrentVersion)
            ? update.CurrentBuildNumber > 0
                ? $"内部版本 {update.CurrentBuildNumber}"
                : "版本未知（可能是旧版）"
            : $"{update.CurrentVersion}（内部版本 {update.CurrentBuildNumber}）";
        string latestVersion =
            $"{update.LatestRelease.Version}（内部版本 {update.LatestRelease.BuildNumber}）";
        ShowNonModal(
            owner,
            $"检测到 {deviceName} 正在使用 {currentVersion}\n\n"
            + $"手机版最新版本为 {latestVersion}\n"
            + "建议前往下载更新；暂不更新时仍可继续使用当前可用功能",
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
            AppDialog.ShowMessage(
                owner,
                "打开手机版下载页面失败，请稍后重试",
                "手机 App 更新",
                AppDialogSeverity.Warning);
        }
    }
}
