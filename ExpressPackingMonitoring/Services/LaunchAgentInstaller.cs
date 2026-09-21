using System.Diagnostics;
using System.Reflection;
using System.Text;
using ExpressPackingMonitoring.Config;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 把保存主机注册成登录自启的后台服务（launchd LaunchAgent）。
///
/// 装在用户自己的 ~/Library/LaunchAgents 下：不需要管理员授权，
/// 登录后自动拉起，崩溃自动重启；不传 --no-dialog，设备接入确认才能弹出来。
/// </summary>
internal static class LaunchAgentInstaller
{
    private const string Label = "com.packingproof.host";

    internal static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "LaunchAgents",
        $"{Label}.plist");

    internal static bool IsInstalled => File.Exists(PlistPath);

    internal static bool TryInstall(out string error)
    {
        error = "";
        if (!OperatingSystem.IsMacOS())
        {
            error = "开机自启目前只支持 macOS";
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            Directory.CreateDirectory(AppPaths.LogDir);
            File.WriteAllText(PlistPath, BuildPlist(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            error = $"写入 LaunchAgent 失败：{ex.Message}";
            return false;
        }

        // 已加载过就先卸掉，避免同一标签重复注册
        TryRunLaunchctl("bootout", $"gui/{GetUserId()}", Label);
        if (!TryRunLaunchctl("bootstrap", $"gui/{GetUserId()}", PlistPath)
            && !TryRunLaunchctl("load", "-w", PlistPath))
        {
            // 保留 plist 但明确报错，不假装注册成功
            error = "注册开机自启失败：launchctl 未能加载该服务";
            return false;
        }

        return true;
    }

    internal static bool TryUninstall(out string error)
    {
        error = "";
        if (!OperatingSystem.IsMacOS())
        {
            error = "开机自启目前只支持 macOS";
            return false;
        }

        if (!TryRunLaunchctl("bootout", $"gui/{GetUserId()}", Label))
            TryRunLaunchctl("unload", "-w", PlistPath);

        try
        {
            if (IsInstalled) File.Delete(PlistPath);
        }
        catch (Exception ex)
        {
            error = $"删除 LaunchAgent 文件失败：{ex.Message}";
            return false;
        }

        return true;
    }

    private static string BuildPlist()
    {
        var arguments = new List<string>();
        string processPath = Environment.ProcessPath ?? "";
        string entryPath = Assembly.GetEntryAssembly()?.Location ?? "";
        arguments.Add(processPath);
        // 用 dotnet 运行 dll 时要把入口程序集也写进去；自包含可执行文件只写自己
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entryPath))
        {
            arguments.Add(entryPath);
        }

        // 后台服务不自动开浏览器，但保留对话框：设备接入仍需要主人点确认
        arguments.Add("--no-browser");
        // 服务模式：登录时若配置不全只记日志，不弹设置窗口打断用户
        arguments.Add("--service");

        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        builder.AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
        builder.AppendLine("<plist version=\"1.0\">");
        builder.AppendLine("<dict>");
        builder.AppendLine("  <key>Label</key>");
        builder.AppendLine($"  <string>{Label}</string>");
        builder.AppendLine("  <key>ProgramArguments</key>");
        builder.AppendLine("  <array>");
        foreach (string argument in arguments)
            builder.AppendLine($"    <string>{Escape(argument)}</string>");
        builder.AppendLine("  </array>");
        builder.AppendLine("  <key>WorkingDirectory</key>");
        builder.AppendLine($"  <string>{Escape(AppContext.BaseDirectory)}</string>");
        // 用 dotnet 运行 dll 时，运行时的滚动策略要跟着走，否则服务用的运行时和交互运行不一致。
        // 自包含发布不需要这一项。
        string? rollForward = Environment.GetEnvironmentVariable("DOTNET_ROLL_FORWARD");
        if (!string.IsNullOrWhiteSpace(rollForward))
        {
            builder.AppendLine("  <key>EnvironmentVariables</key>");
            builder.AppendLine("  <dict>");
            builder.AppendLine("    <key>DOTNET_ROLL_FORWARD</key>");
            builder.AppendLine($"    <string>{Escape(rollForward)}</string>");
            builder.AppendLine("  </dict>");
        }

        builder.AppendLine("  <key>RunAtLoad</key>");
        builder.AppendLine("  <true/>");
        builder.AppendLine("  <key>KeepAlive</key>");
        builder.AppendLine("  <dict>");
        builder.AppendLine("    <key>SuccessfulExit</key>");
        builder.AppendLine("    <false/>");
        builder.AppendLine("  </dict>");
        builder.AppendLine("  <key>ProcessType</key>");
        builder.AppendLine("  <string>Background</string>");
        // 配置不全时服务会退出；节流避免立刻重启形成死循环刷屏
        builder.AppendLine("  <key>ThrottleInterval</key>");
        builder.AppendLine("  <integer>60</integer>");
        builder.AppendLine("  <key>StandardOutPath</key>");
        builder.AppendLine($"  <string>{Escape(Path.Combine(AppPaths.LogDir, "host-launchd.out.log"))}</string>");
        builder.AppendLine("  <key>StandardErrorPath</key>");
        builder.AppendLine($"  <string>{Escape(Path.Combine(AppPaths.LogDir, "host-launchd.err.log"))}</string>");
        builder.AppendLine("</dict>");
        builder.AppendLine("</plist>");
        return builder.ToString();
    }

    private static string GetUserId()
    {
        string? userId = Environment.GetEnvironmentVariable("UID");
        if (!string.IsNullOrWhiteSpace(userId)) return userId.Trim();
        return RunProcess("/usr/bin/id", ["-u"], out string output) ? output.Trim() : "";
    }

    private static bool TryRunLaunchctl(params string[] arguments) =>
        RunProcess("/bin/launchctl", arguments, out _);

    private static bool RunProcess(string fileName, IReadOnlyList<string> arguments, out string error)
    {
        error = "";
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                error = "无法启动进程";
                return false;
            }

            string standardError = process.StandardError.ReadToEnd().Trim();
            string standardOutput = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(60_000);
            if (process.ExitCode == 0) return true;
            error = standardError.Length > 0 ? standardError : standardOutput;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
