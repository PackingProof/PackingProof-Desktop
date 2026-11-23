using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.UpdateCore;
using ExpressPackingMonitoring.Logging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

internal enum AppPatchPreparationStatus
{
    Ready,
    AlreadyReady,
    FullPackageRequired,
    Busy,
    /// <summary>这次补丁要求更新的启动器：启动器已经先更新好了，需要重启软件让新启动器接手。</summary>
    LauncherUpdateRequired,
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
    internal static readonly string UpdateMutexName =
        $@"Local\{BrandMetadata.InstanceNamePrefix}.Launcher.Update";
    private const string PatchPackageType = "baseline_patch";
    // 增量包有 75MB：清单那 75 秒的总超时管不住它，慢网用户每次都正好卡在下到一半。
    // 换成按分钟给的预算 + 下载自身的停滞判据（60 秒没有新字节），断点文件保留，超时后重试从断点继续。
    private static readonly TimeSpan PatchDownloadBudget = TimeSpan.FromMinutes(30);

    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromSeconds(75)
    };

    /// <summary>
    /// 增量包专用客户端：不设 HttpClient 总时长（那会把 75MB 的下载按 75 秒掐断），
    /// 防卡死靠 ResumableFileDownload 的停滞判据与调用方的下载预算。
    /// </summary>
    private static readonly HttpClient SharedPackageClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient _client;
    private readonly HttpClient _packageClient;
    private readonly string _updatesDirectory;
    private readonly string _appBaseDirectory;

    internal AppPatchDownloadService()
        : this(SharedClient, AppPaths.UpdatesCacheDir, AppContext.BaseDirectory, SharedPackageClient)
    {
    }

    internal AppPatchDownloadService(
        HttpClient client,
        string updatesDirectory,
        string appBaseDirectory)
        : this(client, updatesDirectory, appBaseDirectory, client)
    {
    }

    internal AppPatchDownloadService(
        HttpClient client,
        string updatesDirectory,
        string appBaseDirectory,
        HttpClient packageClient)
    {
        _client = client;
        _packageClient = packageClient;
        _updatesDirectory = Path.GetFullPath(updatesDirectory);
        _appBaseDirectory = Path.GetFullPath(appBaseDirectory);
    }

    /// <summary>增量包下载客户端的总超时（守卫用例用来盯住"不能套 75 秒总时长"这条不变量）。</summary>
    internal static TimeSpan PackageDownloadTimeout => SharedPackageClient.Timeout;

    internal async Task<AppPatchPreparationResult> PrepareAsync(
        UpdateCheckResult update,
        IProgress<AppPatchDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (PackagingIdentity.IsPackaged)
        {
            return new AppPatchPreparationResult(
                AppPatchPreparationStatus.FullPackageRequired,
                "商店版由 Microsoft Store 统一更新，不能安装外部增量包");
        }

        if (!LauncherUpdateService.TryResolveInstalledLauncher(
                _appBaseDirectory,
                out _))
        {
            return FullPackage(update, "当前不是标准安装目录，无法自动准备增量更新");
        }

        if (string.IsNullOrWhiteSpace(update.UpdateManifestUrl))
            return FullPackage(update, "此版本没有可用的增量更新描述");

        // 失败时也要能把"完整更新页面/备用网盘"交回给界面，别让用户卡在重试上
        string failureDownloadPage = "";
        string failureFallbackPage = "";
        try
        {
            progress?.Report(new AppPatchDownloadProgress("正在读取增量更新信息"));
            (string manifestJson, string manifestSourceUrl) = await DownloadTextWithMirrorAsync(
                update.UpdateManifestUrl,
                progress,
                cancellationToken);
            AppPatchDescriptor descriptor = ParseDescriptor(manifestJson, update.LatestVersion);
            string fallbackUrl = descriptor.FullDownloadUrl.Length > 0
                ? descriptor.FullDownloadUrl
                : update.DownloadUrl;
            failureDownloadPage = fallbackUrl;
            failureFallbackPage = descriptor.FullDownloadFallbackUrl;

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

            // 这份补丁要求更新的启动器（例如载荷只能由新启动器落盘）时：先把启动器更新掉，绝不先下几十兆。
            if (descriptor.PatchPackage.RequiredLauncherProtocol > 0)
            {
                if (!LauncherUpdateService.TryResolveInstalledLauncher(_appBaseDirectory, out string launcherPath))
                {
                    return FullPackage(
                        update,
                        "无法确定已安装的启动器，不能自动准备这次增量更新",
                        fallbackUrl,
                        descriptor.FullDownloadFallbackUrl);
                }

                if (!LauncherProtocol.Satisfies(
                        LauncherProtocol.ReadState(_updatesDirectory, launcherPath),
                        descriptor.PatchPackage.RequiredLauncherProtocol))
                {
                    progress?.Report(new AppPatchDownloadProgress("这次更新需要新版启动器，正在先更新启动器"));
                    LauncherUpdateOutcome launcherOutcome = await new LauncherUpdateService().CheckAndApplyAsync(
                        enabled: true,
                        cancellationToken,
                        force: true);

                    if (!LauncherRequirementSatisfiedAfterUpdate(
                            launcherOutcome,
                            LauncherProtocol.ReadState(_updatesDirectory, launcherPath),
                            descriptor.PatchPackage.RequiredLauncherProtocol))
                    {
                        return new AppPatchPreparationResult(
                            AppPatchPreparationStatus.Failed,
                            "这次更新需要新版启动器，但启动器自动更新没有成功；请重启软件后重试，或改用完整包升级",
                            fallbackUrl,
                            descriptor.FullDownloadFallbackUrl);
                    }

                    return new AppPatchPreparationResult(
                        AppPatchPreparationStatus.LauncherUpdateRequired,
                        "这次更新需要新版启动器，已自动更新完成（约 3MB）。重启软件后由新启动器完成这次升级",
                        fallbackUrl,
                        descriptor.FullDownloadFallbackUrl);
                }
            }

            return await DownloadAndPublishAsync(
                descriptor,
                manifestJson,
                manifestSourceUrl,
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
                $"补丁下载或校验失败：{ex.Message}",
                failureDownloadPage,
                failureFallbackPage);
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
                        step.ManifestUrl,
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
        string manifestSourceUrl,
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
        string partialDirectory = Path.Combine(_updatesDirectory, "partial");
        string stagingDirectory = Path.Combine(_updatesDirectory, $"staging-{operationId}");
        string backupDirectory = Path.Combine(_updatesDirectory, $"replaced-{operationId}");
        string patchFileName = $"{BrandMetadata.ArtifactPrefix}_AppPatch_v{descriptor.LatestVersion}.zip";
        // 半截文件按版本留在这里，断线后重试（以及根启动器的那次后台下载）都从断点接着下。
        string partialPath = Path.Combine(
            partialDirectory,
            patchFileName + ResumableFileDownload.PartialFileSuffix);
        try
        {
            Directory.CreateDirectory(partialDirectory);
            PruneLegacyDownloadDirectories(_updatesDirectory);
            PackageDownloadRoute route = GetDownloadRoute(
                descriptor.PatchPackage,
                manifestSourceUrl,
                descriptor.DownloadSource);
            progress?.Report(new AppPatchDownloadProgress(
                route.PreferGitee ? "正在从 Gitee 下载增量更新包" : "正在从 GitHub 下载增量更新包"));
            var downloadProgress = new PatchDownloadProgressAdapter(progress);
            string downloadPath;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(PatchDownloadBudget);
            try
            {
                try
                {
                    downloadPath = await DownloadVerifiedPatchAsync(
                        route.SelectedUrl,
                        partialPath,
                        descriptor.PatchPackage,
                        downloadProgress,
                        budget.Token);
                }
                catch (Exception ex) when (IsTransientDownloadFailure(ex, budget.Token))
                {
                    string alternateUrl = ResolveAlternatePatchUrl(route);
                    if (alternateUrl.Length == 0)
                        throw;

                    string failedSource = route.PreferGitee ? "Gitee" : "GitHub";
                    string alternateSource = DescribeDownloadSource(route, alternateUrl);
                    RuntimeLog.Warn(
                        "Update",
                        $"{failedSource} AppPatch download failed, trying {alternateSource}: {ex.Message}");
                    progress?.Report(new AppPatchDownloadProgress(
                        $"{failedSource} 下载失败，正在改用 {alternateSource}"));
                    downloadPath = await DownloadVerifiedPatchAsync(
                        alternateUrl,
                        partialPath,
                        descriptor.PatchPackage,
                        downloadProgress,
                        budget.Token);
                }
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested
                                                     && !cancellationToken.IsCancellationRequested)
            {
                throw new IOException(
                    $"增量包下载超过 {PatchDownloadBudget.TotalMinutes:0} 分钟还没下完；"
                    + "已经下好的部分会留着，稍后重试可以从断点继续");
            }

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
            TryDeleteOwnedDirectory(stagingDirectory);
        }
    }

    /// <summary>
    /// 下载增量包并校验；半截文件失效时由 ResumableFileDownload 自己清掉重下一遍。
    /// 这里是下载失败（断线、停滞）唯一会保留半截文件的地方——下次重试接着下。
    /// </summary>
    private Task<string> DownloadVerifiedPatchAsync(
        string url,
        string partialPath,
        AppPatchPackageInfo package,
        IProgress<ResumableDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        return ResumableFileDownload.DownloadVerifiedAsync(
            _packageClient,
            url,
            partialPath,
            package.Size,
            package.Sha256,
            progress,
            cancellationToken);
    }

    /// <summary>把断点续传的进度翻译成界面上那一行下载进度。</summary>
    private sealed class PatchDownloadProgressAdapter(IProgress<AppPatchDownloadProgress>? target)
        : IProgress<ResumableDownloadProgress>
    {
        public void Report(ResumableDownloadProgress value)
        {
            target?.Report(new AppPatchDownloadProgress(
                "正在下载增量更新包",
                value.BytesReceived,
                value.TotalBytes));
        }
    }

    /// <summary>
    /// 清掉"每次下载建一个目录"那个旧实现留下的 download-&lt;操作号&gt; 目录（进程被强杀时
    /// 会连着几十兆的半截包一起留下）。只清一小时没动过的，正在写的目录不碰。
    /// </summary>
    private static void PruneLegacyDownloadDirectories(string updatesDirectory)
    {
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(updatesDirectory, "download-*"))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < TimeSpan.FromHours(1))
                        continue;
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 删不掉就留着，下一次更新再试。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理旧目录失败不影响本次下载。
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

    /// <summary>
    /// 下载更新清单，主地址失败时改用另一个平台的镜像附件（GitHub ↔ Gitee 的同一 tag、同一文件名）。
    /// 现场（0.0.74 的店里机器）：检查命中 GitHub 后清单地址就在 github.com，而那条链路在店里打不开；
    /// 旧实现只给增量包做了回退，清单这一步挂了就整条中断，增量包的回退永远轮不到。
    /// </summary>
    /// <summary>
    /// 下载更新清单，主地址失败时改用另一个平台的镜像附件（GitHub ↔ Gitee 的同一 tag、同一文件名）。
    /// 现场（0.0.74 的店里机器）：检查命中 GitHub 后清单地址就在 github.com，而那条链路在店里打不开；
    /// 旧实现只给增量包做了回退，清单这一步挂了就整条中断，增量包的回退永远轮不到。
    /// 返回值里带上这份清单**实际**是从哪个地址下来的，后面的增量包就跟着它选源。
    /// </summary>
    private async Task<(string Json, string SourceUrl)> DownloadTextWithMirrorAsync(
        string url,
        IProgress<AppPatchDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await DownloadTextAsync(url, cancellationToken), url);
        }
        catch (Exception ex) when (IsTransientDownloadFailure(ex, cancellationToken))
        {
            string mirrorUrl = UpdateEndpointPolicy.DeriveMirrorDownloadUrl(url);
            if (mirrorUrl.Length == 0
                || string.Equals(mirrorUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }

            RuntimeLog.Warn("Update", $"Manifest download failed, trying mirror: {ex.Message}");
            progress?.Report(new AppPatchDownloadProgress("更新清单下载失败，正在改用镜像源"));
            return (await DownloadTextAsync(mirrorUrl, cancellationToken), mirrorUrl);
        }
    }

    /// <summary>
    /// 第一个地址失败后要试的地址：清单里给了两个平台就用另一个平台（原有行为），
    /// 只给了一个地址时按镜像策略换主机（GitHub ↔ Gitee）。
    /// </summary>
    private static string ResolveAlternatePatchUrl(PackageDownloadRoute route)
    {
        if (!route.PreferGitee
            && !string.Equals(route.GithubUrl, route.GiteeUrl, StringComparison.OrdinalIgnoreCase))
        {
            return route.GiteeUrl;
        }

        string mirrorUrl = UpdateEndpointPolicy.DeriveMirrorDownloadUrl(route.SelectedUrl);
        return string.Equals(mirrorUrl, route.SelectedUrl, StringComparison.OrdinalIgnoreCase)
            ? ""
            : mirrorUrl;
    }

    private static string DescribeDownloadSource(PackageDownloadRoute route, string url)
    {
        if (string.Equals(url, route.GiteeUrl, StringComparison.OrdinalIgnoreCase))
            return "Gitee";
        if (string.Equals(url, route.GithubUrl, StringComparison.OrdinalIgnoreCase))
            return "GitHub";
        return "镜像源";
    }

    private static bool IsTransientDownloadFailure(Exception ex, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested
            && ex is HttpRequestException or IOException or TaskCanceledException;

    /// <summary>
    /// 自动更新启动器之后，这份补丁要求的启动器能力算不算满足。
    ///
    /// 启动器文件刚被换掉时，能力状态文件里记的还是旧启动器那一份：哈希对不上，再读只会得到
    /// "最低能力"，于是会把一次成功的启动器更新误报成失败。换掉了就按"已就绪"收尾——真正能不能装
    /// 这份补丁，下一次启动由新启动器自己按能力号判定（要求高于自身能力时它会明确拒绝，不会装坏）。
    /// </summary>
    internal static bool LauncherRequirementSatisfiedAfterUpdate(
        LauncherUpdateOutcome outcome,
        LauncherProtocol.State installedLauncher,
        int requiredLauncherProtocol) =>
        outcome == LauncherUpdateOutcome.Applied
        || LauncherProtocol.Satisfies(installedLauncher, requiredLauncherProtocol);

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
        string downloadSource = ReadString(root, "download_source").Trim().ToLowerInvariant();
        if (downloadSource is not ("gitee" or "github" or "auto"))
            downloadSource = "gitee";

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
                ReadString(packageElement, "gitee_url"),
                Math.Max(0, ReadInt32(packageElement, "required_launcher_protocol")));
        }

        return new AppPatchDescriptor(
            latestVersion,
            UpdateCheckService.NormalizeVersion(ReadString(root, "patch_baseline_version")),
            ReadBoolean(root, "patch_supported"),
            fullDownloadUrl,
            fullDownloadFallbackUrl,
            downloadSource,
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
        => GetDownloadRoute(package, manifestSourceUrl: null, downloadSource: "gitee");

    /// <param name="manifestSourceUrl">
    /// 这份清单实际是从哪个地址下来的。清单走 gitee.com 时增量包也优先走 Gitee：
    /// 国内机器上 Gitee 附件比 GitHub 快得多，而"清单能下来"本身就说明那条链路是通的。
    /// </param>
    internal static PackageDownloadRoute GetDownloadRoute(
        AppPatchPackageInfo package,
        string? manifestSourceUrl,
        string? downloadSource = "gitee")
    {
        return PackageDownloadRoutePolicy.Resolve(
            package.GithubUrl,
            package.GiteeUrl,
            package.Url,
            derivedGithubUrl: "",
            consecutiveGithubFailures: 0,
            fallbackThreshold: 3,
            manifestSourceUrl,
            downloadSource);
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
                $"{BrandMetadata.ArtifactPrefix}_AppPatch_v{descriptor.LatestVersion}.zip");
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

    /// <summary>缺省按 0 处理：老清单没有这个字段，等于"不要求新启动器"。</summary>
    private static int ReadInt32(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value)
            && value.TryGetInt32(out int result)
            ? result
            : 0;
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

}

internal sealed record AppPatchDescriptor(
    string LatestVersion,
    string PatchBaselineVersion,
    bool PatchSupported,
    string FullDownloadUrl,
    string FullDownloadFallbackUrl,
    string DownloadSource,
    AppPatchPackageInfo PatchPackage);

internal sealed record AppPatchPackageInfo(
    string Type,
    string Url,
    string Sha256,
    long Size,
    string GithubUrl = "",
    string GiteeUrl = "",
    /// <summary>这份补丁要求启动器至少具备的能力号（0 = 不要求）。见 <see cref="LauncherProtocol"/>。</summary>
    int RequiredLauncherProtocol = 0);
