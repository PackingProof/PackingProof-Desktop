using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.UI;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ExpressPackingMonitoring;

/// <summary>
/// WorkstationNetwork 的 WPF 部分：重启程序并关闭窗口。
/// 拆分出来是为了让不引用 WPF 的宿主（例如 macOS 保存主机）复用它其余的网络与发现逻辑。
/// </summary>
public static partial class WorkstationNetwork
{
    public static bool TryRestartApplication(string reason = "unspecified", Window? owner = null)
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return false;

            if (!TryScheduleRestart(exePath, AppContext.BaseDirectory, reason))
                return false;

            RuntimeLog.RecordShutdownRequest("ApplicationRestart", reason);
            RuntimeLog.Info("Restart",
                $"Replacement process scheduled after resource cleanup currentPid={Environment.ProcessId}, reason={reason}");
            try
            {
                if (owner != null)
                    owner.Close();
                else
                    Application.Current.Shutdown();
            }
            catch
            {
                CancelPendingRestart();
                throw;
            }
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("Restart", $"Failed to restart application reason={reason}", ex);
            return false;
        }
    }

    public static bool RestartAfterPurposeChange(Window? owner = null)
    {
        if (TryRestartApplication("workstation-role-change", owner))
            return true;

        AppDialog.Error(
            owner,
            "自动重启失败，请手动关闭后重新打开程序",
            "切换用途");
        return false;
    }
}
