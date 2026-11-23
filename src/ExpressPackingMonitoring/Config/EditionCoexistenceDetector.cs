using System;
using System.IO;
using ExpressPackingMonitoring.UpdateCore;

namespace ExpressPackingMonitoring.Config;

/// <summary>
/// 只检测 Setup 版与 Microsoft Store 版各自的数据目录是否已经存在。
/// 两版的程序目录本来就不会互相覆盖；这里用于在设置页明确告诉用户
/// “另一版的数据不会自动迁移/同步”，避免把空数据误认为丢失。
/// </summary>
internal static class EditionCoexistenceDetector
{
    internal static bool HasOtherEditionData(bool packaged, string? localAppDataRoot = null)
    {
        string root = ResolveLocalAppDataRoot(localAppDataRoot);
        if (string.IsNullOrWhiteSpace(root)) return false;
        return packaged ? HasSetupEditionData(root) : HasStoreEditionData(root);
    }

    internal static bool HasSetupEditionData(string localAppDataRoot)
    {
        string dataRoot = Path.Combine(localAppDataRoot, BrandMetadata.DataDirectoryName);
        return File.Exists(Path.Combine(dataRoot, "config.json"))
            || File.Exists(Path.Combine(dataRoot, "videos.db"));
    }

    internal static bool HasStoreEditionData(string localAppDataRoot)
    {
        string packagesRoot = Path.Combine(localAppDataRoot, "Packages");
        if (!Directory.Exists(packagesRoot)) return false;

        try
        {
            foreach (string packageDirectory in Directory.EnumerateDirectories(packagesRoot))
            {
                string dataRoot = Path.Combine(
                    packageDirectory,
                    "LocalState",
                    BrandMetadata.DataDirectoryName);
                if (File.Exists(Path.Combine(dataRoot, "config.json"))
                    || File.Exists(Path.Combine(dataRoot, "videos.db")))
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            // 目录正在被系统回收/更新时忽略本次检测，不阻断设置页。
        }
        catch (UnauthorizedAccessException)
        {
            // 其他用户的包目录不可读时忽略本次检测。
        }

        return false;
    }

    private static string ResolveLocalAppDataRoot(string? overrideRoot)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return Path.GetFullPath(overrideRoot);
        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }
}
