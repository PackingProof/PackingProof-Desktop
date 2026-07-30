using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ExpressPackingMonitoring.Services;

internal sealed record LauncherPackageInfo(
    int ProtocolVersion,
    string Version,
    string Url,
    long Size,
    string Sha256,
    long ExecutableSize,
    string ExecutableSha256);

internal sealed class LauncherUpdateService
{
    internal const int SupportedProtocolVersion = 1;
    internal const string LauncherFileName = "ExpressPackingMonitoring.exe";
    internal const string UpdateMutexName = @"Local\ExpressPackingMonitoring.Launcher.Update";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task CheckAndApplyAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            RuntimeLog.Info("LauncherUpdate", "Automatic update is disabled");
            return;
        }

        if (!TryResolveInstalledLauncher(AppContext.BaseDirectory, out string launcherPath))
        {
            RuntimeLog.Info("LauncherUpdate", "Skip outside clean package layout");
            return;
        }

        try
        {
            LauncherPackageInfo? package = await FetchPackageInfoAsync(cancellationToken);
            if (package == null)
            {
                RuntimeLog.Info("LauncherUpdate", "Update manifest has no compatible launcher package");
                return;
            }

            if (File.Exists(launcherPath) &&
                string.Equals(
                    ComputeSha256(launcherPath),
                    package.ExecutableSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                RuntimeLog.Info("LauncherUpdate", $"Launcher is current version={package.Version}");
                return;
            }

            string pendingDirectory = Path.Combine(
                AppPaths.CacheDir,
                "updates",
                "launcher-pending",
                NormalizePathSegment(package.Version));
            Directory.CreateDirectory(pendingDirectory);
            string packagePath = Path.Combine(pendingDirectory, "launcher.zip");
            await DownloadAndVerifyAsync(package, packagePath, cancellationToken);

            if (!await WaitForLauncherExitAsync(launcherPath, cancellationToken))
            {
                RuntimeLog.Warn("LauncherUpdate", "Launcher is still running; keep pending package for retry");
                return;
            }

            using var mutex = new Mutex(false, UpdateMutexName);
            bool lockTaken;
            try
            {
                lockTaken = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                lockTaken = true;
            }

            if (!lockTaken)
            {
                RuntimeLog.Warn("LauncherUpdate", "Launcher update mutex is busy; keep pending package for retry");
                return;
            }

            try
            {
                ApplyDownloadedPackage(package, packagePath, launcherPath, AppPaths.BackupsDir);
                TryDeleteDirectory(pendingDirectory);
                RuntimeLog.Info("LauncherUpdate", $"Launcher updated silently version={package.Version}");
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("LauncherUpdate", $"Launcher update deferred: {ex}");
        }
    }

    internal static bool TryResolveInstalledLauncher(string appBaseDirectory, out string launcherPath)
    {
        launcherPath = "";
        try
        {
            var appDirectory = new DirectoryInfo(
                Path.GetFullPath(appBaseDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.Equals(appDirectory.Name, "app", StringComparison.OrdinalIgnoreCase) ||
                appDirectory.Parent == null)
            {
                return false;
            }

            string candidate = Path.Combine(appDirectory.Parent.FullName, LauncherFileName);
            if (!File.Exists(candidate))
                return false;

            launcherPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static LauncherPackageInfo? ParsePackageInfo(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("launcher_package", out JsonElement package) ||
            package.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int protocolVersion = ReadInt32(package, "protocol_version");
        if (protocolVersion != SupportedProtocolVersion)
            return null;

        var result = new LauncherPackageInfo(
            protocolVersion,
            ReadString(package, "version"),
            ReadString(package, "url"),
            ReadInt64(package, "size"),
            ReadString(package, "sha256"),
            ReadInt64(package, "executable_size"),
            ReadString(package, "executable_sha256"));

        return IsValid(result) ? result : null;
    }

    internal static void ApplyDownloadedPackage(
        LauncherPackageInfo package,
        string packagePath,
        string launcherPath,
        string backupDirectory)
    {
        ValidateFile(packagePath, package.Size, package.Sha256, "启动器更新包");

        string launcherDirectory = Path.GetDirectoryName(Path.GetFullPath(launcherPath))
            ?? throw new InvalidOperationException("启动器目录无效");
        string temporaryPath = Path.Combine(
            launcherDirectory,
            $".launcher-update-{Guid.NewGuid():N}.tmp");
        string adjacentBackupPath = Path.Combine(
            launcherDirectory,
            $".launcher-backup-{Guid.NewGuid():N}.bak");

        try
        {
            using (ZipArchive archive = ZipFile.OpenRead(packagePath))
            {
                ZipArchiveEntry[] entries = archive.Entries
                    .Where(entry => !string.IsNullOrEmpty(entry.Name))
                    .ToArray();
                if (entries.Length != 1 ||
                    !string.Equals(entries[0].FullName, LauncherFileName, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("启动器更新包只能包含根目录启动器");
                }

                entries[0].ExtractToFile(temporaryPath, overwrite: false);
            }

            ValidateFile(
                temporaryPath,
                package.ExecutableSize,
                package.ExecutableSha256,
                "启动器程序");

            File.Replace(temporaryPath, launcherPath, adjacentBackupPath, ignoreMetadataErrors: true);
            try
            {
                ValidateFile(
                    launcherPath,
                    package.ExecutableSize,
                    package.ExecutableSha256,
                    "已安装启动器");
            }
            catch
            {
                File.Replace(adjacentBackupPath, launcherPath, null, ignoreMetadataErrors: true);
                throw;
            }

            Directory.CreateDirectory(backupDirectory);
            string retainedBackupPath = Path.Combine(
                backupDirectory,
                $"launcher-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.exe");
            File.Copy(adjacentBackupPath, retainedBackupPath, overwrite: false);
            File.Delete(adjacentBackupPath);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async Task<LauncherPackageInfo?> FetchPackageInfoAsync(
        CancellationToken cancellationToken)
    {
        string releaseUrl = UpdateCheckOptions.GetUpdateCheckUrl();
        using JsonDocument release = await GetJsonAsync(releaseUrl, cancellationToken);
        string latestVersion = ReadString(release.RootElement, "tag_name")
            .TrimStart('v', 'V');
        string expectedName = $"update_v{latestVersion}.json";
        string manifestUrl = "";

        if (release.RootElement.TryGetProperty("assets", out JsonElement assets) &&
            assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name = ReadString(asset, "name");
                if (!string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, "update.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                manifestUrl = ReadString(asset, "browser_download_url");
                if (string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(manifestUrl))
            return null;

        using JsonDocument manifest = await GetJsonAsync(manifestUrl, cancellationToken);
        return ParsePackageInfo(manifest.RootElement);
    }

    private static async Task<JsonDocument> GetJsonAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ExpressPackingMonitoring");
        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task DownloadAndVerifyAsync(
        LauncherPackageInfo package,
        string packagePath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(packagePath))
        {
            try
            {
                ValidateFile(packagePath, package.Size, package.Sha256, "已缓存启动器更新包");
                return;
            }
            catch
            {
                TryDeleteFile(packagePath);
            }
        }

        string temporaryPath = packagePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, package.Url);
            request.Headers.UserAgent.ParseAdd("ExpressPackingMonitoring");
            using HttpResponseMessage response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var destination = File.Create(temporaryPath))
                await source.CopyToAsync(destination, cancellationToken);

            ValidateFile(temporaryPath, package.Size, package.Sha256, "启动器更新包");
            File.Move(temporaryPath, packagePath);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async Task<bool> WaitForLauncherExitAsync(
        string launcherPath,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsProcessRunning(launcherPath))
                return true;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return false;
    }

    private static bool IsProcessRunning(string executablePath)
    {
        foreach (Process process in Process.GetProcessesByName("ExpressPackingMonitoring"))
        {
            try
            {
                if (process.Id != Environment.ProcessId &&
                    string.Equals(
                        process.MainModule?.FileName,
                        executablePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static void ValidateFile(string path, long expectedSize, string expectedSha256, string label)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != expectedSize)
            throw new InvalidDataException($"{label}大小校验失败");
        if (!string.Equals(ComputeSha256(path), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label}完整性校验失败");
    }

    internal static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsValid(LauncherPackageInfo package)
        => package.ProtocolVersion == SupportedProtocolVersion
            && !string.IsNullOrWhiteSpace(package.Version)
            && Uri.TryCreate(package.Url, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback)
            && package.Size > 0
            && package.ExecutableSize > 0
            && IsSha256(package.Sha256)
            && IsSha256(package.ExecutableSha256);

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static string NormalizePathSegment(string value)
    {
        string normalized = string.Concat(value.Where(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_'));
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int ReadInt32(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) &&
           value.TryGetInt32(out int result)
            ? result
            : 0;

    private static long ReadInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) &&
           value.TryGetInt64(out long result)
            ? result
            : 0;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
