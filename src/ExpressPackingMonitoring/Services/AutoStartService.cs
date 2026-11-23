using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.UpdateCore;
using Microsoft.Win32;
using System.IO;

namespace ExpressPackingMonitoring.Services;

internal static class AutoStartService
{
    private const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private static string AppName => BrandMetadata.InstanceNamePrefix;

    public static void Apply(bool enable)
    {
        if (PackagingIdentity.IsPackaged)
            return;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
            if (key == null)
                return;

            if (!enable)
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
                return;
            }

            string executablePath = ResolveStartupExecutable(
                Environment.ProcessPath ?? "",
                AppContext.BaseDirectory,
                File.Exists);
            if (!string.IsNullOrWhiteSpace(executablePath))
                key.SetValue(AppName, $"\"{executablePath}\"");
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("AutoStart", $"Failed to apply auto-start setting: {ex.Message}");
        }
    }

    public static bool IsEnabled()
    {
        if (PackagingIdentity.IsPackaged)
            return false;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 启动时要不要按当前 exe 位置重写自启动项。只有"装在标准布局里"的桌面端才写：
    /// 程序跑在 `&lt;安装目录&gt;\app\`、旁边有根启动器。开发构建、测试产物、发布脚本里的临时输出
    /// 跑起来时不能改用户的注册表——以前它们会把自启动项改指向 `TestResults\...` 这类临时目录，
    /// 用户下次开机就从构建输出启动（甚至输出被清理后直接启动不了）。
    /// 关闭自启动（false）不受这条限制：删的是我们自己那一个值，任何来源都可以做。
    /// </summary>
    internal static bool ShouldApplyOnStartup(
        string baseDirectory,
        Func<string, bool> fileExists)
    {
        if (PackagingIdentity.IsPackaged)
            return false;

        try
        {
            var runtimeDirectory = new DirectoryInfo(
                Path.GetFullPath(baseDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.Equals(runtimeDirectory.Name, "app", StringComparison.OrdinalIgnoreCase)
                && runtimeDirectory.Parent != null
                && fileExists(Path.Combine(runtimeDirectory.Parent.FullName, "ExpressPackingMonitoring.exe"));
        }
        catch
        {
            return false;
        }
    }

    internal static string ResolveStartupExecutable(
        string processPath,
        string baseDirectory,
        Func<string, bool> fileExists)
    {
        try
        {
            var runtimeDirectory = new DirectoryInfo(
                Path.GetFullPath(baseDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(runtimeDirectory.Name, "app", StringComparison.OrdinalIgnoreCase) &&
                runtimeDirectory.Parent != null)
            {
                string launcherPath = Path.Combine(runtimeDirectory.Parent.FullName, "ExpressPackingMonitoring.exe");
                if (fileExists(launcherPath))
                    return launcherPath;
            }
        }
        catch
        {
        }

        return !string.IsNullOrWhiteSpace(processPath) && fileExists(processPath)
            ? Path.GetFullPath(processPath)
            : "";
    }
}
