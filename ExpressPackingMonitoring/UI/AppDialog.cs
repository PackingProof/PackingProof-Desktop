using System.Windows;
using System.Windows.Threading;
using ExpressPackingMonitoring.Localization;

namespace ExpressPackingMonitoring.UI;

public enum AppDialogSeverity
{
    /// <summary>中性结果或成功说明，无需用户决策；图标为 i（蓝色）。</summary>
    Information,

    /// <summary>可继续但需要用户注意或决策，或结果部分成功；图标为 !（橙色）。</summary>
    Warning,

    /// <summary>操作失败或出现异常/不可用状态；图标为 X（红色）。</summary>
    Error
}

public static class AppDialog
{
    /// <summary>中性信息弹窗（蓝色 i 图标），用于纯结果或无需用户决策的说明。</summary>
    public static void Information(
        Window? owner,
        string message,
        string title,
        string? buttonText = null) =>
        ShowMessage(owner, message, title, AppDialogSeverity.Information, buttonText);

    /// <summary>警告弹窗（橙色 ! 图标），用于需要用户注意或决策、或结果部分成功的场景。</summary>
    public static void Warning(
        Window? owner,
        string message,
        string title,
        string? buttonText = null) =>
        ShowMessage(owner, message, title, AppDialogSeverity.Warning, buttonText);

    /// <summary>错误弹窗（红色 X 图标），用于操作失败或异常/不可用状态。</summary>
    public static void Error(
        Window? owner,
        string message,
        string title,
        string? buttonText = null) =>
        ShowMessage(owner, message, title, AppDialogSeverity.Error, buttonText);

    /// <summary>
    /// 统一弹窗核心实现；仅用于严重度由运行时结果动态决定或语义方法内部。
    /// 普通调用请使用 <see cref="Information"/>、<see cref="Warning"/>、<see cref="Error"/>。
    /// </summary>
    internal static void ShowMessage(
        Window? owner,
        string message,
        string title,
        AppDialogSeverity severity,
        string? buttonText = null)
    {
        InvokeOnUiThread(() =>
        {
            var dialog = new ConfirmDialog(
                message,
                title,
                confirmText: buttonText ?? AppLanguage.Get("确定"),
                isDangerous: false,
                showCancelButton: false,
                severity: severity);
            ShowOwned(dialog, owner);
            return true;
        });
    }

    public static bool Confirm(
        Window? owner,
        string message,
        string title,
        AppDialogSeverity severity,
        string? confirmText = null,
        string? cancelText = null,
        bool isDangerous = false)
    {
        return InvokeOnUiThread(() =>
        {
            var dialog = new ConfirmDialog(
                message,
                title,
                confirmText ?? AppLanguage.Get("确定"),
                cancelText ?? AppLanguage.Get("取消"),
                isDangerous,
                showCancelButton: true,
                severity);
            return ShowOwned(dialog, owner);
        });
    }

    private static bool ShowOwned(ConfirmDialog dialog, Window? requestedOwner)
    {
        Window? owner = ResolveOwner(requestedOwner);
        if (owner != null && owner != dialog)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return dialog.ShowDialog() == true;
    }

    private static Window? ResolveOwner(Window? requestedOwner)
    {
        if (requestedOwner is { IsLoaded: true })
            return requestedOwner;

        Application? application = Application.Current;
        if (application == null)
            return null;

        return application.Windows
                   .OfType<Window>()
                   .FirstOrDefault(window => window.IsActive && window.IsLoaded)
               ?? (application.MainWindow is { IsLoaded: true } mainWindow ? mainWindow : null);
    }

    private static T InvokeOnUiThread<T>(Func<T> action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            return action();
        return dispatcher.Invoke(action);
    }
}
