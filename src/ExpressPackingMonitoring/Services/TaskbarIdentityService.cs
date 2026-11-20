using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using System.IO;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.Services;

internal static class TaskbarIdentityService
{
    internal const string AppUserModelId = "PackingProof.ExpressPackingMonitoring";

    internal static void TryApply()
    {
        // 包身份下由 MSIX 决定 AUMID，自己覆盖会让任务栏和启动项对不上。
        if (PackagingIdentity.IsPackaged)
            return;

        // 从 bin\Debug / bin\Release 直接运行时没有安装器创建的 AUMID 快捷方式。
        // 强行套用安装版身份会让 Windows 去解析失效或旧的快捷方式，任务栏最终
        // 显示空白文档图标；开发构建保留进程默认身份，直接使用 exe 内嵌图标。
        if (IsDevelopmentOutputPath(Environment.ProcessPath))
        {
            RuntimeLog.Info(
                "Startup",
                "Development build detected; using the executable taskbar identity");
            return;
        }

        try
        {
            int result = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            if (result < 0)
            {
                RuntimeLog.Warn(
                    "Startup",
                    $"Unable to set AppUserModelID HRESULT=0x{result:X8}");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Startup", $"Unable to set AppUserModelID: {ex.Message}");
        }
    }

    internal static bool IsDevelopmentOutputPath(string? executablePath)
    {
        string normalized = (executablePath ?? "")
            .Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
