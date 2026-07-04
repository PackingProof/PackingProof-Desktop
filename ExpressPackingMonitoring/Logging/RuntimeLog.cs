using ExpressPackingMonitoring.Config;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ExpressPackingMonitoring.Logging
{
    internal static class RuntimeLog
    {
        private const long MaxLogBytes = 5 * 1024 * 1024;
        private static readonly object LockObj = new();

        public static void Info(string category, string message) => Write("INFO", category, message, null);

        public static void Warn(string category, string message) => Write("WARN", category, message, null);

        public static void Error(string category, string message, Exception? exception = null) => Write("ERROR", category, message, exception);

        public static void LogBuildInfo()
        {
            var assembly = Assembly.GetExecutingAssembly();
            string version = assembly.GetName().Version?.ToString() ?? "unknown";
            string informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version;
            string fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? version;
            string gitCommitId = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(x => string.Equals(x.Key, "GitCommitId", StringComparison.OrdinalIgnoreCase))
                ?.Value ?? GetCommitIdFromInformationalVersion(informationalVersion);

            Info("App", $"Build info version={version}, fileVersion={fileVersion}, informationalVersion={informationalVersion}, gitCommitId={(string.IsNullOrWhiteSpace(gitCommitId) ? "unknown" : gitCommitId)}");
        }

        private static string GetCommitIdFromInformationalVersion(string informationalVersion)
        {
            int suffixIndex = informationalVersion.LastIndexOf('+');
            if (suffixIndex < 0 || suffixIndex >= informationalVersion.Length - 1)
                return "";

            return informationalVersion[(suffixIndex + 1)..];
        }

        private static void Write(string level, string category, string message, Exception? exception)
        {
            try
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{category}] {message}";
                if (exception != null)
                    line += $"{Environment.NewLine}{exception}";

                Debug.WriteLine(line);

                lock (LockObj)
                {
                    Directory.CreateDirectory(AppPaths.LogDir);
                    RotateIfNeeded();
                    File.AppendAllText(AppPaths.RuntimeLogPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never affect capture or recording.
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                var path = AppPaths.RuntimeLogPath;
                if (!File.Exists(path)) return;
                var info = new FileInfo(path);
                if (info.Length <= MaxLogBytes) return;

                string oldPath = Path.Combine(AppPaths.LogDir, "runtime.old.log");
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(path, oldPath);
            }
            catch
            {
            }
        }
    }
}
