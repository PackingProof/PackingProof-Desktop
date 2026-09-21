using ExpressPackingMonitoring.Config;

namespace ExpressPackingMonitoring.Host;

/// <summary>
/// Mac 端的用途设置：复用桌面端"选择电脑用途"的文案，只保留 Mac 支持的两种结果——
/// 负责长期保存录像的保存主机，或只连接主机查看的查看端。
/// </summary>
internal static class MacDeploymentSetup
{
    /// <summary>是否需要先问用途（首次打开或刚执行过切换用途）。</summary>
    internal static bool NeedsSetup(AppConfig config) =>
        !DeploymentPresets.IsKnown(config.DeploymentPreset)
        || AppConfig.ShouldRunDeploymentSetup(config);

    /// <summary>问用途并落盘；返回 false 时 error 说明原因（用户取消也算失败）。</summary>
    internal static bool TryConfigure(AppConfig config, out string error)
    {
        error = "";
        if (!MacDialog.IsSupported)
        {
            error = "当前平台不支持用途选择，请用 --purpose host|viewer 指定";
            return false;
        }

        string? purpose = MacDialog.ChoosePurpose();
        if (purpose == null)
        {
            error = "已取消用途选择，程序退出";
            return false;
        }

        if (purpose == "viewer")
        {
            config.DeploymentPreset = DeploymentPresets.ViewerClient;
            AppConfig.NormalizeAfterLoad(config);
        }
        else
        {
            config.DeploymentPreset = DeploymentPresets.MobileBackupHost;
            AppConfig.NormalizeAfterLoad(config);
            if (!TryEnsureStorageLocation(config, out error)) return false;
        }

        AppConfig.MarkDeploymentSetupCompleted(config);
        if (!WorkstationConfigStore.TrySave(config, out error)) return false;

        Console.WriteLine($"用途已保存：{DeploymentPresets.GetDisplayName(config.DeploymentPreset)}");
        return true;
    }

    /// <summary>
    /// 保存主机必须有一个本机可用的录像目录。已有配置就沿用（外接盘暂时不在也不强制重选），
    /// 只有默认的 Windows 路径或空配置才让用户重新选一次。
    /// </summary>
    private static bool TryEnsureStorageLocation(AppConfig config, out string error)
    {
        error = "";
        string configured = config.StorageLocations?.FirstOrDefault()?.Path ?? "";
        if (IsUsableConfiguredLocation(configured)) return true;

        string suggested = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Movies",
            "快递打包视频");
        string? folder = MacDialog.ChooseFolder("选择录像保存位置（可以选外接硬盘）", suggested);
        if (string.IsNullOrWhiteSpace(folder))
        {
            error = "已取消存储位置选择，程序退出";
            return false;
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            error = $"无法使用该目录：{ex.Message}";
            return false;
        }

        config.StorageLocations =
        [
            new StorageLocation
            {
                Path = folder,
                Priority = 1,
                IsBackupTarget = false
            }
        ];
        return true;
    }

    private static bool IsUsableConfiguredLocation(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        // 只认"根目录下面确实存在"的位置：桌面端默认值（D:\快递打包视频）在 Mac 上
        // 会被归一化成 /快递打包视频 这种不存在的路径，靠字符串判断拦不住，必须看实际目录
        try
        {
            return Path.IsPathRooted(path) && Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
