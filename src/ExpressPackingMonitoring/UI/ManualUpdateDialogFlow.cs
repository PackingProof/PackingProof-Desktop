using System.Windows;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UpdateCore;

namespace ExpressPackingMonitoring.UI;

internal static class ManualUpdateDialogFlow
{
    internal static async Task<UpdateCheckResult> CheckSetupEditionAsync(
        Window owner,
        AppConfig config,
        Func<bool> isRecording,
        Action restartRequested)
    {
        var service = new UpdateCheckService();
        bool allowPrerelease = UpdateChannelPolicy.ResolveAllowPrerelease(
            Environment.GetEnvironmentVariable(UpdateChannelPolicy.AllowPrereleaseKey),
            config.AllowPrereleaseUpdates);
        UpdateCheckResult result = await service.CheckManualAsync(
            allowPrerelease: allowPrerelease);
        if (!result.HasUpdate)
            return result;

        var dialog = new UpdateAvailableDialog(
            result,
            new AppPatchDownloadService(),
            isRecording);
        dialog.ShowAsDialog(owner);
        if (dialog.RestartRequested)
        {
            restartRequested();
            return result;
        }
        if (dialog.OpenFullDownloadPageRequested)
        {
            try
            {
                UpdateCheckService.OpenDownloadPage(dialog.DownloadUrl);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Update", "Open download page failed", ex);
                AppDialog.Error(
                    owner,
                    AppLanguage.Get("打开下载页面失败"),
                    AppLanguage.Get("检查更新"));
            }
        }
        return result;
    }
}
