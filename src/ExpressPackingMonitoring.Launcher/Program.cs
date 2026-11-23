using System.Diagnostics;
using System.ComponentModel;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExpressPackingMonitoring.UpdateCore;

internal static class Program
{
    private const string AppRelativePath = "app\\ExpressPackingMonitoring.exe";
    private const string AppDllRelativePath = "app\\ExpressPackingMonitoring.dll";
    // 补丁条目的安装位置：缺省是 app\（旧清单不写 root 也照常），runtime\ 用于过渡补丁投递共享运行时。
    private const string PatchRootApp = "app";
    private const string PatchRootRuntime = "runtime";
    private const string PatchRuntimeSourcePrefix = ".runtime";
    private const string UpdateUrlKey = "UPDATE_CHECK_URL";
    private const string UpdateFallbackUrlKey = "UPDATE_CHECK_FALLBACK_URL";
    private const string DefaultCheckUrlMetadataKey = "LauncherDefaultUpdateCheckUrl";
    private static readonly string DefaultPatchDownloadBaseUrl = BrandMetadata.PatchDownloadBaseUrl;
    private const string PatchPackageType = "baseline_patch";
    private const string WaitForProcessExitOption = "--wait-for-process-exit";
    private const string LaunchedByRootLauncherOption = "--launched-by-root-launcher";
    private static readonly string UpdateMutexName =
        $@"Local\{BrandMetadata.InstanceNamePrefix}.Launcher.Update";
    private const int GithubDownloadFailureFallbackThreshold = 3;
    private const int SuccessfulUpdateCheckCacheHours = 12;
    private static readonly string InstanceNamePrefix = BrandMetadata.InstanceNamePrefix;
    private const string CameraMonitorRole = "CameraMonitor";
    private const string PrintStationRole = "PrintStation";
    /// <summary>本启动器装不了的 pending 补丁挪到这里：不再每次启动重试（补丁本身不删，换新启动器后还能用）。</summary>
    private const string RejectedPendingDirectoryName = "pending-rejected";
    /// <summary>等另一个启动器装完补丁的上限。装一份 73MB 过渡包约 20 秒，给足余量即可；拿不到就不启动主程序。</summary>
    private static readonly TimeSpan PendingInstallLockWait = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan NetworkUpdateTimeout = TimeSpan.FromSeconds(75);
    // 增量包预算：只给"检查元数据"用 75 秒是不够下 75MB 的，下载单独放宽（停滞判据仍然生效）。
    private static readonly TimeSpan PatchDownloadBudget = TimeSpan.FromMinutes(30);
    private const int MetadataRequestAttempts = 2;
    private const uint InfoIcon = 0x00000040;
    private const uint ErrorIcon = 0x00000010;
    private const uint YesNoButtons = 0x00000004;
    private const uint DialogTimeoutMs = 60000;
    private const int StartupWindowTimeoutMs = 30000;
    private const int StartupPollIntervalMs = 200;
    private const string VcRuntimeDownloadUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
    private const string CrashDumpDirectoryName = "dumps";
    private static readonly string[] VcRuntimeFileNames =
    [
        "vcruntime140.dll",
        "vcruntime140_1.dll",
        "msvcp140.dll",
        "concrt140.dll",
        "ucrtbase.dll",
        "api-ms-win-crt-runtime-l1-1-0.dll"
    ];
    private static readonly string[] NativeLibraryRelativePaths =
    [
        "app\\OpenCvSharpExtern.dll",
        "app\\onnxruntime.dll",
        "app\\DirectML.dll",
        "app\\libvlc\\win-x64\\libvlc.dll"
    ];
    private static bool _useChinese;

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    [STAThread]
    private static int Main(string[] args)
    {
        _useChinese = ResolveChineseLanguage();
        if (!TryPrepareStartupArguments(args, out string[] appArgs, out string waitError))
        {
            WriteLog("等待旧主程序退出失败：" + waitError);
            ShowMessage(
                _useChinese ? "无法重启更新" : "Unable to restart update",
                waitError,
                ErrorIcon);
            return 3;
        }

        string baseDir = AppContext.BaseDirectory;
        string appPath = Path.Combine(baseDir, AppRelativePath);
        bool appAlreadyRunning = IsAppRunning(appPath);
        bool hasPendingUpdate = HasPendingUpdate();
        WriteLog($"启动器启动：appRunning={appAlreadyRunning}, pending={hasPendingUpdate}");
        if (appAlreadyRunning && hasPendingUpdate)
            WriteLog("主程序正在运行，跳过 pending 安装，等待下次完全关闭主程序后再安装");

        // 拿不到更新锁时必须**等**另一个启动器把补丁装完，绝不能跳过安装直接启动主程序：
        // 0.0.83 现场就是这样把主程序拉到"文件正在被覆盖"的目录上启动，主程序起不来，用户只能手动再开一次。
        PendingInstallOutcome pendingInstall = appAlreadyRunning
            ? new PendingInstallOutcome(null, Deferred: false)
            : RunExclusivePendingInstall(baseDir);
        if (pendingInstall.Deferred)
        {
            WriteLog("另一个启动器正在安装更新，本次不启动主程序");
            ShowTimedMessageAsync(
                _useChinese ? "正在安装更新" : "Update in progress",
                _useChinese
                    ? "另一个启动器正在安装更新，请等它完成后重新打开本软件"
                    : "Another launcher is installing the update. Reopen the app after it finishes.",
                InfoIcon).Join();
            return 0;
        }

        UpdateNotification? notification = pendingInstall.Notification;

        // 共享运行时布局：主程序没在跑时才迁移老装机的平铺运行时副本（运行中搬文件会失败）。
        SharedRuntimeLaunchContext runtimeContext = SharedRuntimeLayout.PrepareForLaunch(baseDir, allowMigration: !appAlreadyRunning);
        if (runtimeContext.Error is not null)
            WriteLog("共享运行时布局处理失败：" + runtimeContext.Error);
        if (runtimeContext.UsedLayout)
            WriteLog(
                $"共享运行时布局可用：{runtimeContext.LayoutSummary}；本次迁移旧运行时文件 {runtimeContext.MigratedLegacyFiles} 个"
                + (runtimeContext.RemovedLegacyBackup ? "，已清理回滚备份" : ""));
        else if (runtimeContext.LayoutFound)
            WriteLog("共享运行时布局存在但不完整，按系统 .NET 运行时启动");
        else
            WriteLog("未发现共享运行时布局，按系统 .NET 运行时启动");
        if (runtimeContext.RepairedMisplacedLayout)
            WriteLog("运行时装在了 app\\runtime\\（老启动器应用过渡补丁的结果），已搬回安装根目录 runtime\\");

        PrepareAppHostCetCompatibility(appPath);

        // 写下"本启动器具备哪些能力"，主程序据此判断这份补丁到底装不装得上。
        // 老启动器不写这个文件，主程序会按最低能力处理——这正是 0.0.81 机器的正确判定。
        LauncherProtocol.WriteState(
            GetUpdatesCacheDir(),
            Environment.ProcessPath ?? Path.Combine(baseDir, "ExpressPackingMonitoring.exe"),
            typeof(Program).Assembly.GetName().Version?.ToString() ?? "");

        if (appAlreadyRunning)
        {
            if (!TryActivateExistingApp(appArgs))
            {
                int startResult = StartApp(baseDir, appPath, appArgs, runtimeContext.DotNetRoot);
                if (startResult != 0)
                    return startResult;
            }
        }
        else
        {
            int startResult = StartApp(baseDir, appPath, appArgs, runtimeContext.DotNetRoot);
            if (startResult != 0)
                return startResult;
        }

        Thread? backgroundUpdateThread = null;
        if (notification == null && !hasPendingUpdate)
        {
            WriteLog("启动后台自动检查更新");
            backgroundUpdateThread = StartBackgroundUpdateDownload(baseDir);
        }
        else if (notification != null)
        {
            WriteLog("已有更新结果提示，跳过本次后台下载");
        }
        else
        {
            WriteLog("已有 pending 更新包，跳过本次后台下载，等待下次启动安装");
        }

        Thread? notificationThread = null;
        if (notification != null)
        {
            notificationThread = ShowTimedMessageAsync(
                notification.Title,
                notification.Message,
                notification.IsError ? ErrorIcon : InfoIcon);
        }

        backgroundUpdateThread?.Join();
        notificationThread?.Join();
        return 0;
    }

    private static bool TryPrepareStartupArguments(
        IReadOnlyList<string> args,
        out string[] appArgs,
        out string error)
    {
        var forwarded = new List<string>();
        int waitPid = 0;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i] ?? "";
            if (string.Equals(arg, LaunchedByRootLauncherOption, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(arg, WaitForProcessExitOption, StringComparison.OrdinalIgnoreCase))
            {
                forwarded.Add(arg);
                continue;
            }

            if (i + 1 >= args.Count
                || !int.TryParse(args[++i], out waitPid)
                || waitPid <= 0
                || waitPid == Environment.ProcessId)
            {
                appArgs = Array.Empty<string>();
                error = _useChinese ? "等待进程参数无效" : "Invalid wait process argument";
                return false;
            }
        }

        if (waitPid > 0 && !WaitForProcessExit(waitPid, TimeSpan.FromSeconds(20), out error))
        {
            appArgs = Array.Empty<string>();
            return false;
        }

