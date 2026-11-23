using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ExpressPackingMonitoring.UpdateCore;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 以一次管理员授权完成 URL ACL 与 Windows 防火墙配置。
/// 防火墙规则直接写入 HNetCfg.FwPolicy2，不依赖可能缺失或被安全软件禁用的 netsh advfirewall。
/// </summary>
internal static class WindowsLanAccessSetup
{
    internal const string ConfigureCommandLineOption = "--configure-lan-access";
    internal const int UrlAclAddFailureExitCode = 11;
    internal const int TcpFirewallAddFailureExitCode = 12;
    internal const int UdpFirewallAddFailureExitCode = 13;
    internal const int UnexpectedFailureExitCode = 10;

    private const string PortOption = "--port";
    private const string UserSidOption = "--user-sid";
    private const string IncludeUrlAclOption = "--include-url-acl";
    private const string OutputOption = "--output";

    internal sealed record ConfigurationRequest(
        int Port,
        string UserSid,
        bool IncludeUrlAcl,
        string OutputPath);

    internal sealed record FirewallRuleSpecification(
        string Name,
        int Protocol,
        int Port);

    private sealed class StepException : Exception
    {
        internal StepException(string step, int exitCode, string message, Exception? innerException = null)
            : base(message, innerException)
        {
            ExitCode = exitCode;
        }

        internal int ExitCode { get; }
    }

    internal static bool TryHandleCommandLine(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (!args.Any(arg => string.Equals(
                arg,
                ConfigureCommandLineOption,
                StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        exitCode = RunConfigurationCommand(args);
        return true;
    }

    internal static void RequestElevatedConfiguration(
        int port,
        string userSid,
        bool includeUrlAcl)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("当前平台不需要配置 Windows 局域网权限");

        string outputPath = Path.Combine(
            Path.GetTempPath(),
            $"{BrandMetadata.ArtifactPrefix}-lan-repair-{Guid.NewGuid():N}.log");
        try
        {
            ProcessStartInfo startInfo = BuildElevatedStartInfo(
                port,
                userSid,
                includeUrlAcl,
                outputPath);
            bool alreadyElevated = WindowsPlatformSupport.IsCurrentProcessElevated();
            Logging.RuntimeLog.Info(
                "Web",
                $"LAN access setup elevation requested alreadyElevated={alreadyElevated}");

            using Process? process = Process.Start(startInfo);
            if (process == null)
                throw new InvalidOperationException("配置局域网服务访问权限失败：无法启动管理员配置进程");

            if (!process.WaitForExit(20000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("配置局域网服务访问权限超时，请关闭 Web 服务后重试");
            }

            if (process.ExitCode != 0)
            {
                string output = ReadResultFile(outputPath);
                string failedStep = ResolveFailureStep(process.ExitCode, output);
                string detail = CompactOutput(output);
                throw new InvalidOperationException(
                    $"配置局域网服务访问权限失败，失败阶段：{failedStep}，退出码：{process.ExitCode}，输出：{detail}");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("配置局域网服务访问权限未完成：未获得管理员授权", ex);
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not TimeoutException)
        {
            throw new InvalidOperationException(
                "配置局域网服务访问权限失败，可能是用户取消了管理员授权或系统拒绝执行",
                ex);
        }
        finally
        {
            try { File.Delete(outputPath); } catch { }
        }
    }

    internal static ProcessStartInfo BuildElevatedStartInfo(
        int port,
        string userSid,
        bool includeUrlAcl,
        string outputPath)
    {
        ValidateConfiguration(port, userSid, outputPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = GetElevatedExecutablePath(),
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(ConfigureCommandLineOption);
        startInfo.ArgumentList.Add(PortOption);
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(UserSidOption);
        startInfo.ArgumentList.Add(userSid);
        startInfo.ArgumentList.Add(IncludeUrlAclOption);
        startInfo.ArgumentList.Add(includeUrlAcl ? "true" : "false");
        startInfo.ArgumentList.Add(OutputOption);
        startInfo.ArgumentList.Add(outputPath);
        return startInfo;
    }

    internal static bool TryParseConfigurationRequest(
        string[] args,
        out ConfigurationRequest? request,
        out string error)
    {
        request = null;
        int port = 0;
        string userSid = "";
        bool includeUrlAcl = true;
        string outputPath = "";

        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            if (string.Equals(option, ConfigureCommandLineOption, StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 >= args.Length)
            {
                error = $"参数 {option} 缺少值";
                return false;
            }

            string value = args[++i];
            if (string.Equals(option, PortOption, StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port))
                {
                    error = "端口不是有效整数";
                    return false;
                }
            }
            else if (string.Equals(option, UserSidOption, StringComparison.OrdinalIgnoreCase))
            {
                userSid = value.Trim();
            }
            else if (string.Equals(option, IncludeUrlAclOption, StringComparison.OrdinalIgnoreCase))
            {
                if (!bool.TryParse(value, out includeUrlAcl))
                {
                    error = "URL ACL 开关不是有效布尔值";
                    return false;
                }
            }
            else if (string.Equals(option, OutputOption, StringComparison.OrdinalIgnoreCase))
            {
                outputPath = value.Trim();
            }
            else
            {
                error = $"未知参数 {option}";
                return false;
            }
        }

        if (!TryValidateConfiguration(port, userSid, outputPath, out error))
            return false;

        request = new ConfigurationRequest(port, userSid, includeUrlAcl, outputPath);
        return true;
    }

    internal static IReadOnlyList<FirewallRuleSpecification> BuildFirewallRuleSpecifications(int port)
    {
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        return
        [
            new FirewallRuleSpecification(WebServer.FirewallRuleName, Protocol: 6, port),
            new FirewallRuleSpecification(
                WebServer.UdpFirewallRuleName,
                Protocol: 17,
                UdpDiscoveryProtocol.Port)
        ];
    }

    internal static string GetWindowsSystemExecutablePath(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName) || Path.GetFileName(executableName) != executableName)
            throw new ArgumentException("系统程序名称无效", nameof(executableName));

        string systemDirectory = Environment.SystemDirectory;
        return string.IsNullOrWhiteSpace(systemDirectory)
            ? executableName
            : Path.Combine(systemDirectory, executableName);
    }

    internal static string ResolveFailureStep(int exitCode, string output) =>
        exitCode switch
        {
            UrlAclAddFailureExitCode => "urlacl-add",
            TcpFirewallAddFailureExitCode => "firewall-tcp-add",
            UdpFirewallAddFailureExitCode => "firewall-udp-add",
            _ => ExtractLastStep(output)
        };

    internal static string ExtractLastStep(string output)
    {
        const string marker = "[LAN-STEP] ";
        string step = "unknown";
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex >= 0)
                step = line[(markerIndex + marker.Length)..].Trim();
        }
        return step;
    }

