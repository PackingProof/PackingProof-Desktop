using System;
using System.Collections.Generic;
using System.IO;
using ExpressPackingMonitoring.UpdateCore;

namespace ExpressPackingMonitoring.Services
{
    public static class UpdateCheckOptions
    {
        public const string UrlKey = "UPDATE_CHECK_URL";
        public const string FallbackUrlKey = "UPDATE_CHECK_FALLBACK_URL";
        internal const string DefaultGiteeCheckUrl = UpdateEndpointPolicy.DefaultGiteeCheckUrl;
        internal const string DefaultGithubCheckUrl = UpdateEndpointPolicy.DefaultGithubCheckUrl;

        public static string GetUpdateCheckUrl()
        {
            return GetUpdateCheckUrls()[0];
        }

        /// <summary>
        /// 可选的接口令牌：只在检查地址属于对应平台时返回，没配置就返回空串，
        /// 请求行为与以前完全一致。未认证的 GitHub API 每 IP 每小时只有 60 次，
        /// 同一出口 IP 下的多台机器会互相挤掉配额；配上令牌可以稳定检查更新。
        /// </summary>
        public static string GetApiToken(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";

            if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                return FirstConfigured("GITHUB_TOKEN", "GH_TOKEN");
            if (url.Contains("gitee.com", StringComparison.OrdinalIgnoreCase))
                return FirstConfigured("GITEE_TOKEN");
            return "";
        }

        private static string FirstConfigured(params string[] names)
        {
            foreach (string name in names)
            {
                string? value = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrWhiteSpace(value))
                    value = ReadEnvFileValue(name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return "";
        }

        /// <summary>
        /// 把"最新版本"检查地址换成"release 列表"地址：按平台挑版本时要看整份列表，
        /// 而不是只看最新那一个（有的版本只修了另一个平台）。实现在 UpdateCore 里，
        /// 启动器（AOT）也走同一条逻辑。
        /// </summary>
        public static IReadOnlyList<string> ToReleaseListUrls(IReadOnlyList<string> checkUrls) =>
            UpdateEndpointPolicy.ToReleaseListUrls(checkUrls);

        public static IReadOnlyList<string> GetUpdateCheckUrls()
        {
            string? primary = Environment.GetEnvironmentVariable(UrlKey);
            if (string.IsNullOrWhiteSpace(primary))
                primary = ReadEnvFileValue(UrlKey);

            string? fallback = Environment.GetEnvironmentVariable(FallbackUrlKey);
            if (string.IsNullOrWhiteSpace(fallback))
                fallback = ReadEnvFileValue(FallbackUrlKey);

            return ResolveUpdateCheckUrls(primary, fallback);
        }

        internal static IReadOnlyList<string> ResolveUpdateCheckUrls(
            string? configuredPrimary,
            string? configuredFallback)
        {
            return UpdateEndpointPolicy.ResolveCheckUrls(configuredPrimary, configuredFallback);
        }

        private static string? ReadEnvFileValue(string expectedKey)
        {
            foreach (string path in GetEnvFileCandidates())
            {
                if (!File.Exists(path)) continue;

                foreach (string rawLine in File.ReadLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                    int separator = line.IndexOf('=');
                    if (separator <= 0) continue;

                    string key = line[..separator].Trim();
                    if (!string.Equals(key, expectedKey, StringComparison.OrdinalIgnoreCase)) continue;

                    return line[(separator + 1)..].Trim().Trim('"', '\'');
                }
            }

            return null;
        }

        private static string[] GetEnvFileCandidates()
        {
            string baseDir = AppContext.BaseDirectory;
            string currentDir = Environment.CurrentDirectory;
            return new[]
            {
                Path.Combine(baseDir, ".env"),
                Path.Combine(currentDir, ".env")
            };
        }
    }
}
