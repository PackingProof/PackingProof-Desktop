using System.Diagnostics;
using System.IO;

namespace ExpressPackingMonitoring.Services.Extensions;

internal sealed record RunningExtensionProcess(int ProcessId, string ProcessName);

internal static class ExtensionProcessManager
{
    internal static IReadOnlyList<RunningExtensionProcess> FindRunningProcesses(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
            return [];

        var running = new List<RunningExtensionProcess>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string? executablePath = process.MainModule?.FileName;
                    if (executablePath != null && IsExecutableInDirectory(executablePath, installDirectory))
                        running.Add(new RunningExtensionProcess(process.Id, process.ProcessName));
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
                catch (NotSupportedException)
                {
                }
            }
        }

        return running;
    }

    internal static bool TryTerminateProcesses(
        string installDirectory,
        IReadOnlyList<RunningExtensionProcess> runningProcesses,
        out string error)
    {
        var failures = new List<string>();
        foreach (RunningExtensionProcess running in runningProcesses)
        {
            try
            {
                using Process process = Process.GetProcessById(running.ProcessId);
                if (process.HasExited) continue;
                string? executablePath = process.MainModule?.FileName;
                if (executablePath == null || !IsExecutableInDirectory(executablePath, installDirectory))
                {
                    failures.Add($"{running.ProcessName}（PID {running.ProcessId}）身份已变化");
                    continue;
                }

                process.Kill();
                if (!process.WaitForExit(3000))
                    failures.Add($"{running.ProcessName}（PID {running.ProcessId}）未在限定时间内退出");
            }
            catch (ArgumentException)
            {
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
            {
                failures.Add($"{running.ProcessName}（PID {running.ProcessId}）：{ex.Message}");
            }
        }

        error = string.Join("\n", failures);
        return failures.Count == 0;
    }

    internal static bool IsExecutableInDirectory(string executablePath, string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(installDirectory))
            return false;

        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory))
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(executablePath);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