        appArgs = forwarded.ToArray();
        error = "";
        return true;
    }

    private static bool WaitForProcessExit(int processId, TimeSpan timeout, out string error)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                error = _useChinese
                    ? "旧主程序未能及时退出，请稍后从根目录启动器重试"
                    : "The previous application did not exit in time. Try the root launcher again later.";
                return false;
            }
        }
        catch (ArgumentException)
        {
            // 进程已退出。
        }
        catch (InvalidOperationException)
        {
            // 进程已退出。
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        error = "";
        return true;
    }

    private static PendingInstallOutcome RunExclusivePendingInstall(string baseDir)
    {
        using var mutex = new Mutex(initiallyOwned: false, UpdateMutexName);
        bool hasLock = false;
        try
        {
            try
            {
                // 这里等而不是跳过：另一个启动器正在覆盖 app\ 时启动主程序，会把它拉到半新半旧的目录上。
                hasLock = mutex.WaitOne(PendingInstallLockWait);
            }
            catch (AbandonedMutexException)
            {
                hasLock = true;
            }

            if (!hasLock)
            {
                WriteLog($"等了 {PendingInstallLockWait.TotalMinutes:0} 分钟仍拿不到更新锁，本次不启动主程序");
                return new PendingInstallOutcome(null, Deferred: true);
            }

            return new PendingInstallOutcome(InstallPendingUpdateQuietly(baseDir), Deferred: false);
        }
        finally
        {
            if (hasLock)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }
    }

    private static UpdateNotification? InstallPendingUpdateQuietly(string baseDir)
    {
        return RunExclusiveUpdateWork("pending 安装", () =>
        {
            try
            {
                return InstallPendingUpdate(baseDir);
            }
            catch (Exception ex)
            {
                WriteLog("安装 pending 更新失败：" + ex);
                return null;
            }
        });
    }

    private static Thread StartBackgroundUpdateDownload(string baseDir)
    {
        var thread = new Thread(() =>
        {
            UpdateNotification? notification = RunExclusiveUpdateDownload(baseDir);
            if (notification != null)
            {
                Thread notificationThread = ShowTimedMessageAsync(
                    notification.Title,
                    notification.Message,
                    notification.IsError ? ErrorIcon : InfoIcon);
                notificationThread.Join();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = false;
        thread.Start();
        return thread;
    }

    private static UpdateNotification? RunExclusiveUpdateDownload(string baseDir)
    {
        return RunExclusiveUpdateWork("后台下载", () =>
        {
            using var cts = new CancellationTokenSource(NetworkUpdateTimeout);
            return CheckAndDownloadPatchUpdateAsync(baseDir, cts.Token).GetAwaiter().GetResult();
        });
    }

    private static UpdateNotification? RunExclusiveUpdateWork(
        string operationName,
        Func<UpdateNotification?> work)
    {
        bool hasLock = false;

        try
        {
            using var mutex = new Mutex(initiallyOwned: false, UpdateMutexName);
            try
            {
                try
                {
                    hasLock = mutex.WaitOne(TimeSpan.Zero);
                }
                catch (AbandonedMutexException)
                {
                    hasLock = true;
                }

                if (!hasLock)
                {
                    WriteLog($"检测到另一个启动器正在处理更新，跳过本次{operationName}");
                    return null;
                }

                return work();
            }
            finally
            {
                if (hasLock)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog($"启动器更新{operationName}异常：" + ex);
            return null;
        }
    }

    /// <summary>
    /// 把这份启动器装不了的 pending 补丁挪到 pending-rejected\&lt;版本&gt;：补丁本身是合法的，
    /// 换成新启动器后还能用，所以不删；挪走是为了不再每次启动都失败一遍（0.0.82 现场就是那样循环的）。
    /// </summary>
    private static void RejectPendingPatch(string pendingDir, string version)
    {
        try
        {
            string rejectedRoot = Path.Combine(GetUpdatesCacheDir(), RejectedPendingDirectoryName);
            string segment = string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim();
            string rejectedDirectory = Path.Combine(rejectedRoot, segment);
            if (Directory.Exists(rejectedDirectory))
            {
                Directory.Delete(rejectedDirectory, true);
            }
            Directory.CreateDirectory(rejectedRoot);
            Directory.Move(pendingDir, rejectedDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteLog("挪走装不了的 pending 补丁失败：" + ex.Message);
        }
    }

    private static UpdateNotification? InstallPendingUpdate(string baseDir)
    {
        string pendingDir = GetPendingUpdateDir();
        string manifestPath = Path.Combine(pendingDir, "update_manifest.json");
        if (!File.Exists(manifestPath))
        {
            WriteLog("未发现 pending 更新描述，跳过安装");
            return null;
        }

        string patchZipPath = FindPendingPatchZip(pendingDir);
        if (string.IsNullOrWhiteSpace(patchZipPath))
        {
            WriteLog("pending 更新缺少 Patch zip，已清理 pending");
            TryDeleteDirectory(pendingDir);
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            UpdateDescriptor descriptor = ReadUpdateDescriptor(document.RootElement, "");
            ValidatePatchDescriptor(descriptor);
            if (descriptor.PatchPackage.RequiredLauncherProtocol > LauncherProtocol.CurrentLauncherProtocol)
            {
                // 装不了就明确说清、并把这一份挪出 pending：留在原地会变成"每次启动都失败一次"的死循环
                // （0.0.82 现场的“Patch 文件不存在：dotnet.exe”就是这样反复刷出来的）。
                string requirement =
                    $"这份补丁要求启动器能力 {descriptor.PatchPackage.RequiredLauncherProtocol}，"
                    + $"本启动器只有 {LauncherProtocol.CurrentLauncherProtocol}，本机还不能装";
                WriteLog("pending Patch " + requirement + $"，已挪到 {RejectedPendingDirectoryName}，等主程序更新启动器后再试");
                RejectPendingPatch(pendingDir, descriptor.LatestVersion);
                return BuildManualUpdateNotification(descriptor, ManualUpdateReason.LauncherUpdateRequired);
            }

            string currentVersion = ReadInstalledAppVersion(baseDir);
            WriteLog($"准备安装 pending Patch：current={currentVersion}, latest={descriptor.LatestVersion}, baseline={descriptor.PatchBaselineVersion}");
            if (string.IsNullOrWhiteSpace(currentVersion))
            {
                WriteLog("无法读取当前版本，跳过 pending 更新安装");
                return null;
            }

            if (CompareVersions(currentVersion, descriptor.LatestVersion) >= 0)
            {
                WriteLog($"当前版本 {currentVersion} 已不低于 pending 版本 {descriptor.LatestVersion}，清理 pending 更新");
                TryDeleteDirectory(pendingDir);
                return null;
            }

            if (!string.IsNullOrWhiteSpace(descriptor.PatchBaselineVersion) &&
                CompareVersions(currentVersion, descriptor.PatchBaselineVersion) < 0)
            {
                WriteLog($"当前版本 {currentVersion} 低于 pending Patch 基线 {descriptor.PatchBaselineVersion}，清理 pending 更新");
                TryDeleteDirectory(pendingDir);
                return BuildManualUpdateNotification(descriptor, ManualUpdateReason.VersionBelowBaseline);
            }

            ValidateDownloadedFileSize(patchZipPath, descriptor.PatchPackage.Size);
            string actualHash = ComputeSha256(patchZipPath);
            if (!string.Equals(actualHash, descriptor.PatchPackage.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("pending Patch 包 SHA256 校验失败");
            WriteLog("pending Patch 包校验通过，开始覆盖 app 文件");

            InstallPatchZip(baseDir, patchZipPath, descriptor);
            TryDeleteDirectory(pendingDir);
            ResetPatchDownloadFailureState();
            WriteLog($"pending Patch 更新完成：{descriptor.LatestVersion}");
            return new UpdateNotification(_useChinese ? "更新完成" : "Update complete", BuildSuccessMessage(descriptor), false);
        }
        catch (Exception ex)
        {
            WriteLog("pending Patch 更新失败：" + ex);

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
                UpdateDescriptor descriptor = ReadUpdateDescriptor(document.RootElement, "");
                return new UpdateNotification(_useChinese ? "更新失败" : "Update failed", BuildFailedMessage(descriptor), true);
            }
            catch
            {
                return null;
            }
            finally
            {
                TryDeleteDirectory(pendingDir);
            }
        }
    }

    private static async Task<UpdateNotification?> CheckAndDownloadPatchUpdateAsync(
        string baseDir,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!ReadAutoCheckEnabled())
            {
                WriteLog("自动检查更新已关闭，跳过后台检查");
                return null;
            }

            string currentVersion = ReadInstalledAppVersion(baseDir);
            if (string.IsNullOrWhiteSpace(currentVersion))
            {
                WriteLog("无法读取当前版本，跳过自动更新检查");
                return null;
            }
            bool allowPrerelease = UpdateChannelPolicy.AllowPrerelease();
            if (ShouldSkipRecentSuccessfulUpdateCheck(currentVersion, allowPrerelease))
            {
                WriteLog(
                    $"最近 {SuccessfulUpdateCheckCacheHours} 小时已成功检查当前版本"
                    + $"（预览渠道={(allowPrerelease ? "开" : "关")}），跳过重复请求");
                return null;
            }

            IReadOnlyList<string> checkUrls = GetUpdateCheckUrls(baseDir);
            if (checkUrls.Count == 0)
            {
                WriteLog("自动检查更新地址为空，跳过后台检查");
                return null;
            }

            WriteLog($"自动检查更新开始：current={currentVersion}, sources={string.Join(" -> ", checkUrls)}");
            var metadataClient = new UpdateMetadataClient(
                HttpClient,
                attemptsPerSource: MetadataRequestAttempts,
                retryDelay: TimeSpan.FromMilliseconds(500),
                log: message => WriteLog("更新元数据：" + message),
                allowPrerelease: allowPrerelease);
            using ResolvedUpdateManifest resolved = await metadataClient.FetchLatestManifestAsync(
                checkUrls,
                cancellationToken);
            string latestVersion = resolved.LatestVersion;
            WriteLog($"自动检查更新版本：current={currentVersion}, latest={latestVersion}");
            if (CompareVersions(latestVersion, currentVersion) <= 0)
            {
                SaveSuccessfulUpdateCheck(currentVersion, latestVersion, resolved.SourceUrl, allowPrerelease);
                WriteLog("当前已是最新版或高于远程版本，不下载 Patch");
                return null;
            }

            WriteLog($"找到更新描述文件：{resolved.ManifestUrl}");
            UpdateDescriptor descriptor = ReadUpdateDescriptor(resolved.Manifest.RootElement, latestVersion);
            WriteLog($"更新描述读取完成：latest={descriptor.LatestVersion}, patchSupported={descriptor.PatchSupported}, baseline={descriptor.PatchBaselineVersion}");

            if (!descriptor.PatchSupported)
            {
                SaveSuccessfulUpdateCheck(currentVersion, latestVersion, resolved.SourceUrl, allowPrerelease);
                WriteLog("更新描述标记不支持自动增量更新，提示用户下载完整包");
                return BuildManualUpdateNotification(descriptor, ManualUpdateReason.PatchNotSupported);
            }

            if (!string.IsNullOrWhiteSpace(descriptor.PatchBaselineVersion) &&
                CompareVersions(currentVersion, descriptor.PatchBaselineVersion) < 0)
            {
                bool stepPrepared = await TryPrepareBaselineStepAsync(
                    checkUrls,
                    currentVersion,
                    descriptor,
                    cancellationToken);
                if (stepPrepared)
                {
                    SaveSuccessfulUpdateCheck(currentVersion, latestVersion, resolved.SourceUrl, allowPrerelease);
                    WriteLog($"当前版本 {currentVersion} 低于 Patch 基线 {descriptor.PatchBaselineVersion}，已准备先升级到基线版本，下次启动安装");
                    return null;
                }
                SaveSuccessfulUpdateCheck(currentVersion, latestVersion, resolved.SourceUrl, allowPrerelease);
                WriteLog($"当前版本 {currentVersion} 低于 Patch 基线 {descriptor.PatchBaselineVersion}，未找到可先升级到基线的增量包，提示用户下载完整包");
                return BuildManualUpdateNotification(descriptor, ManualUpdateReason.VersionBelowBaseline);
            }

            if (!IsPatchDescriptorUsable(descriptor))
            {
                SaveSuccessfulUpdateCheck(currentVersion, latestVersion, resolved.SourceUrl, allowPrerelease);
                WriteLog("更新描述中的 Patch 信息不完整，提示用户下载完整包");
                return BuildManualUpdateNotification(descriptor, ManualUpdateReason.PatchDescriptorUnavailable);
            }

            await DownloadPendingPatchAsync(resolved.Manifest.RootElement, descriptor, resolved.ManifestUrl);
            SaveSuccessfulUpdateCheck(currentVersion, latestVersion, resolved.SourceUrl, allowPrerelease);
            WriteLog($"Patch 已下载到 pending，下次启动安装：{descriptor.LatestVersion}");
            return null;
        }
        catch (OperationCanceledException ex)
        {
            WriteLog("自动检查更新超时：" + ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            WriteLog("自动检查更新失败：" + ex);
            return null;
        }
    }

    private static async Task<bool> TryPrepareBaselineStepAsync(
        IReadOnlyList<string> checkUrls,
        string currentVersion,
        UpdateDescriptor latestDescriptor,
        CancellationToken cancellationToken)
    {
        try
        {
            var metadataClient = new UpdateMetadataClient(
                HttpClient,
                attemptsPerSource: MetadataRequestAttempts,
                retryDelay: TimeSpan.FromMilliseconds(500),
                log: message => WriteLog("更新元数据：" + message));
            string targetVersion = latestDescriptor.PatchBaselineVersion;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int stepIndex = 0; stepIndex < 10; stepIndex++)
            {
                if (string.IsNullOrWhiteSpace(targetVersion) || !visited.Add(targetVersion))
                    return false;

                WriteLog($"尝试查找基线版本 {targetVersion} 的增量包");
                using ResolvedUpdateManifest? step = await metadataClient.TryResolveManifestForVersionAsync(
                    checkUrls,
                    targetVersion,
                    cancellationToken);
                if (step == null)
                {
                    WriteLog($"未找到基线版本 {targetVersion} 的更新描述");
                    return false;
                }

                UpdateDescriptor stepDescriptor = ReadUpdateDescriptor(
                    step.Manifest.RootElement,
                    step.LatestVersion);
                if (!string.Equals(stepDescriptor.LatestVersion, targetVersion, StringComparison.OrdinalIgnoreCase)
                    || !stepDescriptor.PatchSupported
                    || !IsPatchDescriptorUsable(stepDescriptor)
                    || CompareVersions(stepDescriptor.LatestVersion, currentVersion) <= 0)
                {
                    WriteLog($"基线版本 {targetVersion} 的增量包不可用");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(stepDescriptor.PatchBaselineVersion)
                    || CompareVersions(currentVersion, stepDescriptor.PatchBaselineVersion) >= 0)
                {
                    await DownloadPendingPatchAsync(
                        step.Manifest.RootElement,
                        stepDescriptor,
                        step.ManifestUrl);
                    WriteLog($"基线版本 {stepDescriptor.LatestVersion} 的补丁已下载到 pending，下次启动先安装它");
                    return true;
                }

                if (CompareVersions(stepDescriptor.PatchBaselineVersion, targetVersion) >= 0)
                    return false;
                targetVersion = stepDescriptor.PatchBaselineVersion;
            }

            return false;
        }
        catch (OperationCanceledException ex)
        {
            WriteLog("查找基线版本增量包超时：" + ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            WriteLog("查找基线版本增量包失败：" + ex);
            return false;
        }
    }

    /// <summary>
    /// 下载增量包到 pending。
    ///
    /// 这里刻意不用元数据检查那 75 秒的预算：75MB 的过渡包在店里那条网上要下好几分钟，
    /// 按 75 秒掐断只会让用户永远下不完。下载单独给一段宽松预算，真正防卡死靠
    /// ResumableFileDownload 的停滞判据（60 秒没有新字节就放弃，字节留着下次接着下）。
    /// </summary>
    private static async Task DownloadPendingPatchAsync(
        JsonElement manifestRoot,
        UpdateDescriptor descriptor,
        string manifestSourceUrl)
    {
        if (descriptor.PatchPackage.RequiredLauncherProtocol > LauncherProtocol.CurrentLauncherProtocol)
        {
            // 本启动器装不了这一份：不要白下几十兆，等主程序把启动器换掉之后再来。
            WriteLog(
                $"这份补丁要求启动器能力 {descriptor.PatchPackage.RequiredLauncherProtocol}，"
                + $"本启动器只有 {LauncherProtocol.CurrentLauncherProtocol}，本次不下载（等主程序更新启动器）");
            return;
        }

        string updatesDir = GetUpdatesCacheDir();
        string pendingDir = GetPendingUpdateDir();
        string partialDir = Path.Combine(updatesDir, "partial");
        string patchZipName = GetPatchZipFileName(descriptor);
        string pendingPatchPath = Path.Combine(pendingDir, patchZipName);
        string pendingManifestPath = Path.Combine(pendingDir, "update_manifest.json");
        string partialPath = Path.Combine(partialDir, patchZipName + ResumableFileDownload.PartialFileSuffix);
        using var budget = new CancellationTokenSource(PatchDownloadBudget);
        CancellationToken downloadToken = budget.Token;

        try
        {
            Directory.CreateDirectory(updatesDir);
            Directory.CreateDirectory(partialDir);

            string derivedGithubUrl = BuildDefaultGithubPatchDownloadUrl(descriptor, patchZipName);
            PatchDownloadFailureState failureState = LoadPatchDownloadFailureState(descriptor.LatestVersion);
            PackageDownloadRoute route = PackageDownloadRoutePolicy.Resolve(
                descriptor.PatchPackage.GithubUrl,
                descriptor.PatchPackage.GiteeUrl,
                descriptor.PatchPackage.Url,
                derivedGithubUrl,
                failureState.ConsecutiveGithubDownloadFailures,
                GithubDownloadFailureFallbackThreshold,
                manifestSourceUrl,
                descriptor.DownloadSource);

            string downloadedPath;
            try
            {
                WriteLog($"准备下载 Patch：version={descriptor.LatestVersion}, source={(route.PreferGitee ? "gitee" : "github")}, failures={failureState.ConsecutiveGithubDownloadFailures}, url={route.SelectedUrl}");
                downloadedPath = await DownloadVerifiedPatchAsync(
                    route.SelectedUrl, partialPath, descriptor, downloadToken);
            }
            catch (Exception ex) when (!downloadToken.IsCancellationRequested
                                       && !AreSameUrl(route.GithubUrl, route.GiteeUrl))
            {
                string alternateUrl = route.PreferGitee ? route.GithubUrl : route.GiteeUrl;
                if (string.IsNullOrWhiteSpace(alternateUrl))
                    throw;
                if (!route.PreferGitee)
                {
                    failureState.ConsecutiveGithubDownloadFailures++;
                    SavePatchDownloadFailureState(failureState);
                    WriteLog($"GitHub Patch 下载失败 {failureState.ConsecutiveGithubDownloadFailures}/{GithubDownloadFailureFallbackThreshold}：{ex.Message}");
                    WriteLog($"GitHub Patch 下载失败，本次立即改用 Gitee：{alternateUrl}");
                }
                else
                {
                    WriteLog($"Gitee Patch 下载失败，本次立即改用 GitHub：{alternateUrl}：{ex.Message}");
                }
                downloadedPath = await DownloadVerifiedPatchAsync(
                    alternateUrl, partialPath, descriptor, downloadToken);
            }

            WriteLog($"Patch 下载校验通过：version={descriptor.LatestVersion}, size={(new FileInfo(downloadedPath)).Length}");

            SafeDeleteDirectory(pendingDir);
            Directory.CreateDirectory(pendingDir);
            File.Move(downloadedPath, pendingPatchPath);
            File.WriteAllText(pendingManifestPath, manifestRoot.GetRawText(), Encoding.UTF8);
            ResetPatchDownloadFailureState();
            WriteLog("Patch 已保存到 pending，等待下次启动安装");
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            throw new IOException(
                $"增量包下载超过 {PatchDownloadBudget.TotalMinutes:0} 分钟还没下完；"
                + "已经下好的部分会留着，下次启动继续从断点下载");
        }
        finally
        {
            // 断点文件故意留着：下次启动的这次检查会带上 Range 从断点继续。
            // download\ 是老版本写半截包的地方，这里顺手清掉，别让它白占几十兆。
            TryDeleteDirectory(Path.Combine(updatesDir, "download"));
        }
    }

    /// <summary>
    /// 下载增量包并校验；断点文件在校验不过时由 ResumableFileDownload 自己清掉重来，
    /// 断线或停滞时保留，下次启动接着下。
    /// </summary>
    private static Task<string> DownloadVerifiedPatchAsync(
        string url,
        string partialPath,
        UpdateDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        return ResumableFileDownload.DownloadVerifiedAsync(
            HttpClient,
            url,
            partialPath,
            descriptor.PatchPackage.Size,
            descriptor.PatchPackage.Sha256,
            progress: null,
            cancellationToken);
    }

    private static void InstallPatchZip(string baseDir, string patchZipPath, UpdateDescriptor descriptor)
    {
        string stagingDir = Path.Combine(GetUpdatesCacheDir(), "staging");
        SafeDeleteDirectory(stagingDir);

        try
        {
            ZipFile.ExtractToDirectory(patchZipPath, stagingDir, overwriteFiles: true);
            InstallPatchFromStaging(baseDir, stagingDir, descriptor);
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    private static void InstallPatchFromStaging(string baseDir, string stagingDir, UpdateDescriptor descriptor)
    {
        string manifestPath = Path.Combine(stagingDir, "patch_manifest.json");
        string patchFilesDir = Path.Combine(stagingDir, "files");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("Patch 包缺少 patch_manifest.json");
        if (!Directory.Exists(patchFilesDir))
            throw new InvalidOperationException("Patch 包缺少 files 目录");

        PatchManifest manifest = ReadPatchManifest(manifestPath);
        ValidatePatchManifest(manifest, descriptor);
        ValidatePatchFiles(patchFilesDir, manifest.Files);
        ValidateApprovedDrawableTargets(baseDir, patchFilesDir, manifest.Files);

        string backupRoot = GetUpdateBackupDir();
        SafeDeleteDirectory(backupRoot);
        Directory.CreateDirectory(backupRoot);

        var backups = new List<FileBackup>();
        int deletedCount = 0;
        try
        {
            foreach (PatchFile file in manifest.Files)
            {
                string targetPath = GetSafeChildPath(ResolvePatchTargetRoot(baseDir, file.Root), file.RelativePath);
                string backupPath = GetSafeChildPath(Path.Combine(backupRoot, file.Root), file.RelativePath);
                bool existed = File.Exists(targetPath);
                FileAttributes originalAttributes = FileAttributes.Normal;
                if (existed)
                {
                    originalAttributes = File.GetAttributes(targetPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(targetPath, backupPath, overwrite: true);
                    RemoveReadOnlyAttribute(backupPath);
                }

                backups.Add(new FileBackup(targetPath, backupPath, existed, originalAttributes));
            }

            foreach (PatchDelete entry in manifest.Deletes)
            {
                // 要清理的旧文件也先进备份：装到一半失败必须能整体还原，不能删了回不来。
                string targetPath = GetSafeChildPath(ResolvePatchTargetRoot(baseDir, entry.Root), entry.RelativePath);
                if (!File.Exists(targetPath)) continue;
                string backupPath = GetSafeChildPath(Path.Combine(backupRoot, entry.Root), entry.RelativePath);
                FileAttributes originalAttributes = File.GetAttributes(targetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(targetPath, backupPath, overwrite: true);
                RemoveReadOnlyAttribute(backupPath);
                backups.Add(new FileBackup(targetPath, backupPath, Existed: true, originalAttributes));
            }

            foreach (PatchFile file in manifest.Files)
            {
                string sourcePath = ResolvePatchSourcePath(patchFilesDir, file);
                string targetPath = GetSafeChildPath(ResolvePatchTargetRoot(baseDir, file.Root), file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                RemoveReadOnlyAttribute(targetPath);
                File.Copy(sourcePath, targetPath, overwrite: true);
                RemoveReadOnlyAttribute(targetPath);
            }

            foreach (PatchDelete entry in manifest.Deletes)
            {
                string targetPath = GetSafeChildPath(ResolvePatchTargetRoot(baseDir, entry.Root), entry.RelativePath);
                if (File.Exists(targetPath))
                {
                    RemoveReadOnlyAttribute(targetPath);
                    File.Delete(targetPath);
                    deletedCount++;
                }
            }
            int prunedDirectoryCount = PruneEmptyDirectories(manifest, baseDir);
            if (deletedCount > 0 || prunedDirectoryCount > 0)
                WriteLog($"已清理 {deletedCount} 个旧版本遗留文件、{prunedDirectoryCount} 个空目录");
            DropSeededRuntimeLayoutIfPatchReplacedRuntimeFiles(baseDir, manifest);

            ValidateInstalledApp(baseDir, descriptor.LatestVersion);
            TryDeleteDirectory(backupRoot);
        }
        catch
        {
            RollbackFiles(backups);
            throw;
        }
    }

    /// <summary>
    /// 补丁换了运行时载荷、而布局又是主程序用硬链接落成的那一种时，把布局丢掉：原地覆盖
    /// 会把 runtime\ 里的内容换掉而目录名与 layout.json 还是旧的；丢掉之后主程序下次启动
    /// 会按新产物重新落成。安装包装出来、过渡补丁投递过来的布局是独立副本，一律不动。
    /// </summary>
    private static void DropSeededRuntimeLayoutIfPatchReplacedRuntimeFiles(string baseDir, PatchManifest manifest)
    {
        string layoutDirectory = SharedRuntimeLayout.GetLayoutDirectory(baseDir);
        if (!Directory.Exists(layoutDirectory)) return;

        // 过渡补丁自己就投递整份布局（老装机没有 runtime\），不能因为"它带了运行时载荷"就把布局丢掉。
        bool patchDeliversRuntimeLayout = false;
        var deployedAppPaths = new List<string>(manifest.Files.Count);
        foreach (PatchFile file in manifest.Files)
        {
            if (string.Equals(file.Root, PatchRootRuntime, StringComparison.OrdinalIgnoreCase))
            {
                patchDeliversRuntimeLayout = true;
                continue;
            }
            deployedAppPaths.Add(file.RelativePath);
        }

        if (!SharedRuntimeLayout.ShouldDropSeededRuntimeLayout(baseDir, patchDeliversRuntimeLayout, deployedAppPaths)) return;

        TryDeleteDirectory(layoutDirectory);
        WriteLog("这份补丁替换了运行时文件，已丢弃主程序落成的共享布局；下次启动会按新产物重新落成");
    }

    /// <summary>
    /// 清理旧文件后顺手收掉被清空的目录（只回收刚刚被删空的那些，遇到非空目录立刻停，
    /// 且绝不越过安装目录的 app\ / runtime\ 边界）。
    /// </summary>
    private static int PruneEmptyDirectories(PatchManifest manifest, string baseDir)
    {
        if (manifest.Deletes.Count == 0) return 0;

        int prunedDirectoryCount = 0;
        foreach (PatchDelete entry in manifest.Deletes)
        {
            string targetRoot = ResolvePatchTargetRoot(baseDir, entry.Root);
            string rootPrefix = Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string? directory = Path.GetDirectoryName(GetSafeChildPath(targetRoot, entry.RelativePath));
            while (!string.IsNullOrEmpty(directory)
                && directory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(directory).Any()) break;
                    Directory.Delete(directory);
                    prunedDirectoryCount++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break;
                }

                directory = Path.GetDirectoryName(directory);
            }
        }

        return prunedDirectoryCount;
    }

    private static PatchManifest ReadPatchManifest(string manifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("files", out JsonElement filesElement) || filesElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Patch manifest 缺少 files 列表");

        var files = new List<PatchFile>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement fileElement in filesElement.EnumerateArray())
        {
            string relativePath = NormalizeRelativePath(ReadString(fileElement, "path"));
            string sha256 = ReadString(fileElement, "sha256");
            long size = ReadInt64(fileElement, "size");
            string targetRoot = NormalizePatchRoot(
                fileElement.TryGetProperty("root", out JsonElement rootElement) ? rootElement.GetString() : null);
            if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(sha256))
                throw new InvalidOperationException("Patch manifest 文件条目不完整");
            // 同一相对路径可以分别出现在 app\ 与 runtime\，去重要按“位置 + 路径”来。
            if (!seenPaths.Add(targetRoot + "|" + relativePath))
                throw new InvalidOperationException($"Patch manifest 文件重复：{relativePath}");

            files.Add(new PatchFile(relativePath, sha256, size, targetRoot));
        }

        // 可选：清理列表（老版本留下、新版本已经不再发布的文件）。
        var deletes = new List<PatchDelete>();
        var seenDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("deletes", out JsonElement deletesElement))
        {
            // 字段存在却不是数组时直接报错：静默忽略会让一份写坏的清单悄悄跳过清理，
            // 而 files 走的是抛错路线，两者不该一个严一个松。
            if (deletesElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Patch manifest 的 deletes 不是数组");

            foreach (JsonElement deleteElement in deletesElement.EnumerateArray())
            {
                // 允许写成字符串（默认 app\）或对象（带 root），保持与 files 同一套语义。
                string rawPath = deleteElement.ValueKind == JsonValueKind.String
                    ? deleteElement.GetString() ?? ""
                    : ReadString(deleteElement, "path");
                string deleteRoot = deleteElement.ValueKind == JsonValueKind.Object
                    ? NormalizePatchRoot(
                        deleteElement.TryGetProperty("root", out JsonElement deleteRootElement)
                            ? deleteRootElement.GetString()
                            : null)
                    : NormalizePatchRoot(null);
                string relativePath = NormalizeRelativePath(rawPath);
                if (string.IsNullOrWhiteSpace(relativePath))
                    throw new InvalidOperationException("Patch manifest 清理条目缺少 path");
                if (!seenDeletes.Add(deleteRoot + "|" + relativePath))
                    throw new InvalidOperationException($"Patch manifest 清理条目重复：{relativePath}");

                deletes.Add(new PatchDelete(relativePath, deleteRoot));
            }
        }

        return new PatchManifest(
            ReadString(root, "type"),
            NormalizeVersion(ReadString(root, "patch_baseline_version")),
            NormalizeVersion(ReadString(root, "latest_version")),
            files,
            deletes);
    }

    private static void ValidatePatchManifest(PatchManifest manifest, UpdateDescriptor descriptor)
    {
        if (!string.Equals(manifest.Type, PatchPackageType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Patch manifest 类型不支持");

        // 同一个路径不能既写入又删除（脚本算出来的清理列表不该包含新版本仍发布的东西）。
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PatchFile file in manifest.Files)
            written.Add(file.Root + "|" + file.RelativePath);
        foreach (PatchDelete entry in manifest.Deletes)
        {
            if (!written.Add(entry.Root + "|" + entry.RelativePath))
                throw new InvalidOperationException($"Patch manifest 里同一个路径既写入又清理：{entry.RelativePath}");
        }

        if (!string.Equals(manifest.LatestVersion, descriptor.LatestVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Patch manifest 最新版本与更新描述不一致");

        if (!string.IsNullOrWhiteSpace(descriptor.PatchBaselineVersion) &&
            !string.Equals(manifest.PatchBaselineVersion, descriptor.PatchBaselineVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Patch manifest 基线版本与更新描述不一致");
        }

        // 载荷白名单（安装那一刻的纵深防御）：打包侧已经拦过一遍，但清单来自网络，
        // 放行一条就可能删掉用户自己换的 ffmpeg 或运行时种子。见 AppPatchPayloadPolicy。
        foreach (PatchFile file in manifest.Files)
        {
            string? violation = AppPatchPayloadPolicy.DescribeViolation(
                file.RelativePath,
                isDelete: false,
                root: file.Root);
            if (violation is not null)
                throw new InvalidOperationException("Patch 载荷不合规：" + violation);
        }

        foreach (PatchDelete entry in manifest.Deletes)
        {
            string? violation = AppPatchPayloadPolicy.DescribeViolation(
                entry.RelativePath,
                isDelete: true,
                root: entry.Root);
            if (violation is not null)
                throw new InvalidOperationException("Patch 载荷不合规：" + violation);
        }
    }

    private static void ValidatePatchDescriptor(UpdateDescriptor descriptor)
    {
        if (!IsPatchDescriptorUsable(descriptor))
            throw new InvalidOperationException("更新描述中的 Patch 信息不完整或不支持");
    }

    private static bool IsPatchDescriptorUsable(UpdateDescriptor descriptor)
    {
        return descriptor.PatchSupported &&
            string.Equals(descriptor.PatchPackage.Type, PatchPackageType, StringComparison.OrdinalIgnoreCase) &&
            (UpdateEndpointPolicy.IsSecureAbsoluteUrl(descriptor.PatchPackage.Url) ||
             UpdateEndpointPolicy.IsSecureAbsoluteUrl(descriptor.PatchPackage.GithubUrl) ||
             UpdateEndpointPolicy.IsSecureAbsoluteUrl(descriptor.PatchPackage.GiteeUrl)) &&
            !string.IsNullOrWhiteSpace(descriptor.PatchPackage.Sha256) &&
            !string.IsNullOrWhiteSpace(descriptor.LatestVersion);
    }

    private static void ValidatePatchFiles(string patchFilesDir, List<PatchFile> files)
    {
        if (files.Count == 0)
            throw new InvalidOperationException("Patch manifest 文件列表为空");

        foreach (PatchFile file in files)
        {
            string sourcePath = ResolvePatchSourcePath(patchFilesDir, file);
            if (!File.Exists(sourcePath))
                throw new InvalidOperationException($"Patch 文件不存在：{file.RelativePath}");

            FileInfo info = new(sourcePath);
            if (file.Size >= 0 && info.Length != file.Size)
                throw new InvalidOperationException($"Patch 文件大小校验失败：{file.RelativePath}");

            string hash = ComputeSha256(sourcePath);
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Patch 文件 SHA256 校验失败：{file.RelativePath}");
        }
    }

    private static void ValidateApprovedDrawableTargets(
        string baseDir,
        string patchFilesDir,
        List<PatchFile> files)
    {
        foreach (PatchFile file in files)
        {
            if (!string.Equals(file.Root, "app", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    file.RelativePath.Replace('/', '\\'),
                    AppPatchPayloadPolicy.LibVlcDrawablePluginRelativePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string targetPath = GetSafeChildPath(
                ResolvePatchTargetRoot(baseDir, file.Root),
                file.RelativePath);
            if (!File.Exists(targetPath))
                continue;
            string sourcePath = ResolvePatchSourcePath(patchFilesDir, file);
            if (!string.Equals(
                    ComputeSha256(sourcePath),
                    ComputeSha256(targetPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "目标电脑已有不同的 libdrawable_plugin.dll，无法安全覆盖；请改用完整安装包");
            }
        }
    }

    private static void ValidateDownloadedFileSize(string path, long expectedSize)
    {
        if (expectedSize <= 0)
            return;

        long actualSize = new FileInfo(path).Length;
        if (actualSize != expectedSize)
            throw new InvalidOperationException("Patch 包大小校验失败");
    }

    private static void ValidateInstalledApp(string baseDir, string expectedVersion)
    {
        string appPath = Path.Combine(baseDir, AppRelativePath);
        string dllPath = Path.Combine(baseDir, AppDllRelativePath);
        if (!File.Exists(appPath))
            throw new InvalidOperationException("更新后缺少主程序 exe");
        if (!File.Exists(dllPath))
            throw new InvalidOperationException("更新后缺少主程序 dll");

        string installedVersion = ReadInstalledAppVersion(baseDir);
        if (string.IsNullOrWhiteSpace(installedVersion) ||
            CompareVersions(installedVersion, expectedVersion) != 0)
        {
            throw new InvalidOperationException($"更新后版本校验失败：{installedVersion} != {expectedVersion}");
        }
    }

    private static void RollbackFiles(List<FileBackup> backups)
    {
        foreach (FileBackup backup in backups.AsEnumerable().Reverse())
        {
            try
            {
                if (backup.Existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup.TargetPath)!);
                    RemoveReadOnlyAttribute(backup.TargetPath);
                    File.Copy(backup.BackupPath, backup.TargetPath, overwrite: true);
                    File.SetAttributes(backup.TargetPath, backup.OriginalAttributes);
                }
                else
                {
                    DeleteFileIfExists(backup.TargetPath);
                }
            }
            catch
            {
            }
        }
    }

    private static int StartApp(string baseDir, string appPath, string[] args, string? dotNetRoot = null)
    {
        if (!File.Exists(appPath))
        {
            WriteLog("未找到主程序，无法启动：" + appPath);
            string missingReport = WriteStartupDiagnosticReport(
                baseDir,
                appPath,
                dotNetRoot,
                "未找到主程序",
                exitCode: null,
                TimeSpan.Zero);
            ShowMessage(
                _useChinese ? "启动失败" : "Startup failed",
                AppendDiagnosticHint(
                    _useChinese
                        ? $"未找到主程序：{AppRelativePath}\n\n请确认 app 文件夹与本启动程序放在同一目录"
                        : $"Application not found: {AppRelativePath}\n\nMake sure the app folder is next to this launcher.",
                    missingReport),
                ErrorIcon);
            OpenDiagnosticFolder(missingReport);
            return 2;
        }

        Process? process;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = appPath,
                WorkingDirectory = Path.GetDirectoryName(appPath) ?? baseDir,
                UseShellExecute = false
            };
            if (!string.IsNullOrWhiteSpace(dotNetRoot))
            {
                // 让 apphost 从安装目录里的 runtime\ 解析框架，不依赖机器上另外安装的 .NET。
                startInfo.Environment["DOTNET_ROOT"] = dotNetRoot;
            }
            ConfigureCrashDumps(startInfo);

            startInfo.ArgumentList.Add(LaunchedByRootLauncherOption);
            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            process = Process.Start(startInfo);
            if (process is null)
                throw new InvalidOperationException("Process.Start 没有返回进程");
            WriteLog("已启动主程序，等待主窗口出现");
        }
        catch (Exception ex)
        {
            WriteLog("启动主程序失败：" + ex);
            string startReport = WriteStartupDiagnosticReport(
                baseDir,
                appPath,
                dotNetRoot,
                "启动进程失败：" + ex.Message,
                exitCode: null,
                TimeSpan.Zero);
            ShowMessage(
                _useChinese ? "启动失败" : "Startup failed",
                AppendDiagnosticHint(
                    _useChinese
                        ? $"启动主程序失败：\n{ex.Message}"
                        : $"Failed to start the application:\n{ex.Message}",
                    startReport),
                ErrorIcon);
            OpenDiagnosticFolder(startReport);
            return 1;
        }

        var elapsed = Stopwatch.StartNew();
        StartupState state = WaitForStartupWindow(process, elapsed, out int? exitCode);
        TimeSpan startupElapsed = elapsed.Elapsed;
        try { process.Dispose(); } catch { }

        if (state == StartupState.Started)
        {
            WriteLog($"主程序已显示窗口：elapsedMs={startupElapsed.TotalMilliseconds:F0}");
            return 0;
        }

        if (state == StartupState.Exited && exitCode == 0)
        {
            // 正常退出通常是重复实例被激活或命令行走完；不要误报启动失败。
            WriteLog($"主程序在显示窗口前正常退出：elapsedMs={startupElapsed.TotalMilliseconds:F0}");
            return 0;
        }

        string reason = state == StartupState.Exited
            ? $"主程序启动后立即退出，退出码={FormatExitCode(exitCode)}"
            : $"主程序进程仍在运行，但 {StartupWindowTimeoutMs / 1000} 秒内没有出现主窗口";
        string reportPath = WriteStartupDiagnosticReport(
            baseDir,
            appPath,
            dotNetRoot,
            reason,
            exitCode,
            startupElapsed);
        WriteLog(reason + "；诊断文件=" + reportPath);
        ShowStartupFailureMessage(baseDir, state, reason, exitCode, reportPath);
        return 1;
    }

    private static void PrepareAppHostCetCompatibility(string appPath)
    {
        // Windows 10 会在 CET 兼容进程上开启影子栈，但部分版本缺少 .NET 10 要求的
        // special user-mode APC，导致 CLR 在任何应用代码运行前以 0x80131506 退出。
        if (!OperatingSystem.IsWindows()
            || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            || !File.Exists(appPath))
        {
            return;
        }

        if (!PortableExecutableCetPolicy.TryDisableCetCompatibility(appPath, out bool changed, out string? error))
        {
            WriteLog("处理主程序 CET 兼容标记失败：" + error);
            return;
        }

        if (changed)
            WriteLog("当前 Windows 10 不完整支持 CET 影子栈；已关闭 apphost 的 CET 兼容标记后启动");
    }

    private enum StartupState
    {
        Started,
        Exited,
        Timeout
    }

    private static StartupState WaitForStartupWindow(
        Process process,
        Stopwatch elapsed,
        out int? exitCode)
    {
        exitCode = null;
        while (elapsed.ElapsedMilliseconds < StartupWindowTimeoutMs)
        {
            try
            {
                if (process.HasExited)
                {
                    exitCode = process.ExitCode;
                    return StartupState.Exited;
                }

                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                    return StartupState.Started;
            }
            catch (InvalidOperationException)
            {
                // 进程在查询期间退出，交给下面的最终状态判断。
                break;
            }

            Thread.Sleep(StartupPollIntervalMs);
        }

        try
        {
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
                return StartupState.Exited;
            }
        }
        catch
        {
        }

        return StartupState.Timeout;
    }

    private static string FormatExitCode(int? exitCode) =>
        exitCode.HasValue
            ? $"0x{exitCode.Value:X8} ({exitCode.Value})"
            : "unknown";

    private static string DescribeExitCode(int? exitCode) => exitCode switch
    {
        unchecked((int)0xC0000135) => "缺少 DLL 依赖或依赖无法加载",
        unchecked((int)0xC000007B) => "映像格式不正确（常见于 32/64 位不匹配或 DLL 损坏）",
        unchecked((int)0xC0000142) => "DLL 初始化失败",
        unchecked((int)0xE0434352) => ".NET 运行时异常（应用启动阶段抛出）",
        unchecked((int)0x80131506) => "CLR 执行引擎初始化失败（常见于 Windows 10 CET 影子栈兼容问题）",
        _ => ""
    };

    private static string WriteStartupDiagnosticReport(
        string baseDir,
        string appPath,
        string? dotNetRoot,
        string reason,
        int? exitCode,
        TimeSpan elapsed)
    {
        try
        {
            string directory;
            try
            {
                directory = Path.Combine(GetUserDataDir(), "log");
                Directory.CreateDirectory(directory);
            }
            catch
            {
                directory = Path.Combine(
                    Path.GetTempPath(),
                    BrandMetadata.ArtifactPrefix + "-diagnostics");
                Directory.CreateDirectory(directory);
            }

            var text = new StringBuilder();
        text.AppendLine(BrandMetadata.DisplayName + " 启动诊断");
            text.AppendLine("生成时间: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            text.AppendLine("启动器版本: " + (typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"));
            text.AppendLine("主程序版本: " + ReadInstalledAppVersion(baseDir));
            text.AppendLine("安装目录: " + baseDir);
            text.AppendLine("主程序路径: " + appPath);
            text.AppendLine("结果: " + reason);
            text.AppendLine("退出码: " + FormatExitCode(exitCode));
            string exitDescription = DescribeExitCode(exitCode);
            if (exitDescription.Length > 0)
                text.AppendLine("退出码说明: " + exitDescription);
            text.AppendLine($"启动耗时: {elapsed.TotalMilliseconds:F0} ms");
            text.AppendLine("系统: " + Environment.OSVersion.VersionString);
            text.AppendLine("系统版本号: " + Environment.OSVersion.Version);
            text.AppendLine("系统描述: " + RuntimeInformation.OSDescription);
            text.AppendLine("系统架构: " + RuntimeInformation.OSArchitecture);
            text.AppendLine("进程架构: " + RuntimeInformation.ProcessArchitecture);
            text.AppendLine("64 位系统: " + Environment.Is64BitOperatingSystem);
            text.AppendLine("64 位启动器进程: " + Environment.Is64BitProcess);
            text.AppendLine("DOTNET_ROOT: " + (dotNetRoot ?? "(未使用共享运行时布局)"));
            text.AppendLine("启动器运行时: " + Environment.Version);
            text.AppendLine();

            Version osVersion = Environment.OSVersion.Version;
            if (!Environment.Is64BitOperatingSystem)
            {
                text.AppendLine("警告: 当前是 32 位 Windows，本程序只提供 x64 版本。");
            }
            if (osVersion.Major < 10 || osVersion.Build < 14393)
            {
                text.AppendLine("警告: 当前 Windows 低于 .NET 10 官方支持的 Windows 10 1607；这可能是启动失败的原因，但不是唯一可能。");
            }
            else if (osVersion.Build < 19041)
            {
                text.AppendLine("提示: 当前 Windows build 低于 19041（Windows 10 2004），与安装包/商店清单的最低版本不同，建议一并核对。");
            }
            text.AppendLine();

            text.AppendLine("关键文件:");
            foreach (string relativePath in new[]
                     {
                         AppRelativePath,
                         AppDllRelativePath,
                         "app\\ExpressPackingMonitoring.deps.json",
                         "app\\ExpressPackingMonitoring.runtimeconfig.json",
                         "app\\coreclr.dll",
                         "app\\hostfxr.dll",
                         "app\\hostpolicy.dll",
                         "runtime\\dotnet.exe",
                         "runtime\\layout.json",
                         "app\\runtime-seed\\layout.json"
                     })
            {
                text.AppendLine("  " + DescribeFile(Path.Combine(baseDir, relativePath)));
            }
            text.AppendLine();

            text.AppendLine("VC++ / UCRT 运行库:");
            string systemDirectory = Environment.SystemDirectory;
            foreach (string name in VcRuntimeFileNames)
            {
                text.AppendLine("  " + DescribeFile(Path.Combine(systemDirectory, name)));
            }
            text.AppendLine("  VC++ 2015-2022 x64 下载: " + VcRuntimeDownloadUrl);
            text.AppendLine();

            text.AppendLine("原生依赖加载检查:");
            foreach (string relativePath in NativeLibraryRelativePaths)
            {
                text.AppendLine("  " + DescribeNativeLibrary(Path.Combine(baseDir, relativePath)));
            }
            text.AppendLine();

            text.AppendLine("运行日志尾部:");
            AppendLogTail(text, Path.Combine(GetUserDataDir(), "log", "runtime.log"));
            text.AppendLine();
            text.AppendLine("崩溃转储:");
            AppendCrashDumpList(text);
            text.AppendLine();
            text.AppendLine("启动器日志尾部:");
            AppendLogTail(text, GetLogPath());

            string path = Path.Combine(
                directory,
                $"startup-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.txt");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex)
        {
            WriteLog("写启动诊断文件失败：" + ex.Message);
            return "";
        }
    }

    private static void AppendCrashDumpList(StringBuilder text)
    {
        string dumpDirectory = Path.Combine(GetUserDataDir(), CrashDumpDirectoryName);
        if (!Directory.Exists(dumpDirectory))
        {
            text.AppendLine("  (无)");
            return;
        }

        FileInfo[] dumps = new DirectoryInfo(dumpDirectory)
            .GetFiles("crash_*.dmp")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        if (dumps.Length == 0)
        {
            text.AppendLine("  (无)");
            return;
        }

        foreach (FileInfo dump in dumps)
        {
            text.AppendLine(
                $"  {dump.FullName}（{dump.Length} bytes，{dump.LastWriteTime:yyyy-MM-dd HH:mm:ss}）");
        }
    }

    private static string DescribeFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists
                ? $"{path}（{file.Length} bytes）"
                : $"{path}（缺失）";
        }
        catch (Exception ex)
        {
            return $"{path}（读取失败：{ex.Message}）";
        }
    }

    private static string DescribeNativeLibrary(string path)
    {
        if (!File.Exists(path))
            return $"{path}（缺失）";

        if (!TryLoadNativeLibrary(path, out int error))
            return $"{path}（加载失败，Win32={error}: {new Win32Exception(error).Message}）";

        return $"{path}（加载成功）";
    }

    private static bool TryLoadNativeLibrary(string path, out int error)
    {
        error = 0;
        IntPtr module = LoadLibraryExW(path, IntPtr.Zero, 0x00000008);
        if (module == IntPtr.Zero)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        FreeLibrary(module);
        return true;
    }

    private static void AppendLogTail(StringBuilder text, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                text.AppendLine("  " + path + "（未生成）");
                return;
            }

            text.AppendLine("  " + path);
            foreach (string line in File.ReadLines(path).TakeLast(80))
                text.AppendLine("    " + line);
        }
        catch (Exception ex)
        {
            text.AppendLine("  " + path + "（读取失败：" + ex.Message + "）");
        }
    }

    private static void ShowStartupFailureMessage(
        string baseDir,
        StartupState state,
        string reason,
        int? exitCode,
        string reportPath)
    {
        bool offerVcRuntimeDownload = ShouldOfferVcRuntimeDownload(baseDir, exitCode);
        bool startupTimedOut = state == StartupState.Timeout && !exitCode.HasValue;
        var text = new StringBuilder();
        text.AppendLine(startupTimedOut
            ? (_useChinese
                ? "主程序仍在启动，但超过预期时间还没有显示窗口。"
                : "The application is still starting, but its window did not appear in time.")
            : (_useChinese
                ? "主程序没有成功打开。"
                : "The application did not open successfully."));
        text.AppendLine(reason);
        if (exitCode.HasValue)
        {
            text.AppendLine((_useChinese ? "退出码：" : "Exit code: ") + FormatExitCode(exitCode));
            string exitDescription = DescribeExitCode(exitCode);
            if (exitDescription.Length > 0)
                text.AppendLine((_useChinese ? "说明：" : "Meaning: ") + exitDescription);
        }
        text.AppendLine(
            (_useChinese ? "系统：" : "System: ")
            + Environment.OSVersion.VersionString
            + " / "
            + RuntimeInformation.OSArchitecture
            + (_useChinese ? " / 64 位： " : " / 64-bit: ")
            + Environment.Is64BitOperatingSystem);

        if (!Environment.Is64BitOperatingSystem)
        {
            text.AppendLine(_useChinese
                ? "当前是 32 位 Windows，本程序只提供 x64 版本。"
                : "This is 32-bit Windows; this application is x64 only.");
        }
        else if (Environment.OSVersion.Version.Major < 10
            || Environment.OSVersion.Version.Build < 14393)
        {
            text.AppendLine(_useChinese
                ? "当前系统低于 .NET 10 官方支持的 Windows 10 1607，请升级系统或使用旧版本。"
                : "This Windows version is below .NET 10's supported Windows 10 1607.");
        }

        if (offerVcRuntimeDownload)
        {
            text.AppendLine();
            text.AppendLine(_useChinese
                ? "检测到明确的 DLL 加载问题。请先安装 Microsoft Visual C++ 2015-2022 x64 运行库后重试。"
                : "A DLL loading problem was detected. Install Microsoft Visual C++ 2015-2022 x64 Runtime and try again.");
        }
        text.AppendLine(AppendDiagnosticHint("", reportPath).Trim());

        string content = text.ToString().Trim();
        string title = startupTimedOut
            ? (_useChinese ? "启动较慢" : "Startup is taking longer than expected")
            : (_useChinese ? "启动失败" : "Startup failed");
        if (offerVcRuntimeDownload)
        {
            bool openDownload = ShowQuestion(
                title,
                content
                + Environment.NewLine
                + Environment.NewLine
                + (_useChinese
                    ? "是否现在打开 Microsoft Visual C++ 2015-2022 x64 运行库下载页？"
                    : "Open the Microsoft Visual C++ 2015-2022 x64 Runtime download page now?"),
                ErrorIcon);
            if (openDownload)
                OpenUrl(VcRuntimeDownloadUrl);
        }
        else
        {
            ShowMessage(
                title,
                content,
                startupTimedOut ? InfoIcon : ErrorIcon);
        }

        OpenDiagnosticFolder(reportPath);
    }

    private static bool ShouldOfferVcRuntimeDownload(string baseDir, int? exitCode)
    {
        // 进程仍存活时只是窗口尚未出现，不能根据系统目录里个别 DLL 缺失推断运行库故障。
        // 应用在 app\ 中自带 VC++ 运行库，且 .NET/原生库可能已经成功加载。
        if (!exitCode.HasValue)
            return false;

        if (exitCode is int code
            && (code == unchecked((int)0xC0000135)
                || code == unchecked((int)0xC000007B)))
        {
            return true;
        }

        string systemDirectory = Environment.SystemDirectory;
        foreach (string name in VcRuntimeFileNames)
        {
            if (!File.Exists(Path.Combine(systemDirectory, name)))
                return true;
        }

        foreach (string relativePath in NativeLibraryRelativePaths)
        {
            string path = Path.Combine(baseDir, relativePath);
            if (File.Exists(path) && !TryLoadNativeLibrary(path, out _))
                return true;
        }

        return false;
    }

    private static bool ShowQuestion(string title, string message, uint icon) =>
        MessageBoxW(IntPtr.Zero, message, title, YesNoButtons | icon) == 6;

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            WriteLog("打开下载链接失败：" + ex.Message);
        }
    }

    private static void ConfigureCrashDumps(ProcessStartInfo startInfo)
    {
        try
        {
            string dumpDirectory = Path.Combine(GetUserDataDir(), CrashDumpDirectoryName);
            Directory.CreateDirectory(dumpDirectory);
            PruneCrashDumps(dumpDirectory);
            startInfo.Environment["DOTNET_DbgEnableMiniDump"] = "1";
            startInfo.Environment["DOTNET_DbgMiniDumpType"] = "2";
            startInfo.Environment["DOTNET_DbgMiniDumpName"] =
                Path.Combine(dumpDirectory, "crash_%p.dmp");
            WriteLog("崩溃转储已启用：" + dumpDirectory);
        }
        catch (Exception ex)
        {
            WriteLog("配置崩溃转储失败：" + ex.Message);
        }
    }

    private static void PruneCrashDumps(string dumpDirectory)
    {
        try
        {
            foreach (FileInfo dump in new DirectoryInfo(dumpDirectory)
                         .GetFiles("crash_*.dmp")
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Skip(10))
            {
                dump.Delete();
            }
        }
        catch (Exception ex)
        {
            WriteLog("清理旧崩溃转储失败：" + ex.Message);
        }
    }

    private static string AppendDiagnosticHint(string message, string reportPath)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
            return message;

        return message
            + Environment.NewLine
            + Environment.NewLine
            + (_useChinese ? "诊断文件：" : "Diagnostic file:")
            + Environment.NewLine
            + reportPath
            + Environment.NewLine
            + (_useChinese
                ? "请把诊断文件和 launcher_update.log 一起发给技术支持。关闭提示后会自动打开日志文件夹。"
                : "Send the diagnostic file and launcher_update.log to support. The log folder will open after you close this message.");
    }

    private static void OpenDiagnosticFolder(string reportPath)
    {
        try
        {
            string directory = string.IsNullOrWhiteSpace(reportPath)
                ? Path.Combine(GetUserDataDir(), "log")
                : Path.GetDirectoryName(reportPath) ?? "";
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{directory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            WriteLog("打开日志文件夹失败：" + ex.Message);
        }
    }

    private static bool IsAppRunning(string appPath)
    {
        string expectedPath = Path.GetFullPath(appPath);
        foreach (Process process in Process.GetProcessesByName("ExpressPackingMonitoring"))
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                    continue;

                string? processPath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(processPath) &&
                    string.Equals(Path.GetFullPath(processPath), expectedPath, StringComparison.OrdinalIgnoreCase))
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

    private static bool TryActivateExistingApp(string[] args)
    {
        string requestedRole = ResolveRequestedRole(args);
        if (IsKnownRole(requestedRole) &&
            IsRoleRunning(requestedRole) &&
            RequestActivate(requestedRole))
        {
            WriteLog("已唤醒正在运行的主程序：" + requestedRole);
            return true;
        }

        string configuredRole = ReadConfiguredWorkstationRole();
        if (IsKnownRole(configuredRole) &&
            IsRoleRunning(configuredRole) &&
            RequestActivate(configuredRole))
        {
            WriteLog("已唤醒正在运行的主程序：" + configuredRole);
            return true;
        }

        foreach (string role in new[] { CameraMonitorRole, PrintStationRole })
        {
            if (IsRoleRunning(role) && RequestActivate(role))
            {
                WriteLog("已唤醒正在运行的主程序：" + role);
                return true;
            }
        }

        WriteLog("未能唤醒正在运行的主程序，尝试重新启动");
        return false;
    }

    private static string ResolveRequestedRole(string[] args)
    {
        string temporaryRole = ResolveRoleOption(args, "--temporary-role");
        if (IsKnownRole(temporaryRole))
            return temporaryRole;

        string requestedRole = ResolveRoleOption(args, "--role");
        if (IsKnownRole(requestedRole))
            return requestedRole;

        if (args.Any(a => string.Equals(a, "--monitor", StringComparison.OrdinalIgnoreCase)))
            return CameraMonitorRole;

        if (args.Any(a => string.Equals(a, "--order-workstation", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(a, "--print-station", StringComparison.OrdinalIgnoreCase)))
        {
            return PrintStationRole;
        }

        return "";
    }

    private static string ResolveRoleOption(string[] args, string optionName)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i] ?? "";
            if (arg.StartsWith(optionName + "=", StringComparison.OrdinalIgnoreCase))
                return NormalizeRoleName(arg[(optionName.Length + 1)..]);
            if (string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return NormalizeRoleName(args[i + 1]);
        }

        return "";
    }

    private static string ReadConfiguredWorkstationRole()
    {
        string path = Path.Combine(GetUserDataDir(), "config.json");
        if (!File.Exists(path))
            return "";

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            return NormalizeRoleName(ReadString(document.RootElement, "WorkstationRole"));
        }
        catch (Exception ex)
        {
            WriteLog("读取工位配置失败：" + ex.Message);
            return "";
        }
    }

    private static bool IsRoleRunning(string role)
    {
        if (!IsKnownRole(role))
            return false;

        try
        {
            using var existing = Mutex.OpenExisting(GetRoleMutexName(role));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool RequestActivate(string role)
    {
        if (!IsKnownRole(role))
            return false;

        try
        {
            using var pipe = new NamedPipeClientStream(".", GetRolePipeName(role), PipeDirection.Out);
            pipe.Connect(400);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine("activate");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static UpdateDescriptor ReadUpdateDescriptor(JsonElement manifestRoot, string fallbackVersion)
    {
        string latestVersion = NormalizeVersion(ReadString(manifestRoot, "latest_version"));
        if (string.IsNullOrWhiteSpace(latestVersion))
            latestVersion = fallbackVersion;

        string fullDownloadPage = ReadString(manifestRoot, "full_download_page");
        if (string.IsNullOrWhiteSpace(fullDownloadPage))
            fullDownloadPage = ReadString(manifestRoot, "release_page");
        string fullDownloadFallbackPage = ReadString(manifestRoot, "full_download_fallback_page");
        string downloadSource = ReadString(manifestRoot, "download_source").Trim().ToLowerInvariant();
        if (downloadSource is not ("gitee" or "github" or "auto"))
            downloadSource = "gitee";

        return new UpdateDescriptor(
            latestVersion,
            ReadString(manifestRoot, "title"),
            ReadNotes(manifestRoot),
            fullDownloadPage,
            fullDownloadFallbackPage,
            NormalizeVersion(ReadString(manifestRoot, "patch_baseline_version")),
            ReadBoolean(manifestRoot, "patch_supported"),
            downloadSource,
            ReadPatchPackageInfo(manifestRoot));
    }

    private static PatchPackageInfo ReadPatchPackageInfo(JsonElement manifestRoot)
    {
        if (!manifestRoot.TryGetProperty("patch_package", out JsonElement package) || package.ValueKind != JsonValueKind.Object)
            return new PatchPackageInfo("", "", "", -1, "", "", 0);

        return new PatchPackageInfo(
            ReadString(package, "type"),
            ReadString(package, "url"),
            ReadString(package, "sha256"),
            ReadInt64(package, "size"),
            ReadString(package, "github_url"),
            ReadString(package, "gitee_url"),
            Math.Max(0, ReadInt32(package, "required_launcher_protocol")));
    }

    private static string[] ReadNotes(JsonElement manifestRoot)
    {
        if (!manifestRoot.TryGetProperty("notes", out JsonElement notes) || notes.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return notes
            .EnumerateArray()
            .Where(note => note.ValueKind == JsonValueKind.String)
            .Select(note => note.GetString() ?? "")
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .ToArray();
    }

    private static UpdateNotification BuildManualUpdateNotification(UpdateDescriptor descriptor, ManualUpdateReason reason)
    {
        string reasonText = _useChinese ? reason switch
        {
            ManualUpdateReason.PatchNotSupported => "本版本未提供可用的主程序增量包，需要下载完整包后解压覆盖安装",
            ManualUpdateReason.VersionBelowBaseline => "当前版本过旧，不能直接使用本次增量更新，需要下载完整包后解压覆盖安装",
            ManualUpdateReason.PatchDescriptorUnavailable => "本版本需要完整更新，需要下载完整包后解压覆盖安装",
            ManualUpdateReason.LauncherUpdateRequired =>
                "这份增量包需要新版启动器才能安装。请完全退出本软件后重新从快捷方式启动，让主程序先把启动器更新掉，再更新主程序",
            _ => "本版本需要完整更新，需要下载完整包后解压覆盖安装"
        } : reason switch
        {
            ManualUpdateReason.PatchNotSupported => "No compatible main-app patch is available for this release. Download and extract the full package.",
            ManualUpdateReason.VersionBelowBaseline => "The installed version is too old for this patch. Download and extract the full package.",
            ManualUpdateReason.LauncherUpdateRequired =>
                "This patch needs a newer launcher. Fully exit and start the app again so the main program updates the launcher first.",
            _ => "This release requires a full update. Download and extract the full package."
        };

        string message = _useChinese
            ? $"新版本：v{descriptor.LatestVersion}\n{reasonText}"
            : $"New version: v{descriptor.LatestVersion}\n{reasonText}";
        if (!string.IsNullOrWhiteSpace(descriptor.FullDownloadPage))
            message += _useChinese ? $"\n完整包下载页：{descriptor.FullDownloadPage}" : $"\nFull package: {descriptor.FullDownloadPage}";
        if (!string.IsNullOrWhiteSpace(descriptor.FullDownloadFallbackPage)
            && !AreSameUrl(descriptor.FullDownloadPage, descriptor.FullDownloadFallbackPage))
        {
            message += _useChinese
                ? $"\n备用下载页：{descriptor.FullDownloadFallbackPage}"
                : $"\nBackup download: {descriptor.FullDownloadFallbackPage}";
        }

        return new UpdateNotification(_useChinese ? "需要完整更新" : "Full update required", message, true);
    }

    private static string BuildSuccessMessage(UpdateDescriptor descriptor)
    {
        var lines = new List<string>
        {
            _useChinese ? $"已升级到 v{descriptor.LatestVersion}" : $"Upgraded to v{descriptor.LatestVersion}"
        };

        if (!string.IsNullOrWhiteSpace(descriptor.Title))
        {
            lines.Add("");
            lines.Add(descriptor.Title);
        }

        if (descriptor.Notes.Length > 0)
        {
            lines.Add("");
            lines.AddRange(descriptor.Notes.Select(note => "- " + note));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildFailedMessage(UpdateDescriptor descriptor)
    {
        string message = _useChinese
            ? $"已恢复旧版本\n新版本：v{descriptor.LatestVersion}\n请下载完整包后解压覆盖安装"
            : $"The previous version was restored\nNew version: v{descriptor.LatestVersion}\nDownload and extract the full package";
        if (!string.IsNullOrWhiteSpace(descriptor.FullDownloadPage))
            message += _useChinese ? $"\n完整包下载页：{descriptor.FullDownloadPage}" : $"\nFull package: {descriptor.FullDownloadPage}";
        if (!string.IsNullOrWhiteSpace(descriptor.FullDownloadFallbackPage)
            && !AreSameUrl(descriptor.FullDownloadPage, descriptor.FullDownloadFallbackPage))
        {
            message += _useChinese
                ? $"\n备用下载页：{descriptor.FullDownloadFallbackPage}"
                : $"\nBackup download: {descriptor.FullDownloadFallbackPage}";
        }

        return message;
    }

    /// <summary>自动检查更新默认开；解析口径与"接收预览版更新"共用 UpdateChannelPolicy。</summary>
    private static bool ReadAutoCheckEnabled() =>
        UpdateChannelPolicy.ReadAppConfigToggle(
            Path.Combine(GetUserDataDir(), "config.json"),
            "EnableAutoCheckUpdate",
            fallback: true,
            log: message => WriteLog("读取自动更新配置失败：" + message));

    private static string ReadInstalledAppVersion(string baseDir)
    {
        string dllPath = Path.Combine(baseDir, AppDllRelativePath);
        if (!File.Exists(dllPath))
            return "";

        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(dllPath);
            string version = info.ProductVersion ?? "";
            return NormalizeVersion(version);
        }
        catch (Exception ex)
        {
            WriteLog("读取当前版本失败：" + ex.Message);
            return "";
        }
    }

    private static IReadOnlyList<string> GetUpdateCheckUrls(string baseDir)
    {
        string primary = GetConfiguredUpdateUrl(baseDir, UpdateUrlKey);
        string fallback = GetConfiguredUpdateUrl(baseDir, UpdateFallbackUrlKey);
        if (primary.Length == 0)
            primary = GetEmbeddedDefaultCheckUrl();
        return UpdateEndpointPolicy.ResolveCheckUrls(primary, fallback);
    }

    private static string GetConfiguredUpdateUrl(string baseDir, string expectedKey)
    {
        string? value = Environment.GetEnvironmentVariable(expectedKey);
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();

        foreach (string path in new[] { Path.Combine(baseDir, ".env"), Path.Combine(Environment.CurrentDirectory, ".env") })
        {
            if (!File.Exists(path))
                continue;

            foreach (string rawLine in File.ReadLines(path, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                int separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                string key = line[..separator].Trim();
                if (string.Equals(key, expectedKey, StringComparison.OrdinalIgnoreCase))
                    return line[(separator + 1)..].Trim().Trim('"', '\'');
            }
        }

        return "";
    }

    private static string GetEmbeddedDefaultCheckUrl()
    {
        try
        {
            foreach (AssemblyMetadataAttribute metadata in Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (string.Equals(metadata.Key, DefaultCheckUrlMetadataKey, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(metadata.Value))
                {
                    return metadata.Value.Trim();
                }
            }
        }
        catch
        {
        }

        return UpdateEndpointPolicy.DefaultGiteeCheckUrl;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            return "";

        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            return false;

        return value.ValueKind == JsonValueKind.True;
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            return 0;

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : 0;
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            return -1;

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result) ? result : -1;
    }

    private static int CompareVersions(string latest, string current)
    {
        Version latestVersion = ParseVersion(latest);
        Version currentVersion = ParseVersion(current);
        return latestVersion.CompareTo(currentVersion);
    }

    private static Version ParseVersion(string value)
    {
        string normalized = NormalizeVersion(value);
        int suffixIndex = normalized.IndexOfAny(new[] { '+', '-' });
        if (suffixIndex >= 0)
            normalized = normalized[..suffixIndex];

        int[] parts = normalized
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out int n) ? n : throw new FormatException($"版本号格式异常: {value}"))
            .ToArray();

        return parts.Length switch
        {
            1 => new Version(parts[0], 0, 0),
            2 => new Version(parts[0], parts[1], 0),
            >= 3 => new Version(parts[0], parts[1], parts[2]),
            _ => new Version(0, 0, 0)
        };
    }

    private static string NormalizeVersion(string value)
    {
        string normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[1..];

        int metadataIndex = normalized.IndexOf('+');
        return metadataIndex > 0 ? normalized[..metadataIndex] : normalized;
    }

    private static string EscapeJsonString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string NormalizeRelativePath(string path)
    {
        string normalized = path.Replace('/', '\\').Trim();
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized))
            throw new InvalidOperationException("Patch 文件路径非法");

        string[] parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part == "." || part == ".."))
            throw new InvalidOperationException("Patch 文件路径非法");

        return string.Join('\\', parts);
    }

    /// <summary>补丁条目的安装位置；缺省（旧清单）是 app\。</summary>
    private static string NormalizePatchRoot(string? value)
    {
        string normalized = (value ?? "").Trim().ToLowerInvariant();
        if (normalized.Length == 0) return PatchRootApp;
        if (normalized == PatchRootApp || normalized == PatchRootRuntime) return normalized;
        throw new InvalidOperationException($"Patch manifest 不支持的安装位置：{value}");
    }

    private static string ResolvePatchTargetRoot(string baseDir, string root) =>
        root == PatchRootRuntime ? Path.Combine(baseDir, PatchRootRuntime) : Path.Combine(baseDir, PatchRootApp);

    /// <summary>runtime 位置的条目放在 files\.runtime\ 下，避免与 app\ 里的同名文件冲突。</summary>
    private static string ResolvePatchSourcePath(string patchFilesDir, PatchFile file) =>
        file.Root == PatchRootRuntime
            ? GetSafeChildPath(Path.Combine(patchFilesDir, PatchRuntimeSourcePrefix), file.RelativePath)
            : GetSafeChildPath(patchFilesDir, file.RelativePath);

    private static string GetSafeChildPath(string root, string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        string rootFullPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(rootFullPath, normalized));
        if (!fullPath.StartsWith(rootFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Patch 文件路径越界");

        return fullPath;
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetUserDataDir()
    {
        string? overridePath = Environment.GetEnvironmentVariable("EPM_USER_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string root = string.IsNullOrWhiteSpace(localAppData) ? AppContext.BaseDirectory : localAppData;
        return Path.Combine(root, BrandMetadata.DataDirectoryName);
    }

    private static string GetUpdatesCacheDir()
    {
        return Path.Combine(GetUserDataDir(), "cache", "updates");
    }

    private static string GetPendingUpdateDir()
    {
        return Path.Combine(GetUpdatesCacheDir(), "pending");
    }

    private static bool HasPendingUpdate()
    {
        string pendingDir = GetPendingUpdateDir();
        return File.Exists(Path.Combine(pendingDir, "update_manifest.json")) &&
            !string.IsNullOrWhiteSpace(FindPendingPatchZip(pendingDir));
    }

    private static string GetUpdateBackupDir()
    {
        return Path.Combine(GetUserDataDir(), "backups", "launcher-update");
    }

    private static string GetLogPath()
    {
        return Path.Combine(GetUserDataDir(), "log", "launcher_update.log");
    }

    private static string GetPatchDownloadFailureStatePath()
    {
        return Path.Combine(GetUpdatesCacheDir(), "patch_download_failures.json");
    }

    private static string GetSuccessfulUpdateCheckStatePath()
    {
        return Path.Combine(GetUpdatesCacheDir(), "app-check-state.json");
    }

    private static bool ShouldSkipRecentSuccessfulUpdateCheck(
        string currentVersion,
        bool allowPrerelease)
    {
        string path = GetSuccessfulUpdateCheckStatePath();
        try
        {
            if (!File.Exists(path))
                return false;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            JsonElement root = document.RootElement;
            bool stateAllowPrerelease = root.TryGetProperty("allow_prerelease", out JsonElement flag)
                && flag.ValueKind == JsonValueKind.True;
            // 渠道参与判定：用户刚打开预览版开关时，不能拿上一次正式渠道的结论挡住检查。
            return UpdateCheckThrottle.ShouldSkip(
                ReadString(root, "current_version"),
                ReadString(root, "checked_at_utc"),
                currentVersion,
                stateAllowPrerelease,
                allowPrerelease,
                DateTimeOffset.UtcNow,
                TimeSpan.FromHours(SuccessfulUpdateCheckCacheHours));
        }
        catch (Exception ex)
        {
            WriteLog("读取更新检查缓存失败：" + ex.Message);
            return false;
        }
    }

    private static void SaveSuccessfulUpdateCheck(
        string currentVersion,
        string latestVersion,
        string sourceUrl,
        bool allowPrerelease)
    {
        try
        {
            string path = GetSuccessfulUpdateCheckStatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json =
                "{" +
                $"\"current_version\":\"{EscapeJsonString(NormalizeVersion(currentVersion))}\"," +
                $"\"latest_version\":\"{EscapeJsonString(NormalizeVersion(latestVersion))}\"," +
                $"\"source_url\":\"{EscapeJsonString(sourceUrl)}\"," +
                // 渠道要一起记：用户切换"接收预览版更新"之后，上一次的结论不能继续挡住检查
                $"\"allow_prerelease\":{(allowPrerelease ? "true" : "false")}," +
                $"\"checked_at_utc\":\"{DateTimeOffset.UtcNow:O}\"" +
                "}";
            File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            WriteLog("保存更新检查缓存失败：" + ex.Message);
        }
    }

    private static string FindPendingPatchZip(string pendingDir)
    {
        if (!Directory.Exists(pendingDir))
            return "";

        return new[]
            {
                BrandMetadata.ArtifactPrefix + "_AppPatch_v*.zip",
                "ExpressPackingMonitoring_AppPatch_v*.zip"
            }
            .SelectMany(pattern => Directory.EnumerateFiles(
                pendingDir,
                pattern,
                SearchOption.TopDirectoryOnly))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? "";
    }

    private static string GetPatchZipFileName(UpdateDescriptor descriptor)
    {
        foreach (string candidate in new[]
        {
            descriptor.PatchPackage.GithubUrl,
            descriptor.PatchPackage.GiteeUrl,
            descriptor.PatchPackage.Url
        })
        {
            try
            {
                string fileName = Path.GetFileName(new Uri(candidate).LocalPath);
                if (!string.IsNullOrWhiteSpace(fileName) &&
                    fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return fileName;
                }
            }
            catch
            {
            }
        }

        return $"{BrandMetadata.ArtifactPrefix}_AppPatch_v{descriptor.LatestVersion}.zip";
    }

    private static string BuildDefaultGithubPatchDownloadUrl(UpdateDescriptor descriptor, string patchZipName)
    {
        string tag = "v" + NormalizeVersion(descriptor.LatestVersion);
        return $"{DefaultPatchDownloadBaseUrl}/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(patchZipName)}";
    }

    private static PatchDownloadFailureState LoadPatchDownloadFailureState(string latestVersion)
    {
        string normalizedVersion = NormalizeVersion(latestVersion);
        string path = GetPatchDownloadFailureStatePath();
        try
        {
            if (File.Exists(path))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                JsonElement root = document.RootElement;
                string stateVersion = NormalizeVersion(ReadString(root, "latest_version"));
                int failures = ReadInt32(root, "consecutive_github_download_failures");
                if (string.Equals(stateVersion, normalizedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return new PatchDownloadFailureState
                    {
                        LatestVersion = normalizedVersion,
                        ConsecutiveGithubDownloadFailures = Math.Max(0, failures)
                    };
                }
            }
        }
        catch (Exception ex)
        {
            WriteLog("读取 Patch 下载失败计数失败：" + ex.Message);
        }

        return new PatchDownloadFailureState
        {
            LatestVersion = normalizedVersion,
            ConsecutiveGithubDownloadFailures = 0
        };
    }

    private static void SavePatchDownloadFailureState(PatchDownloadFailureState state)
    {
        try
        {
            string path = GetPatchDownloadFailureStatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string json = "{" +
                $"\"latest_version\":\"{EscapeJsonString(NormalizeVersion(state.LatestVersion))}\"," +
                $"\"consecutive_github_download_failures\":{Math.Max(0, state.ConsecutiveGithubDownloadFailures)}" +
                "}";
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            WriteLog("写入 Patch 下载失败计数失败：" + ex.Message);
        }
    }

    private static void ResetPatchDownloadFailureState()
    {
        TryDeleteFile(GetPatchDownloadFailureStatePath());
    }

    private static bool AreSameUrl(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownRole(string role)
    {
        return string.Equals(role, CameraMonitorRole, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, PrintStationRole, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoleName(string? role)
    {
        if (string.Equals(role, CameraMonitorRole, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "monitor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "camera", StringComparison.OrdinalIgnoreCase))
        {
            return CameraMonitorRole;
        }

        if (string.Equals(role, PrintStationRole, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "print", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "printer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "order", StringComparison.OrdinalIgnoreCase))
        {
            return PrintStationRole;
        }

        return "";
    }

    private static string NormalizeRole(string role)
    {
        return string.Equals(role, PrintStationRole, StringComparison.OrdinalIgnoreCase)
            ? PrintStationRole
            : CameraMonitorRole;
    }

    private static string GetRoleMutexName(string role)
    {
        return $@"Local\{InstanceNamePrefix}.{NormalizeRole(role)}.Mutex";
    }

    private static string GetRolePipeName(string role)
    {
        return $"{InstanceNamePrefix}.{NormalizeRole(role)}.Activate";
    }

    private static void SafeDeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            RemoveReadOnlyAttribute(path);
            File.Delete(path);
        }
    }

    private static void RemoveReadOnlyAttribute(string path)
    {
        if (!File.Exists(path))
            return;

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            SafeDeleteDirectory(path);
        }
        catch (Exception ex)
        {
            WriteLog("清理目录失败：" + path + Environment.NewLine + ex);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            DeleteFileIfExists(path);
        }
        catch (Exception ex)
        {
            WriteLog("清理文件失败：" + path + Environment.NewLine + ex);
        }
    }

    private static void WriteLog(string message)
    {
        try
        {
            string logPath = GetLogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(
                logPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static Thread ShowTimedMessageAsync(string title, string message, uint icon)
    {
        var thread = new Thread(() => ShowTimedMessage(title, message, icon));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = false;
        thread.Start();
        return thread;
    }

    private static void ShowTimedMessage(string title, string message, uint icon)
    {
        try
        {
            MessageBoxTimeoutW(IntPtr.Zero, message, title, icon, 0, DialogTimeoutMs);
        }
        catch
        {
            ShowMessage(title, message, icon);
        }
    }

    private static void ShowMessage(string title, string message, uint icon)
    {
        MessageBoxW(IntPtr.Zero, message, title, icon);
    }

    private static bool ResolveChineseLanguage()
    {
        try
        {
            string path = Path.Combine(GetUserDataDir(), "config.json");
            if (File.Exists(path))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                if (document.RootElement.TryGetProperty("Language", out JsonElement value))
                {
                    string? preference = value.GetString();
                    if (preference == "zh-Hans") return true;
                    if (preference == "en-US") return false;
                }
            }
        }
        catch { }

        // Primary language ID 0x04 represents the Chinese language family.
        return (GetUserDefaultUILanguage() & 0x03ff) == 0x04;
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(
        string fileName,
        IntPtr file,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [DllImport("user32.dll", EntryPoint = "MessageBoxTimeoutW", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxTimeoutW(
        IntPtr hWnd,
        string lpText,
        string lpCaption,
        uint uType,
        ushort wLanguageId,
        uint dwMilliseconds);

    private sealed record PatchPackageInfo(
        string Type,
        string Url,
        string Sha256,
        long Size,
        string GithubUrl,
        string GiteeUrl,
        int RequiredLauncherProtocol = 0);

    private sealed record UpdateDescriptor(
        string LatestVersion,
        string Title,
        string[] Notes,
        string FullDownloadPage,
        string FullDownloadFallbackPage,
        string PatchBaselineVersion,
        bool PatchSupported,
        string DownloadSource,
        PatchPackageInfo PatchPackage);

    private sealed record UpdateNotification(string Title, string Message, bool IsError);

    private sealed record PatchManifest(
        string Type,
        string PatchBaselineVersion,
        string LatestVersion,
        List<PatchFile> Files,
        List<PatchDelete> Deletes);

    private sealed record PatchFile(string RelativePath, string Sha256, long Size, string Root);

    /// <summary>
    /// 补丁要清理的旧文件：只允许落在安装目录的 app\ 或 runtime\ 内（GetSafeChildPath 兜底），
    /// 删除前先进备份、失败整体回滚。升级是累计覆盖、从不删除，不清理的话旧版本的运行时与 DLL
    /// 会在 app\ 里越攒越多。
    /// </summary>
    private sealed record PatchDelete(string RelativePath, string Root);

    private sealed record FileBackup(
        string TargetPath,
        string BackupPath,
        bool Existed,
        FileAttributes OriginalAttributes);

    /// <summary>
    /// pending 安装的结果：Deferred=true 表示另一个启动器正在安装更新、本次刻意不启动主程序
    /// （启动器直接退出，让用户等它装完再开）。
    /// </summary>
    private sealed record PendingInstallOutcome(UpdateNotification? Notification, bool Deferred);

    private enum ManualUpdateReason
    {
        PatchNotSupported,
        VersionBelowBaseline,
        PatchDescriptorUnavailable,
        LauncherUpdateRequired
    }

    private sealed class PatchDownloadFailureState
    {
        public string LatestVersion { get; set; } = "";
        public int ConsecutiveGithubDownloadFailures { get; set; }
    }
}
