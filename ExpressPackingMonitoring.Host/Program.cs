using ExpressPackingMonitoring;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Host;

// Mac 端入口：先问清这台电脑的用途，再按用途运行保存主机或查看端。
//   （无参数）             首次打开会弹原生对话框问用途与存储位置
//   --purpose host|viewer  跳过询问，直接指定用途（自动化与排查用）
//   --storage <目录>       保存主机的录像保存位置
//   --switch-purpose       重新选择用途
//   --install-autostart    注册成登录自启的后台服务（仅保存主机）
//   --uninstall-autostart  取消开机自启
if (args.Any(argument => argument is "-h" or "--help"))
{
    Console.WriteLine("用法: ExpressPackingMonitoring.Host [--purpose host|viewer] [--storage <目录>] [--switch-purpose]"
        + " [--install-autostart] [--uninstall-autostart] [--no-browser] [--no-dialog]");
    Console.WriteLine("菜单栏壳用: --storage-summary | --set-storage-capacity <GB> [--storage-path <目录>]"
        + " | --set-storage-reserve <GB> [--storage-path <目录>]"
        + " | --list-hosts | --select-host <地址> [--host-node-id <id>] [--host-node-name <名字>]"
        + " | --forget-host | --viewer-status [--state searching]");
    return 0;
}

// 菜单栏壳的命令：只读/改本机配置，不走 HTTP，查看端也能用
if (await MenuCommands.TryRunAsync(args, CancellationToken.None))
    return 0;

HostOptions.Apply(args);
AppConfig config = WorkstationConfigStore.Load();
if (args.Any(argument => string.Equals(argument, "--switch-purpose", StringComparison.OrdinalIgnoreCase)))
{
    AppConfig.ResetDeploymentSetupForRetry(config);
}

string? purpose = ReadOption(args, "--purpose");
string? storageDirectory = ReadOption(args, "--storage");
bool installAutostart = args.Any(argument => string.Equals(argument, "--install-autostart", StringComparison.OrdinalIgnoreCase));
bool uninstallAutostart = args.Any(argument => string.Equals(argument, "--uninstall-autostart", StringComparison.OrdinalIgnoreCase));

if (uninstallAutostart)
{
    if (!LaunchAgentInstaller.TryUninstall(out string uninstallError))
    {
        Console.Error.WriteLine(uninstallError);
        return 2;
    }

    Console.WriteLine("已取消开机自启");
    return 0;
}

if (!string.IsNullOrWhiteSpace(purpose))
{
    if (!TryApplyPurpose(config, purpose, storageDirectory, out string applyError))
    {
        Console.Error.WriteLine(applyError);
        return 2;
    }

    config = WorkstationConfigStore.Load();
}
else if (MacDeploymentSetup.NeedsSetup(config))
{
    if (HostOptions.ServiceMode)
    {
        Console.Error.WriteLine("尚未选择用途，服务模式不弹窗；请先运行一次程序完成用途设置");
        return 2;
    }

    if (!MacDeploymentSetup.TryConfigure(config, out string setupError))
    {
        Console.Error.WriteLine(setupError);
        return 2;
    }

    config = WorkstationConfigStore.Load();
}

if (installAutostart)
{
    if (DeploymentPresets.Normalize(config.DeploymentPreset) != DeploymentPresets.MobileBackupHost)
    {
        Console.Error.WriteLine("只有保存主机才需要开机自启；当前用途是查看端");
        return 2;
    }

    if (!LaunchAgentInstaller.TryInstall(out string installError))
    {
        Console.Error.WriteLine(installError);
        return 2;
    }

    Console.WriteLine($"已注册开机自启：{LaunchAgentInstaller.PlistPath}");
    Console.WriteLine("登录后会自动启动保存主机；取消用 --uninstall-autostart");
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

Console.WriteLine($"当前用途：{DeploymentPresets.GetDisplayName(config.DeploymentPreset)}");
return DeploymentPresets.Normalize(config.DeploymentPreset) == DeploymentPresets.ViewerClient
    ? await ViewerSession.RunAsync(config, cancellation.Token)
    : await HostSession.RunAsync(config, cancellation.Token);

static string? ReadOption(string[] arguments, string name)
{
    for (int index = 0; index < arguments.Length - 1; index++)
    {
        if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            return arguments[index + 1];
    }

    return null;
}

static bool TryApplyPurpose(
    AppConfig config,
    string purpose,
    string? storageDirectory,
    out string error)
{
    error = "";
    if (string.Equals(purpose, "viewer", StringComparison.OrdinalIgnoreCase))
    {
        config.DeploymentPreset = DeploymentPresets.ViewerClient;
        AppConfig.NormalizeAfterLoad(config);
    }
    else if (string.Equals(purpose, "host", StringComparison.OrdinalIgnoreCase))
    {
        config.DeploymentPreset = DeploymentPresets.MobileBackupHost;
        AppConfig.NormalizeAfterLoad(config);
        string directory = (storageDirectory ?? "").Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            error = "作为保存主机必须指定存储目录：--storage <目录>";
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            error = $"无法使用该存储目录：{ex.Message}";
            return false;
        }

        config.StorageLocations =
        [
            new StorageLocation
            {
                Path = Path.GetFullPath(directory),
                Priority = 1,
                IsBackupTarget = false
            }
        ];
    }
    else
    {
        error = "用途只能是 host 或 viewer";
        return false;
    }

    AppConfig.MarkDeploymentSetupCompleted(config);
    return WorkstationConfigStore.TrySave(config, out error);
}
