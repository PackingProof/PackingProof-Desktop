using ExpressPackingMonitoring.Logging;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ExpressPackingMonitoring.Services;

internal static class RootLauncherStartupService
{
    internal const string RootLauncherMarkerOption = "--launched-by-root-launcher";
    internal const string WaitForProcessExitOption = "--wait-for-process-exit";

    internal static bool TryRedirectNormalStartup(string[] args)
    {
        if (!ShouldRedirect(args)
            || !LauncherUpdateService.TryResolveInstalledLauncher(
                AppContext.BaseDirectory,
                out string launcherPath))
        {
            return false;
        }

        try
        {
            ProcessStartInfo startInfo = BuildHandoffStartInfo(
                launcherPath,
                Environment.ProcessId,
                args);
            using Process? process = Process.Start(startInfo);
            if (process == null)
                return false;
            RuntimeLog.RecordShutdownRequest(
                "DirectAppLaunchRedirect",
                $"launcherPid={process.Id}");
            RuntimeLog.Info(
                "Startup",
                $"Direct app launch redirected through root launcher launcherPid={process.Id}");
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "Startup",
                $"Unable to redirect direct app launch through root launcher: {ex.Message}");
            return false;
        }
    }

    internal static bool ShouldRedirect(IReadOnlyList<string> args)
    {
        if (args.Any(arg => string.Equals(
                arg,
                RootLauncherMarkerOption,
                StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !IsMaintenanceCommand(args);
    }

    internal static bool IsMaintenanceCommand(IReadOnlyList<string> args)
    {
        return args.Any(arg =>
            arg.StartsWith("--uninstall-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--audio-check", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "--audio-probe", StringComparison.OrdinalIgnoreCase));
    }

    internal static ProcessStartInfo BuildHandoffStartInfo(
        string launcherPath,
        int currentProcessId,
        IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = launcherPath,
            WorkingDirectory = Path.GetDirectoryName(launcherPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(WaitForProcessExitOption);
        startInfo.ArgumentList.Add(currentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (string arg in args)
        {
            if (!string.Equals(arg, RootLauncherMarkerOption, StringComparison.OrdinalIgnoreCase))
                startInfo.ArgumentList.Add(arg);
        }
        return startInfo;
    }
}
