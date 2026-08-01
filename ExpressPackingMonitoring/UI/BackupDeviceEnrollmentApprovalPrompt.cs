using System.Windows;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI;

internal static class BackupDeviceEnrollmentApprovalPrompt
{
    internal static BackupDeviceEnrollmentApprovalDecision Show(
        Window? requestedOwner,
        BackupDeviceEnrollmentRequest request)
    {
        Application? application = Application.Current;
        if (application?.Dispatcher == null || application.Dispatcher.HasShutdownStarted)
            return BackupDeviceEnrollmentApprovalDecision.Unavailable;

        try
        {
            return application.Dispatcher.CheckAccess()
                ? ShowCore(requestedOwner, request)
                : application.Dispatcher.Invoke(() => ShowCore(requestedOwner, request));
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("BackupEnrollment", "Unable to display approval prompt", ex);
            return BackupDeviceEnrollmentApprovalDecision.Unavailable;
        }
    }

    private static BackupDeviceEnrollmentApprovalDecision ShowCore(
        Window? requestedOwner,
        BackupDeviceEnrollmentRequest request)
    {
        Window? owner = requestedOwner is { IsLoaded: true }
            ? requestedOwner
            : Application.Current?.Windows.OfType<Window>()
                .FirstOrDefault(window => window.IsLoaded && window.IsVisible);
        if (owner == null)
            return BackupDeviceEnrollmentApprovalDecision.Unavailable;

        if (!owner.IsVisible)
            owner.Show();
        if (owner.WindowState == WindowState.Minimized)
            owner.WindowState = WindowState.Normal;
        owner.Activate();

        string kind = string.Equals(request.DeviceKind, "pc", StringComparison.OrdinalIgnoreCase)
            ? "录制电脑"
            : "手机";
        string name = string.IsNullOrWhiteSpace(request.DeviceName) ? kind : request.DeviceName;
        RuntimeLog.Info("BackupEnrollment", $"Showing approval prompt deviceKind={request.DeviceKind}, remote={request.RemoteAddress}");
        bool approved = AppDialog.Confirm(
            owner,
            $"{name}（{request.RemoteAddress}）申请连接这台保存主机。允许后，该设备只能上传、查看和确认自己录制的录像",
            "允许设备连接？",
            confirmText: "允许连接",
            cancelText: "拒绝",
            severity: AppDialogSeverity.Information);
        return approved
            ? BackupDeviceEnrollmentApprovalDecision.Approved
            : BackupDeviceEnrollmentApprovalDecision.Denied;
    }
}
