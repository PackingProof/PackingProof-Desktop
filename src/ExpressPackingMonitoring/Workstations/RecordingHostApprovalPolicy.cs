using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;

namespace ExpressPackingMonitoring;

internal enum BackupDeviceEnrollmentFailureKind
{
    None,
    Denied,
    TimedOut,
    ApprovalUnavailable,
    Busy,
    Unreachable,
    Incompatible,
    InvalidResponse,
    Failed
}

internal sealed class BackupDeviceEnrollmentException : InvalidOperationException
{
    public BackupDeviceEnrollmentException(
        BackupDeviceEnrollmentFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public BackupDeviceEnrollmentFailureKind Kind { get; }
}

internal static class RecordingHostApprovalPolicy
{
    // 保存主机自己的确认窗口最多等待 65 秒；再留出网络返回时间。
    internal static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(90);

    internal static bool IsPending(
        AppConfig? config,
        DateTimeOffset now)
    {
        if (config?.RecordingHostApprovalPending != true
            || config.RecordingHostApprovalRequestedAtUtc is not DateTime requestedAt)
        {
            return false;
        }

        DateTime requestedUtc = requestedAt.Kind == DateTimeKind.Utc
            ? requestedAt
            : requestedAt.ToUniversalTime();
        TimeSpan age = now.UtcDateTime - requestedUtc;
        return age >= TimeSpan.Zero && age <= PendingLifetime;
    }

    internal static string GetFailureCode(BackupDeviceEnrollmentFailureKind kind) =>
        kind == BackupDeviceEnrollmentFailureKind.None ? "" : kind.ToString();

    internal static string GetFailureMessage(string? code) =>
        code?.Trim() switch
        {
            nameof(BackupDeviceEnrollmentFailureKind.Denied) =>
                AppLanguage.Get("保存主机拒绝了本机连接申请，可重新申请或更换保存主机"),
            nameof(BackupDeviceEnrollmentFailureKind.TimedOut) =>
                AppLanguage.Get("等待保存主机允许连接超时，请确认保存主机界面已打开后重新申请"),
            nameof(BackupDeviceEnrollmentFailureKind.ApprovalUnavailable) =>
                AppLanguage.Get("保存主机暂时无法显示连接确认窗口，请打开保存主机界面后重试"),
            nameof(BackupDeviceEnrollmentFailureKind.Busy) =>
                AppLanguage.Get("保存主机正在确认其他设备，请稍后重新申请"),
            nameof(BackupDeviceEnrollmentFailureKind.Unreachable) =>
                AppLanguage.Get("无法连接保存主机，请检查网络后重试"),
            nameof(BackupDeviceEnrollmentFailureKind.Incompatible) =>
                AppLanguage.Get("保存主机版本不兼容，请更新后重新连接"),
            nameof(BackupDeviceEnrollmentFailureKind.InvalidResponse) =>
                AppLanguage.Get("保存主机返回的连接信息无效，请更新保存主机后重试"),
            nameof(BackupDeviceEnrollmentFailureKind.Failed) =>
                AppLanguage.Get("保存主机暂时无法处理连接申请，请稍后重试"),
            _ => ""
        };

    internal static string BuildReconnectReason(AppConfig? config)
    {
        string detail = GetFailureMessage(config?.LastRecordingHostApprovalFailureCode);
        return detail.Length == 0
            ? AppLanguage.Get("需要重新连接保存主机")
            : AppLanguage.Format("需要重新连接保存主机：{0}", detail);
    }
}
