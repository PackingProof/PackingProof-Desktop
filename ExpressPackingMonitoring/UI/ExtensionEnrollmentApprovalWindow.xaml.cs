using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI;

public partial class ExtensionEnrollmentApprovalWindow : Window
{
    private static readonly TimeSpan ApprovalCountdownDuration = TimeSpan.FromSeconds(60);
    private readonly ExtensionEnrollmentRequest _request;
    private readonly string _localOriginNodeId;
    private readonly DispatcherTimer _countdownTimer;
    private readonly DateTime _countdownDeadlineUtc;
    private bool _completed;

    internal ExtensionEnrollmentApprovalWindow(
        ExtensionEnrollmentRequest request,
        string localOriginNodeId,
        string localOriginNodeName)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _localOriginNodeId = localOriginNodeId?.Trim() ?? "";
        InitializeComponent();

        string source = string.IsNullOrWhiteSpace(request.Source)
            ? AppLanguage.Get("Extension.EnrollmentSourceUnknown")
            : request.Source;
        string remote = string.IsNullOrWhiteSpace(request.RemoteAddress)
            ? AppLanguage.Get("Extension.EnrollmentAddressUnknown")
            : request.RemoteAddress;
        IdentityText.Text = AppLanguage.Format(
            "Extension.EnrollmentIdentity",
            request.DisplayName,
            request.Version,
            source,
            request.ProviderId,
            remote);
        AccessItems.ItemsSource = BuildAccessDescriptions(request);
        bool measurement = request.RequestedCapabilities.Contains(
            ExtensionScanCapabilities.MeasurementCapture,
            StringComparer.Ordinal);
        string nodeName = string.IsNullOrWhiteSpace(localOriginNodeName)
            ? _localOriginNodeId
            : localOriginNodeName.Trim();
        RoutingText.Text = measurement
            ? string.IsNullOrWhiteSpace(_localOriginNodeId)
                ? AppLanguage.Get("Extension.EnrollmentNoRecordingNode")
                : AppLanguage.Format("Extension.EnrollmentBoundNode", nodeName)
            : AppLanguage.Get("Extension.EnrollmentAllLocalNodes");

        _countdownDeadlineUtc = DateTime.UtcNow.Add(ApprovalCountdownDuration);
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += CountdownTimer_Tick;
        _countdownTimer.Start();
        UpdateCountdownText();
    }

    internal event Action<ExtensionEnrollmentApprovalResult>? Completed;

    internal static IReadOnlyList<string> BuildAccessDescriptions(
        ExtensionEnrollmentRequest request)
    {
        var descriptions = new List<string>();
        foreach (string capability in request.RequestedCapabilities.Distinct(StringComparer.Ordinal))
        {
            descriptions.Add(capability switch
            {
                ExtensionScanCapabilities.OrderLookup => AppLanguage.Get("Extension.Access.OrderLookup"),
                ExtensionScanCapabilities.RefundLookup => AppLanguage.Get("Extension.Access.RefundLookup"),
                ExtensionScanCapabilities.MeasurementCapture => AppLanguage.Get("Extension.Access.MeasurementCapture"),
                _ => AppLanguage.Format("Extension.Access.UnknownCapability", capability)
            });
        }
        foreach (string permission in request.RequestedPermissions
            .Where(permission => permission is not (
                ExtensionPermissions.ScanTasksRead
                or ExtensionPermissions.ScanResultsWrite
                or ExtensionPermissions.RecordingFieldsWrite))
            .Distinct(StringComparer.Ordinal))
        {
            descriptions.Add(permission switch
            {
                ExtensionPermissions.OrdersWrite => AppLanguage.Get("Extension.Access.OrdersWrite"),
                ExtensionPermissions.RecordingsActiveRead => AppLanguage.Get("Extension.Access.RecordingsActiveRead"),
                _ => AppLanguage.Format("Extension.Access.UnknownPermission", permission)
            });
        }
        return descriptions.Count == 0
            ? [AppLanguage.Get("Extension.Access.None")]
            : descriptions;
    }

    internal static bool TryGetRemainingDisplaySeconds(
        TimeSpan remaining,
        out int displaySeconds)
    {
        if (remaining <= TimeSpan.Zero)
        {
            displaySeconds = 0;
            return false;
        }
        displaySeconds = (int)Math.Ceiling(remaining.TotalSeconds);
        return true;
    }

    private void Approve_Click(object sender, RoutedEventArgs e)
    {
        bool measurement = _request.RequestedCapabilities.Contains(
            ExtensionScanCapabilities.MeasurementCapture,
            StringComparer.Ordinal);
        if (measurement && string.IsNullOrWhiteSpace(_localOriginNodeId))
        {
            Complete(new ExtensionEnrollmentApprovalResult
            {
                Disposition = ExtensionEnrollmentApprovalDisposition.Unavailable
            });
            return;
        }
        Complete(new ExtensionEnrollmentApprovalResult
        {
            Disposition = ExtensionEnrollmentApprovalDisposition.Approved,
            ApprovedPermissions = _request.RequestedPermissions,
            ApprovedCapabilities = _request.RequestedCapabilities,
            RoutingScope = measurement
                ? ExtensionRoutingScope.SelectedRecordingNodes
                : ExtensionRoutingScope.AllLocalRecordingNodes,
            BoundOriginNodeIds = measurement ? [_localOriginNodeId] : []
        });
    }

    private void Deny_Click(object sender, RoutedEventArgs e) => Complete(Denied());

    private void CountdownTimer_Tick(object? sender, EventArgs e) => UpdateCountdownText();

    private void UpdateCountdownText()
    {
        TimeSpan remaining = _countdownDeadlineUtc - DateTime.UtcNow;
        if (!TryGetRemainingDisplaySeconds(remaining, out int displaySeconds))
        {
            Complete(new ExtensionEnrollmentApprovalResult
            {
                Disposition = ExtensionEnrollmentApprovalDisposition.Unavailable
            });
            return;
        }
        CountdownText.Text = $"{displaySeconds} 秒内未处理，本次申请将自动取消";
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Complete(Denied());
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _countdownTimer.Stop();
        if (!_completed) Complete(Denied(), closeWindow: false);
    }

    private void Complete(
        ExtensionEnrollmentApprovalResult result,
        bool closeWindow = true)
    {
        if (_completed) return;
        _completed = true;
        _countdownTimer.Stop();
        Completed?.Invoke(result);
        if (closeWindow) Close();
    }

    private static ExtensionEnrollmentApprovalResult Denied() => new()
    {
        Disposition = ExtensionEnrollmentApprovalDisposition.Denied
    };
}
