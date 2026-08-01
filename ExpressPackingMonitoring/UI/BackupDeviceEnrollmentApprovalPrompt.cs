using System.Windows;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI;

internal static class BackupDeviceEnrollmentApprovalPrompt
{
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(65);

    internal static BackupDeviceEnrollmentApprovalDecision Show(
        Window? requestedOwner,
        BackupDeviceEnrollmentRequest request)
    {
        Application? application = Application.Current;
        if (application?.Dispatcher == null || application.Dispatcher.HasShutdownStarted)
            return BackupDeviceEnrollmentApprovalDecision.Unavailable;

        if (application.Dispatcher.CheckAccess())
        {
            RuntimeLog.Warn("BackupEnrollment", "Approval prompt was requested on the UI thread");
            return BackupDeviceEnrollmentApprovalDecision.Unavailable;
        }

        var completion = new TaskCompletionSource<BackupDeviceEnrollmentApprovalDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        BackupDeviceEnrollmentApprovalWindow? shownPrompt = null;
        try
        {
            _ = application.Dispatcher.InvokeAsync(() =>
                shownPrompt = ShowCore(requestedOwner, request, completion));
            if (completion.Task.Wait(ApprovalTimeout))
                return completion.Task.GetAwaiter().GetResult();

            completion.TrySetResult(BackupDeviceEnrollmentApprovalDecision.Unavailable);
            _ = application.Dispatcher.InvokeAsync(() =>
            {
                if (shownPrompt?.IsVisible == true)
                    shownPrompt.Close();
            });
            return BackupDeviceEnrollmentApprovalDecision.Unavailable;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("BackupEnrollment", "Unable to display approval prompt", ex);
            return BackupDeviceEnrollmentApprovalDecision.Unavailable;
        }
    }

    private static BackupDeviceEnrollmentApprovalWindow? ShowCore(
        Window? requestedOwner,
        BackupDeviceEnrollmentRequest request,
        TaskCompletionSource<BackupDeviceEnrollmentApprovalDecision> completion)
    {
        if (completion.Task.IsCompleted)
            return null;

        Window? owner = requestedOwner is { IsLoaded: true }
            ? requestedOwner
            : Application.Current?.Windows.OfType<Window>()
                .FirstOrDefault(window => window.IsLoaded && window.IsVisible);
        if (owner == null)
        {
            completion.TrySetResult(BackupDeviceEnrollmentApprovalDecision.Unavailable);
            return null;
        }

        if (!owner.IsVisible)
            owner.Show();
        if (owner.WindowState == WindowState.Minimized)
            owner.WindowState = WindowState.Normal;
        owner.Activate();

        RuntimeLog.Info("BackupEnrollment", $"Showing approval prompt deviceKind={request.DeviceKind}, remote={request.RemoteAddress}");
        var prompt = new BackupDeviceEnrollmentApprovalWindow(request) { Owner = owner };
        prompt.Completed += decision => completion.TrySetResult(decision);
        prompt.Show();
        prompt.Activate();
        return prompt;
    }
}
