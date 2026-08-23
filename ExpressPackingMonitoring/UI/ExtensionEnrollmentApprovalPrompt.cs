using System.Windows;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI;

internal static class ExtensionEnrollmentApprovalPrompt
{
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(65);

    internal static ExtensionEnrollmentApprovalResult Show(
        Window? requestedOwner,
        ExtensionEnrollmentRequest request,
        string localOriginNodeId,
        string localOriginNodeName)
    {
        Application? application = Application.Current;
        if (application?.Dispatcher == null || application.Dispatcher.HasShutdownStarted)
            return Unavailable();
        if (application.Dispatcher.CheckAccess())
        {
            RuntimeLog.Warn("ExtensionEnrollment", "Approval prompt was requested on the UI thread");
            return Unavailable();
        }

        var completion = new TaskCompletionSource<ExtensionEnrollmentApprovalResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ExtensionEnrollmentApprovalWindow? shownPrompt = null;
        try
        {
            _ = application.Dispatcher.InvokeAsync(() =>
                shownPrompt = ShowCore(
                    requestedOwner,
                    request,
                    localOriginNodeId,
                    localOriginNodeName,
                    completion));
            if (completion.Task.Wait(ApprovalTimeout))
                return completion.Task.GetAwaiter().GetResult();

            completion.TrySetResult(Unavailable());
            _ = application.Dispatcher.InvokeAsync(() =>
            {
                if (shownPrompt?.IsVisible == true)
                    shownPrompt.Close();
            });
            return Unavailable();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("ExtensionEnrollment", "Unable to display approval prompt", ex);
            return Unavailable();
        }
    }

    private static ExtensionEnrollmentApprovalWindow? ShowCore(
        Window? requestedOwner,
        ExtensionEnrollmentRequest request,
        string localOriginNodeId,
        string localOriginNodeName,
        TaskCompletionSource<ExtensionEnrollmentApprovalResult> completion)
    {
        if (completion.Task.IsCompleted)
            return null;
        Window? owner = requestedOwner is { IsLoaded: true }
            ? requestedOwner
            : Application.Current?.Windows.OfType<Window>()
                .FirstOrDefault(window => window.IsLoaded && window.IsVisible);
        if (owner == null)
        {
            completion.TrySetResult(Unavailable());
            return null;
        }
        if (!owner.IsVisible) owner.Show();
        if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
        owner.Activate();

        RuntimeLog.Info(
            "ExtensionEnrollment",
            $"Showing approval prompt extension={Safe(request.ExtensionInstanceId)}, remote={Safe(request.RemoteAddress)}");
        var prompt = new ExtensionEnrollmentApprovalWindow(
            request,
            localOriginNodeId,
            localOriginNodeName)
        {
            Owner = owner
        };
        prompt.Completed += result => completion.TrySetResult(result);
        prompt.Show();
        prompt.Activate();
        return prompt;
    }

    private static ExtensionEnrollmentApprovalResult Unavailable() => new()
    {
        Disposition = ExtensionEnrollmentApprovalDisposition.Unavailable
    };

    private static string Safe(string? value)
    {
        string normalized = value?.Trim() ?? "";
        return normalized.Length <= 24 ? normalized : normalized[..24];
    }
}
