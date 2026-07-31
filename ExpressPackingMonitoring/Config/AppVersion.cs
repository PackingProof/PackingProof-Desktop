using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ExpressPackingMonitoring.Config
{
    public static class AppVersion
    {
        private const string FallbackCurrent = "v0.0.0";

        public static string Current => GetCurrentVersion();
        public static string BuildDateText => GetBuildDateText();
        public static string CommitId => GetCommitId(Assembly.GetExecutingAssembly());
        public static string CommitShortId => ShortenCommitId(CommitId);

        internal static string GetCommitId(Assembly assembly)
        {
            string? metadataCommit = assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => string.Equals(
                    attribute.Key,
                    "GitCommitId",
                    StringComparison.OrdinalIgnoreCase))
                ?.Value;
            string? informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            return ResolveCommitId(metadataCommit, informationalVersion);
        }

        internal static string ResolveCommitId(string? metadataCommit, string? informationalVersion)
        {
            string normalizedMetadata = NormalizeCommitId(metadataCommit);
            if (normalizedMetadata.Length > 0)
                return normalizedMetadata;

            if (string.IsNullOrWhiteSpace(informationalVersion))
                return string.Empty;

            int metadataIndex = informationalVersion.LastIndexOf('+');
            return metadataIndex >= 0 && metadataIndex < informationalVersion.Length - 1
                ? NormalizeCommitId(informationalVersion[(metadataIndex + 1)..])
                : string.Empty;
        }

        internal static string ShortenCommitId(string? commitId)
        {
            string normalized = NormalizeCommitId(commitId);
            return normalized.Length > 8 ? normalized[..8] : normalized;
        }

        private static string NormalizeCommitId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string commitId = value.Trim();
            if (commitId.Length is < 7 or > 64 || commitId.Any(character => !Uri.IsHexDigit(character)))
                return string.Empty;

            return commitId.ToLowerInvariant();
        }

        private static string GetCurrentVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            string? informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (IsUsefulVersion(informationalVersion))
                return NormalizeDisplayVersion(informationalVersion!);

            return FallbackCurrent;
        }

        private static bool IsUsefulVersion(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            string version = value.Trim();
            if (version.StartsWith("1.0.0+", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private static string NormalizeDisplayVersion(string value)
        {
            string version = value.Trim();
            int metadataIndex = version.IndexOf('+');
            return metadataIndex > 0 ? version[..metadataIndex] : version;
        }

        private static string GetBuildDateText()
        {
            try
            {
                string processPath = Environment.ProcessPath ?? AppContext.BaseDirectory;
                DateTime buildTime = File.GetLastWriteTime(processPath);
                return $"编译日期 {buildTime:yyyy-MM-dd HH:mm}";
            }
            catch
            {
                return "编译日期未知";
            }
        }
    }
}
