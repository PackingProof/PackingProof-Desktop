using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI;

/// <summary>统一弹窗与 Toast 的严重度图标/颜色映射，避免各窗口各自维护一份导致分叉。</summary>
internal static class NotificationVisuals
{
    public static string GetIconKey(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Error => "FluentDismissIcon",
        ToastSeverity.Warning => "FluentWarningIcon",
        ToastSeverity.Information => "FluentInfoIcon",
        _ => "FluentCheckIcon"
    };

    public static string GetBrushKey(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Error => "AccentRed",
        ToastSeverity.Warning => "AccentOrange",
        ToastSeverity.Information => "AccentBlue",
        _ => "AccentGreen"
    };
}
