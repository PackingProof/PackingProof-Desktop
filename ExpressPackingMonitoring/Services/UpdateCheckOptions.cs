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
