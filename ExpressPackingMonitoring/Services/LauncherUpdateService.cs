using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ExpressPackingMonitoring.UpdateCore;

namespace ExpressPackingMonitoring.Services;

internal sealed record LauncherPackageInfo(
    int ProtocolVersion,
    string Version,
    string Url,
    string GithubUrl,
    long Size,
    string Sha256,
    long ExecutableSize,
    string ExecutableSha256,
    string GiteeUrl = "");

internal sealed record LauncherDownloadFailureState(
    string PackageVersion,
    string PackageSha256,
    int ConsecutiveGithubDownloadFailures);

internal sealed record LauncherDownloadRoute(
    string GithubUrl,
    string FallbackUrl,
    string SelectedUrl,
    bool PreferFallback);

internal sealed record LauncherUpdateCheckState(
    string AppVersion,
    long LauncherLength,
    long LauncherLastWriteUtcTicks,
    string LauncherSha256,
    string PackageVersion);

internal enum LauncherPackageApplyResult
{
    Applied,
    Deferred
}

internal sealed class LauncherUpdateService
{
    internal const int SupportedProtocolVersion = 1;
    internal const string LauncherFileName = "ExpressPackingMonitoring.exe";
    internal const string ManualInstallerCommandName = "双击更新启动器.cmd";
    internal const string ManualInstallerScriptName = "apply_launcher_patch.ps1";
    internal const string ManualInstallerManifestName = "launcher_patch_manifest.json";
    internal const string ManualInstallerNoticeName = "启动器更新说明.txt";
    internal const string UpdateMutexName = @"Local\ExpressPackingMonitoring.Launcher.Update";
    internal const string PendingDescriptorFileName = "launcher-package.json";
    internal const string CheckStateFileName = "launcher-check-state.json";
    internal const string DownloadFailureStateFileName = "launcher-download-failures.json";
    internal const int GithubDownloadFailureFallbackThreshold = 3;
    internal const string DefaultGithubDownloadBaseUrl =
        "https://github.com/PackingProof/PackingProof-Desktop/releases/download";
    internal const int MaxRetainedBackups = 3;
    internal static readonly TimeSpan LauncherExitWaitTimeout = TimeSpan.FromSeconds(5);

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
            string updatesDirectory = Path.Combine(AppPaths.CacheDir, "updates");
            string pendingRoot = Path.Combine(updatesDirectory, "launcher-pending");
            string checkStatePath = Path.Combine(updatesDirectory, CheckStateFileName);
            string downloadFailureStatePath = Path.Combine(
                updatesDirectory,
                DownloadFailureStateFileName);
            string appVersion = GetCurrentAppVersion();
            if (await TryApplyPendingPackageAsync(
                    pendingRoot,
                    checkStatePath,
                    appVersion,
                    launcherPath,
                    cancellationToken))
            {
                return;
            }

            if (ShouldSkipSuccessfulCheck(
                    checkStatePath,
                    appVersion,
                    launcherPath))
            {
                RuntimeLog.Info("LauncherUpdate", "Skip repeated successful launcher check");
                return;
            }

            LauncherPackageInfo? package = await FetchPackageInfoAsync(cancellationToken);
            if (package == null)
            {
                RuntimeLog.Info("LauncherUpdate", "Update manifest has no compatible launcher package");
                SaveSuccessfulCheck(
                    checkStatePath,
                    appVersion,
                    launcherPath,
                    "",
                    "");
                return;
            }