    internal static string CompactOutput(string output)
    {
        string compact = string.Join(
            " | ",
            output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(compact))
            return "无命令输出";
        return compact.Length <= 4000 ? compact : compact[..4000] + "…";
    }

    private static int RunConfigurationCommand(string[] args)
    {
        if (!TryParseConfigurationRequest(args, out ConfigurationRequest? request, out _)
            || request == null)
        {
            return UnexpectedFailureExitCode;
        }

        var log = new StringBuilder();
        try
        {
            if (request.IncludeUrlAcl)
                EnsureUrlAcl(request.Port, request.UserSid, log);
            foreach (FirewallRuleSpecification rule in BuildFirewallRuleSpecifications(request.Port))
                ReplaceFirewallRule(rule, log);
            File.WriteAllText(request.OutputPath, log.ToString(), new UTF8Encoding(false));
            return 0;
        }
        catch (StepException ex)
        {
            AppendLine(log, ex.Message);
            TryWriteResult(request.OutputPath, log.ToString());
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            AppendLine(log, "[LAN-STEP] unexpected");
            AppendLine(log, ex.Message);
            TryWriteResult(request.OutputPath, log.ToString());
            return UnexpectedFailureExitCode;
        }
    }

    private static void EnsureUrlAcl(int port, string userSid, StringBuilder log)
    {
        AppendLine(log, "[LAN-STEP] urlacl-delete");
        _ = RunProcess(
            GetWindowsSystemExecutablePath("netsh.exe"),
            ["http", "delete", "urlacl", $"url=http://+:{port}/"],
            log);

        AppendLine(log, "[LAN-STEP] urlacl-add");
        ProcessResult result = RunProcess(
            GetWindowsSystemExecutablePath("netsh.exe"),
            ["http", "add", "urlacl", $"url=http://+:{port}/", $"sddl=D:(A;;GX;;;{userSid})"],
            log);
        if (result.ExitCode != 0)
        {
            throw new StepException(
                "urlacl-add",
                UrlAclAddFailureExitCode,
                $"netsh http 退出码：{result.ExitCode}");
        }
    }

