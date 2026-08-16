using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI;

public partial class BackupDeviceEnrollmentApprovalWindow : Window
{
    private static readonly TimeSpan ApprovalCountdownDuration = TimeSpan.FromSeconds(60);
    private readonly DispatcherTimer _countdownTimer;
    private readonly DateTime _countdownDeadlineUtc;
    private bool _completed;

    internal BackupDeviceEnrollmentApprovalWindow(BackupDeviceEnrollmentRequest request)
    {
        InitializeComponent();
        bool isWorkstation = string.Equals(request.DeviceKind, "pc", StringComparison.OrdinalIgnoreCase);
        bool isViewer = string.Equals(request.DeviceKind, "viewer", StringComparison.OrdinalIgnoreCase);
        string kind = isWorkstation
            ? "录制电脑"
            : isViewer
                ? "电脑查看端"
                : "手机";
        string name = string.IsNullOrWhiteSpace(request.DeviceName) ? kind : request.DeviceName;
        string permission = isWorkstation
            ? "允许后，这台录制电脑可以上传、查看和确认自己录制的录像"
            : isViewer
                ? "允许后，这台查看端可以查看、播放、下载和剪辑保存主机中的录像；不能删除主机录像或修改设置"
                : "允许后，这台手机可以上传自己的录像，并查看、播放、下载和剪辑保存主机中的录像；不能删除主机录像或修改设置";
        string location = string.IsNullOrWhiteSpace(request.Platform)
            ? request.RemoteAddress
            : $"{request.Platform} · {request.RemoteAddress}";
        MessageText.Text = $"{name}（{location}）申请连接这台保存主机。{permission}";

        _countdownDeadlineUtc = DateTime.UtcNow.Add(ApprovalCountdownDuration);
        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += CountdownTimer_Tick;
        _countdownTimer.Start();
        UpdateCountdownText();
    }

    internal event Action<BackupDeviceEnrollmentApprovalDecision>? Completed;

    internal static bool TryGetRemainingDisplaySeconds(TimeSpan remaining, out int displaySeconds)
    {
        if (remaining <= TimeSpan.Zero)
        {
            displaySeconds = 0;
            return false;
        }

        displaySeconds = (int)Math.Ceiling(remaining.TotalSeconds);
        return true;
    }

    private void Approve_Click(object sender, RoutedEventArgs e) =>
        Complete(BackupDeviceEnrollmentApprovalDecision.Approved);

    private void Deny_Click(object sender, RoutedEventArgs e) =>
        Complete(BackupDeviceEnrollmentApprovalDecision.Denied);

    private void CountdownTimer_Tick(object? sender, EventArgs e) =>
        UpdateCountdownText();

    private void UpdateCountdownText()
    {
        TimeSpan remaining = _countdownDeadlineUtc - DateTime.UtcNow;
        if (!TryGetRemainingDisplaySeconds(remaining, out int displaySeconds))
        {
            Complete(BackupDeviceEnrollmentApprovalDecision.Unavailable);
            return;
        }

        CountdownText.Text = $"{displaySeconds} 秒内未处理，本次申请将自动取消";
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Complete(BackupDeviceEnrollmentApprovalDecision.Denied);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _countdownTimer.Stop();
        if (!_completed)
            Complete(BackupDeviceEnrollmentApprovalDecision.Denied, closeWindow: false);
    }

    private void Complete(
        BackupDeviceEnrollmentApprovalDecision decision,
        bool closeWindow = true)
    {
        if (_completed)
            return;

        _completed = true;
        _countdownTimer.Stop();
        Completed?.Invoke(decision);
        if (closeWindow)
            Close();
    }
}