            if (File.Exists(launcherPath) &&
                string.Equals(ComputeSha256(launcherPath), package.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            {
                RuntimeLog.Info("LauncherUpdate", $"Launcher is current version={package.Version}");
                SaveSuccessfulCheck(
                    checkStatePath,
                    appVersion,
                    launcherPath,
                    package.ExecutableSha256,
                    package.Version);
                return;
            }

            string pendingDirectory = Path.Combine(
                pendingRoot,
                NormalizePathSegment(package.Version));
            Directory.CreateDirectory(pendingDirectory);
            string packagePath = Path.Combine(pendingDirectory, "launcher.zip");
            await DownloadAndVerifyAsync(
                package,
                packagePath,
                downloadFailureStatePath,
                cancellationToken);
            SavePendingDescriptor(
                Path.Combine(pendingDirectory, PendingDescriptorFileName),
                package);
            LauncherPackageApplyResult result = await TryApplyPackageAsync(
                package,
                packagePath,
                launcherPath,
                AppPaths.BackupsDir,
                cancellationToken);
            if (result == LauncherPackageApplyResult.Deferred)
                return;

            TryDeleteDirectoryWithinRoot(pendingDirectory, pendingRoot);
            SaveSuccessfulCheck(
                checkStatePath,
                appVersion,
                launcherPath,
                package.ExecutableSha256,
                package.Version);
            RuntimeLog.Info("LauncherUpdate", $"Launcher updated silently version={package.Version}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("LauncherUpdate", $"Launcher update deferred: {ex}");
        }
    }

    private static async Task<bool> TryApplyPendingPackageAsync(
        string pendingRoot,
        string checkStatePath,
        string appVersion,
        string launcherPath,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(pendingRoot))
            return false;

        foreach (string descriptorPath in Directory
                     .EnumerateDirectories(
                         pendingRoot,
                         "*",
                         SearchOption.TopDirectoryOnly)
                     .Select(directory => Path.Combine(
                         directory,
                         PendingDescriptorFileName))
                     .Where(File.Exists)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            string pendingDirectory = Path.GetDirectoryName(descriptorPath) ?? "";
            string packagePath = Path.Combine(pendingDirectory, "launcher.zip");
            LauncherPackageInfo package;
            try
            {
                package = LoadPendingDescriptor(descriptorPath);
                ValidateFile(packagePath, package.Size, package.Sha256, "已缓存启动器更新包");
            }
            catch (Exception ex) when (ex is InvalidDataException
                                       or JsonException
                                       or FileNotFoundException)
            {
                RuntimeLog.Warn(
                    "LauncherUpdate",
                    $"Discard invalid pending launcher package: {ex.Message}");
                TryDeleteDirectoryWithinRoot(pendingDirectory, pendingRoot);
                continue;
            }

            try
            {
                if (File.Exists(launcherPath)
                    && string.Equals(
                        ComputeSha256(launcherPath),
                        package.ExecutableSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteDirectoryWithinRoot(pendingDirectory, pendingRoot);
                    SaveSuccessfulCheck(
                        checkStatePath,
                        appVersion,
                        launcherPath,
                        package.ExecutableSha256,
                        package.Version);
                    return true;
                }

                LauncherPackageApplyResult result = await TryApplyPackageAsync(
                    package,
                    packagePath,
                    launcherPath,
                    AppPaths.BackupsDir,
                    cancellationToken);
                if (result == LauncherPackageApplyResult.Applied)
                {
                    TryDeleteDirectoryWithinRoot(pendingDirectory, pendingRoot);
                    SaveSuccessfulCheck(
                        checkStatePath,
                        appVersion,
                        launcherPath,
                        package.ExecutableSha256,
                        package.Version);
                    RuntimeLog.Info(
                        "LauncherUpdate",
                        $"Applied verified pending launcher package version={package.Version}");
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn(
                    "LauncherUpdate",
                    $"Pending launcher update deferred: {ex}");
                return true;
            }
        }

        return false;
    }

    private static async Task<LauncherPackageApplyResult> TryApplyPackageAsync(
        LauncherPackageInfo package,
        string packagePath,
        string launcherPath,
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        if (!await WaitForLauncherExitAsync(launcherPath, cancellationToken))
        {
            RuntimeLog.Warn(
                "LauncherUpdate",
                "Launcher is still running; keep pending package for retry");
            return LauncherPackageApplyResult.Deferred;
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
            RuntimeLog.Warn(
                "LauncherUpdate",
                "Launcher update mutex is busy; keep pending package for retry");
            return LauncherPackageApplyResult.Deferred;
        }

        try
        {
            ApplyDownloadedPackage(
                package,
                packagePath,
                launcherPath,
                backupDirectory);
            return LauncherPackageApplyResult.Applied;
        }
        finally
        {
            mutex.ReleaseMutex();
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
            ReadString(package, "github_url"),
            ReadInt64(package, "size"),
            ReadString(package, "sha256"),
            ReadInt64(package, "executable_size"),
            ReadString(package, "executable_sha256"),
            ReadString(package, "gitee_url"));

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
                string[] expectedEntries =
                [
                    LauncherFileName,
                    ManualInstallerCommandName,
                    ManualInstallerScriptName,
                    ManualInstallerManifestName,
                    ManualInstallerNoticeName
                ];
                if (entries.Length != expectedEntries.Length ||
                    expectedEntries.Any(expected => !entries.Any(entry =>
                        string.Equals(entry.FullName, expected, StringComparison.Ordinal))))
                {
                    throw new InvalidDataException("启动器更新包包含缺失、额外或越界文件");
                }

                entries.Single(entry => string.Equals(
                    entry.FullName,
                    LauncherFileName,
                    StringComparison.Ordinal)).ExtractToFile(temporaryPath, overwrite: false);
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

            string launcherBackupDirectory = Path.Combine(backupDirectory, "launcher");
            Directory.CreateDirectory(launcherBackupDirectory);
            string retainedBackupPath = Path.Combine(
                launcherBackupDirectory,
                $"launcher-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.exe");
            File.Copy(adjacentBackupPath, retainedBackupPath, overwrite: false);
            File.Delete(adjacentBackupPath);
            PruneRetainedBackups(launcherBackupDirectory);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async Task<LauncherPackageInfo?> FetchPackageInfoAsync(
        CancellationToken cancellationToken)
    {
        var metadataClient = new UpdateMetadataClient(
            HttpClient,
            log: message => RuntimeLog.Info("LauncherUpdate", message));
        using ResolvedUpdateManifest resolved = await metadataClient.FetchLatestManifestAsync(
            UpdateCheckOptions.GetUpdateCheckUrls(),
            cancellationToken);
        return ParsePackageInfo(resolved.Manifest.RootElement);
    }

    private static async Task DownloadAndVerifyAsync(
        LauncherPackageInfo package,
        string packagePath,
        string failureStatePath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(packagePath))
        {
            try
            {
                ValidateFile(packagePath, package.Size, package.Sha256, "已缓存启动器更新包");
                ResetDownloadFailureState(failureStatePath);
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
            LauncherDownloadFailureState failureState = LoadDownloadFailureState(
                failureStatePath,
                package);
            LauncherDownloadRoute route = GetDownloadRoute(
                package,
                failureState.ConsecutiveGithubDownloadFailures);
            try
            {
                RuntimeLog.Info(
                    "LauncherUpdate",
                    $"Download launcher package source={(route.PreferFallback ? "gitee" : "github")}, failures={failureState.ConsecutiveGithubDownloadFailures}, version={package.Version}");
                await DownloadFileAsync(route.SelectedUrl, temporaryPath, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                       !route.PreferFallback &&
                                       !AreSameUrl(route.GithubUrl, route.FallbackUrl))
            {
                failureState = failureState with
                {
                    ConsecutiveGithubDownloadFailures =
                        failureState.ConsecutiveGithubDownloadFailures + 1
                };
                SaveDownloadFailureState(failureStatePath, failureState);
                RuntimeLog.Warn(
                    "LauncherUpdate",
                    $"GitHub launcher package download failed {failureState.ConsecutiveGithubDownloadFailures}/{GithubDownloadFailureFallbackThreshold}: {ex.Message}");
                TryDeleteFile(temporaryPath);
                RuntimeLog.Info(
                    "LauncherUpdate",
                    "Retry launcher package from Gitee fallback source");
                await DownloadFileAsync(route.FallbackUrl, temporaryPath, cancellationToken);
            }

            ValidateFile(temporaryPath, package.Size, package.Sha256, "启动器更新包");
            File.Move(temporaryPath, packagePath);
            ResetDownloadFailureState(failureStatePath);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async Task DownloadFileAsync(
        string url,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("ExpressPackingMonitoring");
        using HttpResponseMessage response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task<bool> WaitForLauncherExitAsync(
        string launcherPath,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.Add(LauncherExitWaitTimeout);
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

    internal static bool ShouldSkipSuccessfulCheck(
        string statePath,
        string appVersion,
        string launcherPath)
    {
        try
        {
            if (!File.Exists(statePath) || !File.Exists(launcherPath))
                return false;
            LauncherUpdateCheckState? state = JsonSerializer.Deserialize<LauncherUpdateCheckState>(
                File.ReadAllText(statePath));
            var launcher = new FileInfo(launcherPath);
            return state != null
                && string.Equals(state.AppVersion, appVersion, StringComparison.Ordinal)
                && state.LauncherLength == launcher.Length
                && state.LauncherLastWriteUtcTicks == launcher.LastWriteTimeUtc.Ticks;
        }
        catch
        {
            return false;
        }
    }

    internal static void SaveSuccessfulCheck(
        string statePath,
        string appVersion,
        string launcherPath,
        string launcherSha256,
        string packageVersion)
    {
        var launcher = new FileInfo(launcherPath);
        if (!launcher.Exists)
            return;
        string directory = Path.GetDirectoryName(Path.GetFullPath(statePath))
            ?? throw new InvalidOperationException("启动器检查缓存目录无效");
        Directory.CreateDirectory(directory);
        string temporaryPath = statePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var state = new LauncherUpdateCheckState(
                appVersion,
                launcher.Length,
                launcher.LastWriteTimeUtc.Ticks,
                launcherSha256 ?? "",
                packageVersion ?? "");
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state));
            File.Move(temporaryPath, statePath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    internal static void SavePendingDescriptor(
        string descriptorPath,
        LauncherPackageInfo package)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(descriptorPath))
            ?? throw new InvalidOperationException("启动器待处理目录无效");
        Directory.CreateDirectory(directory);
        string temporaryPath = descriptorPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(package));
            File.Move(temporaryPath, descriptorPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    internal static LauncherPackageInfo LoadPendingDescriptor(string descriptorPath)
    {
        LauncherPackageInfo? package = JsonSerializer.Deserialize<LauncherPackageInfo>(
            File.ReadAllText(descriptorPath));
        if (package != null)
        {
            package = package with
            {
                GithubUrl = package.GithubUrl ?? "",
                GiteeUrl = package.GiteeUrl ?? ""
            };
        }
        return package != null && IsValid(package)
            ? package
            : throw new InvalidDataException("启动器待处理描述无效");
    }

    internal static void PruneRetainedBackups(
        string launcherBackupDirectory,
        int keepCount = MaxRetainedBackups)
    {
        string normalizedDirectory = Path.GetFullPath(launcherBackupDirectory);
        if (!Directory.Exists(normalizedDirectory))
            return;
        keepCount = Math.Max(1, keepCount);
        FileInfo[] backups = new DirectoryInfo(normalizedDirectory)
            .EnumerateFiles("launcher-*.exe", SearchOption.TopDirectoryOnly)
            .Where(file => file.Name.StartsWith("launcher-", StringComparison.Ordinal)
                && file.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        foreach (FileInfo backup in backups.Skip(keepCount))
        {
            string fullPath = Path.GetFullPath(backup.FullName);
            if (!IsPathInside(fullPath, normalizedDirectory))
                continue;
            TryDeleteFile(fullPath);
        }
    }

    private static string GetCurrentAppVersion()
        => Assembly.GetExecutingAssembly()
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
               ?.InformationalVersion
           ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
           ?? "unknown";

    private static bool IsValid(LauncherPackageInfo package)
        => package.ProtocolVersion == SupportedProtocolVersion
            && !string.IsNullOrWhiteSpace(package.Version)
            && (UpdateEndpointPolicy.IsSecureAbsoluteUrl(package.Url)
                || UpdateEndpointPolicy.IsSecureAbsoluteUrl(package.GithubUrl)
                || UpdateEndpointPolicy.IsSecureAbsoluteUrl(package.GiteeUrl))
            && package.Size > 0
            && package.ExecutableSize > 0
            && IsSha256(package.Sha256)
            && IsSha256(package.ExecutableSha256);

    internal static LauncherDownloadRoute GetDownloadRoute(
        LauncherPackageInfo package,
        int consecutiveGithubFailures)
    {
        PackageDownloadRoute route = PackageDownloadRoutePolicy.Resolve(
            package.GithubUrl,
            package.GiteeUrl,
            package.Url,
            ResolveDerivedGithubDownloadUrl(package),
            consecutiveGithubFailures,
            GithubDownloadFailureFallbackThreshold);
        return new LauncherDownloadRoute(
            route.GithubUrl,
            route.GiteeUrl,
            route.SelectedUrl,
            route.PreferGitee);
    }

    internal static LauncherDownloadFailureState LoadDownloadFailureState(
        string statePath,
        LauncherPackageInfo package)
    {
        try
        {
            if (File.Exists(statePath))
            {
                LauncherDownloadFailureState? state = JsonSerializer.Deserialize<LauncherDownloadFailureState>(
                    File.ReadAllText(statePath));
                if (state != null &&
                    string.Equals(state.PackageVersion, package.Version, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(state.PackageSha256, package.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return state with
                    {
                        ConsecutiveGithubDownloadFailures =
                            Math.Max(0, state.ConsecutiveGithubDownloadFailures)
                    };
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("LauncherUpdate", $"Unable to read launcher download failure state: {ex.Message}");
        }

        return new LauncherDownloadFailureState(package.Version, package.Sha256, 0);
    }

    internal static void SaveDownloadFailureState(
        string statePath,
        LauncherDownloadFailureState state)
    {
        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(statePath))
                ?? throw new InvalidOperationException("启动器下载状态目录无效");
            Directory.CreateDirectory(directory);
            string temporaryPath = statePath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(state with
                    {
                        ConsecutiveGithubDownloadFailures =
                            Math.Max(0, state.ConsecutiveGithubDownloadFailures)
                    }));
                File.Move(temporaryPath, statePath, overwrite: true);
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("LauncherUpdate", $"Unable to save launcher download failure state: {ex.Message}");
        }
    }

    internal static void ResetDownloadFailureState(string statePath)
        => TryDeleteFile(statePath);

    private static string ResolveDerivedGithubDownloadUrl(LauncherPackageInfo package)
    {
        string fileName;
        try
        {
            fileName = Path.GetFileName(new Uri(package.Url).LocalPath);
        }
        catch
        {
            fileName = "";
        }

        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            fileName = $"PackingProof_LauncherPatch_v{package.Version}.zip";
        }

        string tag = "v" + package.Version.Trim().TrimStart('v', 'V');
        return $"{DefaultGithubDownloadBaseUrl}/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(fileName)}";
    }

    private static bool IsValidDownloadUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
           (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback);

    private static bool AreSameUrl(string left, string right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out Uri? leftUri) ||
            !Uri.TryCreate(right, UriKind.Absolute, out Uri? rightUri))
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        return Uri.Compare(
            leftUri,
            rightUri,
            UriComponents.AbsoluteUri,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
    }

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

    internal static bool TryDeleteDirectoryWithinRoot(string path, string allowedRoot)
    {
        try
        {
            string normalizedPath = Path.GetFullPath(path);
            string normalizedRoot = Path.GetFullPath(allowedRoot);
            if (!IsPathInside(normalizedPath, normalizedRoot))
                return false;
            if (Directory.Exists(normalizedPath))
                Directory.Delete(normalizedPath, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPathInside(string path, string rootPath)
    {
        string root = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
