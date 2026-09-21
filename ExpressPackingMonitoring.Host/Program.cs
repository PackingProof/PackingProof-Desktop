using ExpressPackingMonitoring;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Host;

// Mac 端入口：先问清这台电脑的用途，再按用途运行保存主机或查看端。
//   （无参数）             首次打开会弹原生对话框问用途与存储位置
//   --purpose host|viewer  跳过询问，直接指定用途（自动化与排查用）
//   --storage <目录>       保存主机的录像保存位置
//   --switch-purpose       重新选择用途
if (args.Any(argument => argument is "-h" or "--help"))
{
    Console.WriteLine("用法: ExpressPackingMonitoring.Host [--purpose host|viewer] [--storage <目录>] [--switch-purpose] [--no-browser] [--no-dialog]");
    return 0;
}

HostOptions.Apply(args);
AppConfig config = WorkstationConfigStore.Load();
if (args.Any(argument => string.Equals(argument, "--switch-purpose", StringComparison.OrdinalIgnoreCase)))
{
    AppConfig.ResetDeploymentSetupForRetry(config);
}

string? purpose = ReadOption(args, "--purpose");
string? storageDirectory = ReadOption(args, "--storage");
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
    if (!MacDeploymentSetup.TryConfigure(config, out string setupError))
    {
        Console.Error.WriteLine(setupError);
        return 2;
    }

    config = WorkstationConfigStore.Load();
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
