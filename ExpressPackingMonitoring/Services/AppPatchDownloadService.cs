using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExpressPackingMonitoring.UpdateCore;

namespace ExpressPackingMonitoring.Services;

internal enum AppPatchPreparationStatus
{
    Ready,
    AlreadyReady,
    FullPackageRequired,
    Busy,
    Failed
}

internal sealed record AppPatchPreparationResult(
    AppPatchPreparationStatus Status,
    string Message,
    string FullDownloadUrl = "",
    string FullDownloadFallbackUrl = "");

internal sealed record AppPatchDownloadProgress(
    string Message,
    long BytesReceived = 0,
    long TotalBytes = 0);

internal sealed class AppPatchDownloadService
{
    internal const string UpdateMutexName = @"Local\ExpressPackingMonitoring.Launcher.Update";
    private const string PatchPackageType = "baseline_patch";
    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromSeconds(75)
    };

    private readonly HttpClient _client;
    private readonly string _updatesDirectory;
    private readonly string _appBaseDirectory;

    internal AppPatchDownloadService()
        : this(SharedClient, AppPaths.UpdatesCacheDir, AppContext.BaseDirectory)
    {
    }

    internal AppPatchDownloadService(
        HttpClient client,
        string updatesDirectory,
        string appBaseDirectory)
    {
        _client = client;
        _updatesDirectory = Path.GetFullPath(updatesDirectory);
        _appBaseDirectory = Path.GetFullPath(appBaseDirectory);
    }

    internal async Task<AppPatchPreparationResult> PrepareAsync(
        UpdateCheckResult update,
        IProgress<AppPatchDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!LauncherUpdateService.TryResolveInstalledLauncher(
                _appBaseDirectory,
                out _))
        {
            return FullPackage(update, "当前不是标准安装目录，无法自动准备增量更新");
        }

        if (string.IsNullOrWhiteSpace(update.UpdateManifestUrl))
            return FullPackage(update, "此版本没有可用的增量更新描述");

        try
        {
            progress?.Report(new AppPatchDownloadProgress("正在读取增量更新信息"));
            string manifestJson = await DownloadTextAsync(
                update.UpdateManifestUrl,
                cancellationToken);
            AppPatchDescriptor descriptor = ParseDescriptor(manifestJson, update.LatestVersion);
            string fallbackUrl = descriptor.FullDownloadUrl.Length > 0
                ? descriptor.FullDownloadUrl
                : update.DownloadUrl;

            if (!descriptor.PatchSupported)
                return FullPackage(update, "此版本未提供可用的增量包", fallbackUrl, descriptor.FullDownloadFallbackUrl);
            if (!IsPatchUsable(descriptor))
                return FullPackage(update, "增量更新信息不完整", fallbackUrl, descriptor.FullDownloadFallbackUrl);
            if (CompareVersions(AppVersion.Current, descriptor.PatchBaselineVersion) < 0)
            {
                AppPatchPreparationResult? stepUp = await TryPrepareBaselineStepAsync(
                    update,
                    descriptor,
                    progress,
                    cancellationToken);
                if (stepUp != null)
                    return stepUp;
                return FullPackage(
                    update,
                    $"当前版本低于增量更新基线 {descriptor.PatchBaselineVersion}，且未找到可先升级到基线版本的增量包",
                    fallbackUrl,
                    descriptor.FullDownloadFallbackUrl);
            }
            if (CompareVersions(descriptor.LatestVersion, AppVersion.Current) <= 0)
            {
                return new AppPatchPreparationResult(
                    AppPatchPreparationStatus.AlreadyReady,
                    "当前版本已不低于更新版本");
            }

            return await DownloadAndPublishAsync(
                descriptor,
                manifestJson,
                progress,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or JsonException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            RuntimeLog.Error("Update", "Manual AppPatch preparation failed", ex);
            return new AppPatchPreparationResult(
                AppPatchPreparationStatus.Failed,
                $"补丁下载或校验失败：{ex.Message}");
        }
    }

    private async Task<AppPatchPreparationResult?> TryPrepareBaselineStepAsync(
        UpdateCheckResult update,
        AppPatchDescriptor latestDescriptor,
        IProgress<AppPatchDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(update.SourceUrl))
        {
            RuntimeLog.Warn("Update", "无法确定更新来源，跳过基线分步升级查找");
            return null;
        }

        var metadataClient = new UpdateMetadataClient(
            _client,
            log: message => RuntimeLog.Info("Update", message));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string targetVersion = latestDescriptor.PatchBaselineVersion;
        try
        {
            for (int stepIndex = 0; stepIndex < 10; stepIndex++)
            {
                if (targetVersion.Length == 0 || !visited.Add(targetVersion))
                    return null;

                progress?.Report(new AppPatchDownloadProgress(
                    $"当前版本低于新基线，正在查找基线版本 {targetVersion} 的增量包"));
                using ResolvedUpdateManifest? step = await metadataClient.TryResolveManifestForVersionAsync(
                    new[] { update.SourceUrl },
                    targetVersion,
                    cancellationToken);
                if (step == null)
                {
                    RuntimeLog.Warn("Update", $"未找到基线版本 {targetVersion} 的更新描述");
                    return null;
                }

                AppPatchDescriptor stepDescriptor = ParseDescriptor(
                    step.Manifest.RootElement.GetRawText(),
                    step.LatestVersion);
                if (!string.Equals(
                        stepDescriptor.LatestVersion,
                        targetVersion,
                        StringComparison.OrdinalIgnoreCase)
                    || !IsPatchUsable(stepDescriptor)
                    || CompareVersions(stepDescriptor.LatestVersion, AppVersion.Current) <= 0)
                {
                    RuntimeLog.Warn("Update", $"基线版本 {targetVersion} 的增量包不可用");
                    return null;
                }

                if (string.IsNullOrWhiteSpace(stepDescriptor.PatchBaselineVersion)
                    || CompareVersions(AppVersion.Current, stepDescriptor.PatchBaselineVersion) >= 0)
                {
                    AppPatchPreparationResult prepared = await DownloadAndPublishAsync(
                        stepDescriptor,
                        step.Manifest.RootElement.GetRawText(),
                        progress,
                        cancellationToken,
                        requireExactPendingVersion: true);
                    if (prepared.Status == AppPatchPreparationStatus.Busy)
                        return prepared;
                    if (prepared.Status is not (
                            AppPatchPreparationStatus.Ready
                            or AppPatchPreparationStatus.AlreadyReady))
                        return null;

                    return prepared with
                    {
                        Message =
                            $"已准备先升级到基线版本 {stepDescriptor.LatestVersion}；"
                            + $"重启安装后，将自动继续升级到最新版本 {update.LatestVersion}"
                    };
                }

                if (CompareVersions(stepDescriptor.PatchBaselineVersion, targetVersion) >= 0)
                    return null;
                targetVersion = stepDescriptor.PatchBaselineVersion;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Update", $"查找基线分步升级失败：{ex.Message}");
        }

        return null;
    }

    private async Task<AppPatchPreparationResult> DownloadAndPublishAsync(
        AppPatchDescriptor descriptor,
        string manifestJson,
        IProgress<AppPatchDownloadProgress>? progress,
        CancellationToken cancellationToken,
        bool requireExactPendingVersion = false)
    {
        string pendingDirectory = Path.Combine(_updatesDirectory, "pending");
        AppPatchPreparationResult? existing = InspectPendingExclusive(
            pendingDirectory,
            descriptor.LatestVersion,
            requireExactPendingVersion);
        if (existing != null)
            return existing;

        string operationId = Guid.NewGuid().ToString("N");
        string downloadDirectory = Path.Combine(_updatesDirectory, $"download-{operationId}");
        string stagingDirectory = Path.Combine(_updatesDirectory, $"staging-{operationId}");
        string backupDirectory = Path.Combine(_updatesDirectory, $"replaced-{operationId}");
        string patchFileName = $"PackingProof_AppPatch_v{descriptor.LatestVersion}.zip";
        string downloadPath = Path.Combine(downloadDirectory, patchFileName);
        try
        {
            Directory.CreateDirectory(downloadDirectory);
            PackageDownloadRoute route = GetDownloadRoute(descriptor.PatchPackage);
            progress?.Report(new AppPatchDownloadProgress(
                route.PreferGitee ? "正在从 Gitee 下载增量更新包" : "正在从 GitHub 下载增量更新包"));
            try
            {
                await DownloadFileAsync(
                    route.SelectedUrl,
                    downloadPath,
                    descriptor.PatchPackage.Size,
                    progress,
                    cancellationToken);
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested
                && !route.PreferGitee
                && !string.Equals(route.GithubUrl, route.GiteeUrl, StringComparison.OrdinalIgnoreCase)
                && ex is HttpRequestException or IOException or TaskCanceledException)
            {
                RuntimeLog.Warn("Update", $"GitHub AppPatch download failed, trying Gitee: {ex.Message}");
                TryDeleteOwnedFile(downloadPath);
                progress?.Report(new AppPatchDownloadProgress("GitHub 下载失败，正在改用 Gitee"));
                await DownloadFileAsync(
                    route.GiteeUrl,
                    downloadPath,
                    descriptor.PatchPackage.Size,
                    progress,
                    cancellationToken);
            }
            ValidatePackage(downloadPath, descriptor.PatchPackage);

            progress?.Report(new AppPatchDownloadProgress("校验通过，正在准备下次启动安装"));
            Directory.CreateDirectory(stagingDirectory);
            File.Move(downloadPath, Path.Combine(stagingDirectory, patchFileName));
            File.WriteAllText(
                Path.Combine(stagingDirectory, "update_manifest.json"),
                manifestJson,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            AppPatchPreparationResult published = PublishPendingExclusive(
                stagingDirectory,
                pendingDirectory,
                backupDirectory,
                descriptor.LatestVersion,
                requireExactPendingVersion);
            if (published.Status != AppPatchPreparationStatus.Ready)
                return published;
            RuntimeLog.Info(
                "Update",
                $"Manual AppPatch prepared version={descriptor.LatestVersion}");
            return new AppPatchPreparationResult(
                AppPatchPreparationStatus.Ready,
                $"版本 {descriptor.LatestVersion} 的补丁已下载并校验");
        }
        finally
        {
            TryDeleteOwnedDirectory(downloadDirectory);
            TryDeleteOwnedDirectory(stagingDirectory);
        }
    }

    private static AppPatchPreparationResult? InspectPendingExclusive(
        string pendingDirectory,
        string requestedVersion,
        bool requireExactVersion = false)
    {
        return RunExclusive(() =>
        {
            if (TryReadValidPendingVersion(pendingDirectory, out string pendingVersion)
                && IsPendingVersionSatisfied(pendingVersion, requestedVersion, requireExactVersion))
            {
                return new AppPatchPreparationResult(
                    AppPatchPreparationStatus.AlreadyReady,
                    $"版本 {pendingVersion} 的补丁已经准备完成");
            }

            return null;
        });
    }

    private static AppPatchPreparationResult PublishPendingExclusive(
        string stagingDirectory,
        string pendingDirectory,
        string backupDirectory,
        string requestedVersion,
        bool requireExactVersion = false)
    {
        return RunExclusive(() =>
        {
            if (TryReadValidPendingVersion(pendingDirectory, out string pendingVersion)
                && IsPendingVersionSatisfied(pendingVersion, requestedVersion, requireExactVersion))
            {
                return new AppPatchPreparationResult(
                    AppPatchPreparationStatus.AlreadyReady,
                    $"版本 {pendingVersion} 的补丁已经准备完成");
            }

            PublishPendingDirectory(stagingDirectory, pendingDirectory, backupDirectory);
            return new AppPatchPreparationResult(
                AppPatchPreparationStatus.Ready,
                $"版本 {requestedVersion} 的补丁已下载并校验");
        }) ?? new AppPatchPreparationResult(
            AppPatchPreparationStatus.Busy,
            "另一个更新任务正在运行，请稍后重试");
    }

    private static bool IsPendingVersionSatisfied(
        string pendingVersion,
        string requestedVersion,
        bool requireExactVersion)
    {
        int comparison = CompareVersions(pendingVersion, requestedVersion);
        return requireExactVersion ? comparison == 0 : comparison >= 0;
    }

    private static T? RunExclusive<T>(Func<T?> action) where T : class
    {
        bool hasMutex = false;
        using var mutex = new Mutex(initiallyOwned: false, UpdateMutexName);
        try
        {
            try
            {
                hasMutex = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                hasMutex = true;
            }

            return hasMutex ? action() : null;
        }
        finally
        {
            if (hasMutex)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }
    }

    private async Task<string> DownloadTextAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task DownloadFileAsync(
        string url,
        string path,
        long expectedSize,
        IProgress<AppPatchDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? expectedSize;
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream target = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        byte[] buffer = new byte[81920];
        long received = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            progress?.Report(new AppPatchDownloadProgress("正在下载增量更新包", received, total));
        }
    }

    internal static AppPatchDescriptor ParseDescriptor(string json, string fallbackVersion)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string latestVersion = UpdateCheckService.NormalizeVersion(ReadString(root, "latest_version"));
        if (latestVersion.Length == 0)
            latestVersion = UpdateCheckService.NormalizeVersion(fallbackVersion);
        string fullDownloadUrl = ReadString(root, "full_download_page");
        if (fullDownloadUrl.Length == 0)
            fullDownloadUrl = ReadString(root, "release_page");
        string fullDownloadFallbackUrl = ReadString(root, "full_download_fallback_page");

        AppPatchPackageInfo package = new("", "", "", -1);
        if (root.TryGetProperty("patch_package", out JsonElement packageElement)
            && packageElement.ValueKind == JsonValueKind.Object)
        {
            package = new AppPatchPackageInfo(
                ReadString(packageElement, "type"),
                ReadString(packageElement, "url"),
                ReadString(packageElement, "sha256"),
                ReadInt64(packageElement, "size"),
                ReadString(packageElement, "github_url"),
                ReadString(packageElement, "gitee_url"));
        }

        return new AppPatchDescriptor(
            latestVersion,
            UpdateCheckService.NormalizeVersion(ReadString(root, "patch_baseline_version")),
            ReadBoolean(root, "patch_supported"),
            fullDownloadUrl,
            fullDownloadFallbackUrl,
            package);
    }

    private static bool IsPatchUsable(AppPatchDescriptor descriptor)
    {
        return descriptor.LatestVersion.Length > 0
            && descriptor.PatchBaselineVersion.Length > 0
            && string.Equals(
                descriptor.PatchPackage.Type,
                PatchPackageType,
                StringComparison.OrdinalIgnoreCase)
            && (UpdateEndpointPolicy.IsSecureAbsoluteUrl(descriptor.PatchPackage.Url)
                || UpdateEndpointPolicy.IsSecureAbsoluteUrl(descriptor.PatchPackage.GithubUrl)
                || UpdateEndpointPolicy.IsSecureAbsoluteUrl(descriptor.PatchPackage.GiteeUrl))
            && descriptor.PatchPackage.Size > 0
            && descriptor.PatchPackage.Sha256.Length == 64
            && descriptor.PatchPackage.Sha256.All(Uri.IsHexDigit);
    }

    internal static PackageDownloadRoute GetDownloadRoute(AppPatchPackageInfo package)
    {
        return PackageDownloadRoutePolicy.Resolve(
            package.GithubUrl,
            package.GiteeUrl,
            package.Url,
            derivedGithubUrl: "",
            consecutiveGithubFailures: 0,
            fallbackThreshold: 3);
    }

    private static void ValidatePackage(string path, AppPatchPackageInfo package)
    {
        long size = new FileInfo(path).Length;
        if (size != package.Size)
            throw new InvalidDataException($"补丁包大小不匹配，期望 {package.Size}，实际 {size}");
        using FileStream stream = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(hash, package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("补丁包 SHA256 校验失败");
    }

    private static void PublishPendingDirectory(
        string stagingDirectory,
        string pendingDirectory,
        string backupDirectory)
    {
        bool movedExisting = false;
        try
        {
            if (Directory.Exists(pendingDirectory))
            {
                Directory.Move(pendingDirectory, backupDirectory);
                movedExisting = true;
            }
            Directory.Move(stagingDirectory, pendingDirectory);
            if (movedExisting)
                Directory.Delete(backupDirectory, recursive: true);
        }
        catch
        {
            if (!Directory.Exists(pendingDirectory)
                && movedExisting
                && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, pendingDirectory);
            }
            throw;
        }
    }

    private static bool TryReadValidPendingVersion(string pendingDirectory, out string version)
    {
        version = "";
        try
        {
            string manifestPath = Path.Combine(pendingDirectory, "update_manifest.json");
            if (!File.Exists(manifestPath))
                return false;
            string json = File.ReadAllText(manifestPath, Encoding.UTF8);
            AppPatchDescriptor descriptor = ParseDescriptor(json, "");
            if (!IsPatchUsable(descriptor))
                return false;
            string packagePath = Path.Combine(
                pendingDirectory,
                $"PackingProof_AppPatch_v{descriptor.LatestVersion}.zip");
            if (!File.Exists(packagePath))
                return false;
            ValidatePackage(packagePath, descriptor.PatchPackage);
            version = descriptor.LatestVersion;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AppPatchPreparationResult FullPackage(
        UpdateCheckResult update,
        string message,
        string? url = null,
        string? fallbackUrl = null)
    {
        return new AppPatchPreparationResult(
            AppPatchPreparationStatus.FullPackageRequired,
            message,
            string.IsNullOrWhiteSpace(url) ? update.DownloadUrl : url,
            fallbackUrl ?? "");
    }

    private static int CompareVersions(string left, string right)
    {
        return ParseVersion(left).CompareTo(ParseVersion(right));
    }

    private static Version ParseVersion(string value)
    {
        string normalized = UpdateCheckService.NormalizeVersion(value);
        if (!Version.TryParse(normalized, out Version? result))
            throw new InvalidDataException($"版本号格式异常：{value}");
        return result;
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";
    }

    private static bool ReadBoolean(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.GetBoolean();
    }

    private static long ReadInt64(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value)
            && value.TryGetInt64(out long result)
            ? result
            : -1;
    }

    private static void TryDeleteOwnedDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Update", $"Unable to clean owned update directory {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static void TryDeleteOwnedFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Update", $"Unable to clean owned update file {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}

internal sealed record AppPatchDescriptor(
    string LatestVersion,
    string PatchBaselineVersion,
    bool PatchSupported,
    string FullDownloadUrl,
    string FullDownloadFallbackUrl,
    AppPatchPackageInfo PatchPackage);

internal sealed record AppPatchPackageInfo(
    string Type,
    string Url,
    string Sha256,
    long Size,
    string GithubUrl = "",
    string GiteeUrl = "");
