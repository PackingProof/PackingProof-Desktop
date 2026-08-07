using ExpressPackingMonitoring.Logging;
using System;
using System.IO;
using System.Linq;

namespace ExpressPackingMonitoring.Services
{
    /// <summary>
    /// WinRT 依赖文件放置策略：Windows 7 上把 WinRT 相关文件移动到子目录，
    /// 避免未知探测加载触发 WinRT 初始化崩溃；Windows 8+ 保持可用，并把
    /// 历史残留（例如 Win7 原地升级）移回原位置。
    /// </summary>
    internal static class WinRtPlacementService
    {
        private const string DisabledFolderName = "winrt-disabled";
        private static readonly string[] WinRtFiles =
        {
            "ExpressPackingMonitoring.WinTts.dll",
            "Microsoft.Windows.SDK.NET.dll",
            "WinRT.Runtime.dll"
        };

        public static void Apply()
        {
            Apply(AppContext.BaseDirectory, OperatingSystem.IsWindowsVersionAtLeast(6, 2));
        }

        internal static void Apply(string baseDirectory, bool modernWindows)
        {
            try
            {
                string baseFull = Path.GetFullPath(baseDirectory);
                string disabledDir = Path.Combine(baseFull, DisabledFolderName);

                if (modernWindows)
                {
                    RestoreDisabledFiles(baseFull, disabledDir);
                }
                else
                {
                    DisableWinRtFiles(baseFull, disabledDir);
                }

                RuntimeLog.Info(
                    "WinRt",
                    $"Placement applied, modernWindows={modernWindows}, base={baseFull}");
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("WinRt", $"Placement failed: {ex.Message}");
            }
        }

        private static void DisableWinRtFiles(string baseDirectory, string disabledDir)
        {
            Directory.CreateDirectory(disabledDir);
            foreach (string fileName in WinRtFiles)
            {
                string source = Path.Combine(baseDirectory, fileName);
                if (!File.Exists(source))
                    continue;

                string target = Path.Combine(disabledDir, fileName);
                if (File.Exists(target))
                    File.Delete(target);
                File.Move(source, target);
                RuntimeLog.Info("WinRt", $"Moved {fileName} to {DisabledFolderName}");
            }
        }

        private static void RestoreDisabledFiles(string baseDirectory, string disabledDir)
        {
            if (!Directory.Exists(disabledDir))
                return;

            foreach (string fileName in WinRtFiles)
            {
                string source = Path.Combine(disabledDir, fileName);
                if (!File.Exists(source))
                    continue;

                string target = Path.Combine(baseDirectory, fileName);
                if (File.Exists(target))
                    File.Delete(source);
                else
                    File.Move(source, target);
                RuntimeLog.Info("WinRt", $"Restored {fileName} from {DisabledFolderName}");
            }

            if (!Directory.EnumerateFileSystemEntries(disabledDir).Any())
                Directory.Delete(disabledDir);
        }
    }
}