    private static void ReplaceFirewallRule(
        FirewallRuleSpecification specification,
        StringBuilder log)
    {
        string step = specification.Protocol == 6 ? "firewall-tcp-add" : "firewall-udp-add";
        int failureExitCode = specification.Protocol == 6
            ? TcpFirewallAddFailureExitCode
            : UdpFirewallAddFailureExitCode;
        AppendLine(log, $"[LAN-STEP] {step}");

        object? policyObject = null;
        object? ruleObject = null;
        try
        {
            if (!WindowsPlatformSupport.TryCreateComInstance("HNetCfg.FwPolicy2", out policyObject))
                throw new InvalidOperationException("无法创建 Windows 防火墙策略对象");
            if (!WindowsPlatformSupport.TryCreateComInstance("HNetCfg.FwRule", out ruleObject))
                throw new InvalidOperationException("无法创建 Windows 防火墙规则对象");

            dynamic policy = policyObject!;
            dynamic rule = ruleObject!;
            try { policy.Rules.Remove(specification.Name); } catch { }

            rule.Name = specification.Name;
            rule.Direction = 1;
            rule.Action = 1;
            rule.Protocol = specification.Protocol;
            rule.LocalPorts = specification.Port.ToString(CultureInfo.InvariantCulture);
            rule.Profiles = int.MaxValue;
            rule.InterfaceTypes = "All";
            rule.RemoteAddresses = "*";
            rule.EdgeTraversal = false;
            rule.Enabled = true;
            policy.Rules.Add(rule);
        }
        catch (Exception ex)
        {
            throw new StepException(step, failureExitCode, ex.Message, ex);
        }
        finally
        {
            WindowsPlatformSupport.ReleaseComObject(ruleObject);
            WindowsPlatformSupport.ReleaseComObject(policyObject);
        }
    }

    private static ProcessResult RunProcess(
        string executablePath,
        IReadOnlyList<string> arguments,
        StringBuilder log)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(executablePath)}");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{Path.GetFileName(executablePath)} 执行超时");
        }

        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(stdout))
            AppendLine(log, stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr))
            AppendLine(log, stderr.Trim());
        return new ProcessResult(process.ExitCode);
    }

    private static string GetElevatedExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && !string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        string applicationPath = Path.Combine(AppContext.BaseDirectory, "ExpressPackingMonitoring.exe");
        return File.Exists(applicationPath) ? applicationPath : processPath ?? applicationPath;
    }

    private static string ReadResultFile(string outputPath)
    {
        try
        {
            return File.Exists(outputPath) ? File.ReadAllText(outputPath, Encoding.UTF8) : "";
        }
        catch (Exception ex)
        {
            return $"读取配置结果失败：{ex.Message}";
        }
    }

    private static void TryWriteResult(string outputPath, string content)
    {
        try
        {
            File.WriteAllText(outputPath, content, new UTF8Encoding(false));
        }
        catch
        {
            // 调用方仍可依据退出码判断失败阶段。
        }
    }

    private static void ValidateConfiguration(int port, string userSid, string outputPath)
    {
        if (!TryValidateConfiguration(port, userSid, outputPath, out string error))
            throw new ArgumentException(error);
    }

    private static bool TryValidateConfiguration(
        int port,
        string userSid,
        string outputPath,
        out string error)
    {
        if (port is <= 0 or > 65535)
        {
            error = "端口必须介于 1 和 65535 之间";
            return false;
        }
        if (string.IsNullOrWhiteSpace(userSid))
        {
            error = "用户 SID 不能为空";
            return false;
        }
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            error = "配置结果路径不能为空";
            return false;
        }

        error = "";
        return true;
    }

    private static void AppendLine(StringBuilder log, string value)
    {
        if (log.Length > 0 && log[^1] != '\n')
            log.AppendLine();
        log.AppendLine(value);
    }

    private readonly record struct ProcessResult(int ExitCode);
}
