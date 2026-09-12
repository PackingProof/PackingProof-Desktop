using System;
using System.IO;
using System.Text.Json;

namespace ExpressPackingMonitoring.Config
{
    /// <summary>
    /// 在 WPF 静态初始化阶段读取极少量启动开关。
    /// 此时不能使用 <see cref="AppPaths"/>：它的静态构造会建目录并迁移历史数据，
    /// 放在渲染模式确定之前执行既慢又可能把启动拖垮；日志系统此刻也尚未就绪。
    /// </summary>
    internal static class EarlyStartupConfig
    {
        internal const string UserDataDirectoryName = "ExpressPackingMonitoring";
        internal const string ForceSoftwareRenderingEnvironmentVariable = "EPM_FORCE_SOFTWARE_RENDERING";

        /// <summary>与 <see cref="AppPaths"/> 的用户数据目录共用同一份根目录计算，避免两处漂移。</summary>
        internal static string GetLocalAppDataRoot()
        {
            string? overridePath = Environment.GetEnvironmentVariable("EPM_USER_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overridePath))
                return Path.GetFullPath(overridePath);

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localAppData)
                ? AppDomain.CurrentDomain.BaseDirectory
                : localAppData;
        }

        /// <summary>
        /// 是否强制软件渲染。环境变量优先于配置，便于现场在界面打不开时临时改回来；
        /// 读不到或配置损坏时一律回退到硬件渲染。
        /// </summary>
        public static bool IsSoftwareRenderingForced()
        {
            if (TryParseBool(
                    Environment.GetEnvironmentVariable(ForceSoftwareRenderingEnvironmentVariable),
                    out bool overrideValue))
                return overrideValue;

            return ReadForceSoftwareRenderingFromConfig(
                Path.Combine(GetLocalAppDataRoot(), UserDataDirectoryName, "config.json"));
        }

        internal static bool ReadForceSoftwareRenderingFromConfig(string configPath)
        {
            try
            {
                if (!File.Exists(configPath))
                    return false;

                using FileStream stream = File.Open(
                    configPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using JsonDocument document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return false;

                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (!string.Equals(
                            property.Name,
                            nameof(AppConfig.ForceSoftwareRendering),
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    return property.Value.ValueKind == JsonValueKind.True
                        || (property.Value.ValueKind == JsonValueKind.String
                            && TryParseBool(property.Value.GetString(), out bool parsed)
                            && parsed);
                }

                return false;
            }
            catch (Exception)
            {
                // 配置缺失、损坏或无权访问时保持硬件渲染；此处不能记日志，日志尚未初始化。
                return false;
            }
        }

        private static bool TryParseBool(string? value, out bool result)
        {
            result = false;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string normalized = value.Trim();
            if (string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase))
            {
                result = true;
                return true;
            }

            if (string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase))
            {
                result = false;
                return true;
            }

            return false;
        }
    }
}
