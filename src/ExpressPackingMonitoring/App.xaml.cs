using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Services.Extensions;
using ExpressPackingMonitoring.Themes;
using ExpressPackingMonitoring.UpdateCore;
using ExpressPackingMonitoring.ViewModels;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ExpressPackingMonitoring
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static readonly bool SoftwareRenderingForced = ConfigureWpfRendering();
        private WorkstationInstanceCoordinator? _instanceCoordinator;
        private IDisposable? _hostLease;
        private CancellationTokenSource? _launcherUpdateCancellation;

        protected override void OnStartup(StartupEventArgs e)
        {
            long startupStartedAt = Stopwatch.GetTimestamp();
            void LogStartupStage(string stage)
            {
                RuntimeLog.Info(
                    "StartupTrace",
                    $"{stage} elapsedMs={Stopwatch.GetElapsedTime(startupStartedAt).TotalMilliseconds:F0}");
            }

            ApplyConfiguredWpfRenderingMode();
            WinRtPlacementService.Apply();
            base.OnStartup(e);
            if (WindowsLanAccessSetup.TryHandleCommandLine(e.Args, out int lanSetupExitCode))
            {
                RuntimeLog.Info("App", $"Windows LAN access setup handled, exitCode={lanSetupExitCode}");
                Shutdown(lanSetupExitCode);
                return;
            }

            if (!ApplicationRestart.WaitForRestartParentExit(e.Args, 15000, out string restartWaitError))
            {
                AppDialog.Error(null, restartWaitError, "切换用途失败");
                Shutdown(1);
                return;
            }

            if (UninstallCleanupService.TryHandleCommandLine(e.Args, out int uninstallExitCode))
            {
                Shutdown(uninstallExitCode);
                return;
            }

            if (RootLauncherStartupService.TryRedirectNormalStartup(e.Args))
            {
                Shutdown(0);
                return;
            }

            TaskbarIdentityService.TryApply();

            CameraBarcodeRuntimeOptions.Initialize(e.Args);
            var config = WorkstationConfigStore.Load();
            AppLanguage.Initialize(config.Language);
            WpfLocalization.EnableAutomaticWpfLocalization();
            WpfQrCode.UseWpfRenderer();
            ThemeManager.ApplyConfiguredTheme(config.Theme);
            RegisterRuntimeExceptionLogging();
            RuntimeLog.Info("App", "Application startup");
            RuntimeLog.LogSessionStart(e.Args);
            RuntimeLog.LogBuildInfo();
            _ = Task.Run(OpenCvNativeRuntimeDiagnostics.Log);
            SeedSharedRuntimeLayout();
            RuntimeLog.Info(
                "Rendering",
                $"WPF process render mode={RenderOptions.ProcessRenderMode}, softwareForced={SoftwareRenderingForced}");
            LogStartupStage("bootstrap");
            _launcherUpdateCancellation = new CancellationTokenSource();
            bool enableAutoCheckUpdate = config.EnableAutoCheckUpdate;
            _ = Task.Run(async () =>
            {
                try
                {
                    await new LauncherUpdateService().CheckAndApplyAsync(
                        enableAutoCheckUpdate,
                        _launcherUpdateCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("LauncherUpdate", "Startup launcher update check failed", ex);
                }
            });
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (AudioProbe.TryHandleCommandLine(e.Args, out int exitCode))
            {
                RuntimeLog.Info("App", $"AudioProbe command handled, exitCode={exitCode}");
                RuntimeLog.RecordShutdownRequest("AudioProbeCompleted", $"exitCode={exitCode}");
                Shutdown(exitCode);
                return;
            }

            if (!WorkstationInstanceCoordinator.TryCreate(
                    out _instanceCoordinator,
                    out InstanceStartConflict instanceConflict))
            {
                if (instanceConflict == InstanceStartConflict.LegacyVersionRunning)
                {
                    bool activatedLegacy = WorkstationInstanceCoordinator.RequestActivateLegacy();
                    RuntimeLog.RecordShutdownRequest(
                        activatedLegacy ? "LegacyInstanceActivated" : "LegacyInstanceBlocked");
                    if (!activatedLegacy)
                    {
                        AppDialog.Warning(
                            null,
                            AppLanguage.Get(
                                "检测到旧版本仍在运行。请先关闭旧版本，再打开新版本，避免两个进程同时写入同一份配置和录像数据库"),
                            AppLanguage.Get("请先关闭旧版本"));
                    }
                    Shutdown(0);
                    return;
                }

                WorkstationInstanceCoordinator.RequestActivate();
                RuntimeLog.RecordShutdownRequest("DuplicateInstanceActivated");
                Shutdown(0);
                return;
            }

            bool forceChoose = e.Args.Any(a =>
                string.Equals(a, "--choose-workstation", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "--choose-deployment", StringComparison.OrdinalIgnoreCase));
            string requestedPreset = ResolvePresetOption(e.Args, "--preset");
            if (requestedPreset.Length == 0)
                requestedPreset = ResolvePresetOption(e.Args, "--role");
            if (requestedPreset.Length == 0)
                requestedPreset = ResolveLegacyRequestedPreset(e.Args);

            if (forceChoose)
                config.DeploymentPreset = "";
            if (requestedPreset.Length > 0 && !TrySaveStartupPreset(config, requestedPreset))
                return;

            string startupPreset = config.DeploymentPreset;
            bool requiresDeploymentSetup = forceChoose || AppConfig.ShouldRunDeploymentSetup(config);
            while (true)
            {
                if (!DeploymentPresets.IsKnown(startupPreset) || requiresDeploymentSetup)
                {
                    var selector = new WorkstationSelectionWindow(startupPreset, confirmCurrentPreset: true);
                    if (selector.ShowDialog() != true || string.IsNullOrWhiteSpace(selector.SelectedPreset))
                    {
                        RuntimeLog.RecordShutdownRequest("DeploymentSelectionCancelled");
                        Shutdown(0);
                        return;
                    }

                    AppConfig draft = JsonSerializer.Deserialize<AppConfig>(
                        JsonSerializer.Serialize(config)) ?? new AppConfig();
                    draft.DeploymentPreset = selector.SelectedPreset;
                    if (selector.SelectedPreset == DeploymentPresets.RecordingWorkstation)
                    {
                        draft.RecordingWorkstationActivatedAtUtc = DateTime.UtcNow;
                        RecordingWorkstationCachePolicy.ConfigureInitialLocation(
                            draft,
                            preserveExistingLocation: config.FirstUseWizardCompleted);
                    }
                    draft.DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion;
                    draft.EnableWebServer = DeploymentCapabilities
                        .ForPreset(selector.SelectedPreset)
                        .CanRunWebServer;
                    draft.WorkstationRole = selector.SelectedPreset switch
                    {
                        DeploymentPresets.RecordingHost => WorkstationRoles.CameraMonitor,
                        DeploymentPresets.MobileBackupHost => WorkstationRoles.PrintStation,
                        _ => ""
                    };

                    AppConfig.NormalizeAfterLoad(draft);
                    config = draft;
                    startupPreset = draft.DeploymentPreset;
                    requiresDeploymentSetup = false;
                }

                if (!DeploymentPresets.IsKnown(startupPreset))
                {
                    RuntimeLog.RecordShutdownRequest("InvalidDeploymentPreset", startupPreset);
                    Shutdown(0);
                    return;
                }

                if ((string.Equals(
                         startupPreset,
                         DeploymentPresets.RecordingHost,
                         StringComparison.OrdinalIgnoreCase)
                     || string.Equals(
                         startupPreset,
                         DeploymentPresets.RecordingWorkstation,
                         StringComparison.OrdinalIgnoreCase))
                    && AppConfig.ShouldRunRecordingSetup(config))
                {
                    if (!FirstUseSetupWizardWindow.TryConfigureRecordingHost(
                            config,
                            owner: null,
                            out AppConfig configuredRecordingHost))
                    {
                        RuntimeLog.RecordShutdownRequest("RecordingHostSetupCancelled");
                        AppConfig.ResetDeploymentSetupForRetry(config);
                        if (!WorkstationConfigStore.TryUpdate(
                                current => { current.DeploymentSetupVersion = 0; },
                                out _,
                                out string resetError))
                        {
                            RuntimeLog.Warn("Setup", $"重置部署设置版本失败：{resetError}");
                        }

                        requiresDeploymentSetup = true;
                        continue;
                    }

                    if (!WorkstationConfigStore.TrySave(configuredRecordingHost, out string saveError))
                    {
                        AppDialog.Error(
                            null,
                            $"配置保存失败，程序无法安全启动。\n\n{saveError}",
                            "启动失败");
                        Shutdown(1);
                        return;
                    }

                    config = configuredRecordingHost;
                }

                break;
            }

            if (DeploymentCapabilities.ForPreset(startupPreset).IsHost)
            {
                string? hostLeaseScope = Environment.GetEnvironmentVariable("EPM_HOST_LEASE_SCOPE");
                if (!WorkstationInstanceCoordinator.TryAcquireHostLease(out _hostLease, hostLeaseScope))
                {
                    RuntimeLog.Info(
                        "Instance",
                        $"Host lease unavailable preset={startupPreset}; prompting for a non-host purpose");
                    var hostConflictSelector = new WorkstationSelectionWindow(
                        startupPreset,
                        confirmCurrentPreset: true,
                        hostUnavailable: true);
                    if (hostConflictSelector.ShowDialog() != true ||
                        string.IsNullOrWhiteSpace(hostConflictSelector.SelectedPreset) ||
                        DeploymentCapabilities.ForPreset(hostConflictSelector.SelectedPreset).IsHost)
                    {
                        RuntimeLog.RecordShutdownRequest("HostLeaseFallbackCancelled", startupPreset);
                        Shutdown(0);
                        return;
                    }

                    if (!TrySaveStartupPreset(config, hostConflictSelector.SelectedPreset))
                        return;
                    startupPreset = config.DeploymentPreset;
                    RuntimeLog.Info(
                        "Instance",
                        $"Host lease unavailable; continuing with non-host preset={startupPreset}");
                }
                else
                {
                    RuntimeLog.Info("Instance", $"Host lease acquired preset={startupPreset}");
                }
            }

            // 只有装在标准布局里的桌面端才按当前位置重写自启动项；开发/测试构建跑起来不许改用户注册表。
            if (!config.AutoStartOnBoot
                || AutoStartService.ShouldApplyOnStartup(AppContext.BaseDirectory, System.IO.File.Exists))
            {
                AutoStartService.Apply(config.AutoStartOnBoot);
            }
            // 扩展更新会换版本目录：把"把开机自启项改到新目录"的 Windows 实现挂给 Core
            // （Core 是跨平台工程，不能直接碰注册表）。
            ExtensionStartupRetarget.Hook = ExtensionStartupEntryRewriter.Retarget;
            bool allowLanAccessSetup = !IsLanAccessSetupDisabled();
            MainViewModel.AllowLanAccessSetupOnStartup = allowLanAccessSetup;

            LogStartupStage("before-window-create");
            Window window = DeploymentPresets.Normalize(startupPreset) switch
            {
                DeploymentPresets.ViewerClient => new ViewerClientWindow(config),
                DeploymentPresets.MobileBackupHost => new PrintWorkstationWindow(
                    config,
                    openPlaybackOnStartup: true,
                    requestLanAccessOnStartup: allowLanAccessSetup,
                    enableCloseBehaviorPrompt: true),
                _ => new MainWindow(enableCloseBehaviorPrompt: true)
            };
            LogStartupStage($"window-created type={window.GetType().Name}");
            window.SourceInitialized += (_, _) =>
            {
                if (PresentationSource.FromVisual(window) is HwndSource source)
                    source.AddHook(ShutdownWindowProc);
            };
            MainWindow = window;
            _instanceCoordinator?.StartActivationListener(window);
            window.Show();
            LogStartupStage("window-shown");
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        // 默认沿用 WPF 的硬件加速渲染；仅当显卡或虚拟显示驱动导致窗口全白/全黑时，
        // 才由高级设置（或同名环境变量）回退到软件渲染。进程级设置，改动后需重启生效。
        private static bool ConfigureWpfRendering()
        {
            bool forceSoftwareRendering = EarlyStartupConfig.IsSoftwareRenderingForced();
            ApplyWpfRenderingMode(forceSoftwareRendering);

            return forceSoftwareRendering;
        }

        private static void ApplyConfiguredWpfRenderingMode() =>
            ApplyWpfRenderingMode(SoftwareRenderingForced);

        /// <summary>
        /// 自包含产物把运行时平铺在 app\ 里，市场里的扩展要靠共享布局（安装根目录的 runtime\）
        /// 才能复用它。这里在启动早期落一次：有种子才动作，已经有布局就跳过，失败只记录不阻断启动。
        /// </summary>
        private static void SeedSharedRuntimeLayout()
        {
            if (PackagingIdentity.IsPackaged)
                return;

            try
            {
                string appDirectory = System.IO.Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
                string? installRoot = System.IO.Path.GetDirectoryName(appDirectory);
                if (string.IsNullOrEmpty(installRoot)) return;

                RuntimeLayoutSeedResult result = RuntimeLayoutSeeder.Ensure(installRoot, appDirectory);
                if (result.Materialized)
                {
                    RuntimeLog.Info(
                        "Runtime",
                        $"已从自包含产物落成共享运行时布局：硬链接 {result.LinkedFiles} 个，复制 {result.CopiedFiles} 个");
                }
                else if (result.Error is not null)
                {
                    RuntimeLog.Warn("Runtime", "落成共享运行时布局失败：" + result.Error);
                }
            }
            catch (Exception exception)
            {
                RuntimeLog.Warn("Runtime", "落成共享运行时布局异常：" + exception.Message);
            }
        }

        private static void ApplyWpfRenderingMode(bool forceSoftwareRendering)
        {
            if (forceSoftwareRendering)
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        private static bool IsLanAccessSetupDisabled()
        {
            string? value = Environment.GetEnvironmentVariable("EPM_DISABLE_LAN_ACCESS_SETUP");
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private bool TrySaveStartupPreset(AppConfig config, string preset)
        {
            if (WorkstationConfigStore.TryUpdate(
                    current =>
                    {
                        current.DeploymentPreset = preset;
                        if (preset == DeploymentPresets.RecordingWorkstation)
                        {
                            current.RecordingWorkstationActivatedAtUtc = DateTime.UtcNow;
                            RecordingWorkstationCachePolicy.ConfigureInitialLocation(
                                current,
                                preserveExistingLocation: current.FirstUseWizardCompleted);
                        }
                        current.DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion;
                        current.EnableWebServer = DeploymentCapabilities
                            .ForPreset(preset)
                            .CanRunWebServer;
                        current.WorkstationRole = preset switch
                        {
                            DeploymentPresets.RecordingHost => WorkstationRoles.CameraMonitor,
                            DeploymentPresets.MobileBackupHost => WorkstationRoles.PrintStation,
                            _ => ""
                        };
                    },
                    out AppConfig savedConfig,
                    out string error))
            {
                config.DeploymentPreset = savedConfig.DeploymentPreset;
                config.DeploymentSchemaVersion = savedConfig.DeploymentSchemaVersion;
                config.EnableWebServer = savedConfig.EnableWebServer;
                config.WorkstationRole = savedConfig.WorkstationRole;
                return true;
            }

            AppDialog.Error(
                null,
                $"配置保存失败，程序无法安全启动。\n\n请检查磁盘空间和配置目录权限。\n{error}",
                "启动失败");
            RuntimeLog.RecordShutdownRequest("StartupConfigSaveFailed", error);
            Shutdown(1);
            return false;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            (string source, string detail) = RuntimeLog.GetShutdownRequest();
            if (string.Equals(source, "not-recorded", StringComparison.Ordinal))
            {
                RuntimeLog.RecordShutdownRequest("ApplicationExitWithoutRecordedClose", $"shutdownMode={ShutdownMode}");
                (source, detail) = RuntimeLog.GetShutdownRequest();
            }
            RuntimeLog.Info("App", $"Session exit session={RuntimeLog.CurrentSessionId}, pid={Environment.ProcessId}, exitCode={e.ApplicationExitCode}, source={source}, detail={detail}");
            _launcherUpdateCancellation?.Cancel();
            _launcherUpdateCancellation?.Dispose();
            _launcherUpdateCancellation = null;
            _hostLease?.Dispose();
            _hostLease = null;
            _instanceCoordinator?.Dispose();
            _instanceCoordinator = null;
            WorkstationNetwork.TryStartPendingRestart();
            base.OnExit(e);
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            RuntimeLog.RecordShutdownRequest("WindowsSessionEnding", e.ReasonSessionEnding.ToString());
            base.OnSessionEnding(e);
        }

        internal static string ResolveLegacyRequestedPreset(string[] args)
        {
            if (args.Any(a => string.Equals(a, "--monitor", StringComparison.OrdinalIgnoreCase)))
                return DeploymentPresets.RecordingHost;
            if (args.Any(a => string.Equals(a, "--order-workstation", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(a, "--print-station", StringComparison.OrdinalIgnoreCase)))
                return DeploymentPresets.MobileBackupHost;
            if (args.Any(a => string.Equals(a, "--viewer", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(a, "--viewer-client", StringComparison.OrdinalIgnoreCase)))
                return DeploymentPresets.ViewerClient;
            return "";
        }

        internal static string ResolvePresetOption(string[] args, string optionName)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i] ?? "";
                if (arg.StartsWith(optionName + "=", StringComparison.OrdinalIgnoreCase))
                    return NormalizePresetName(arg[(optionName.Length + 1)..]);
                if (string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    return NormalizePresetName(args[i + 1]);
            }

            return "";
        }

        internal static string NormalizePresetName(string? role)
        {
            if (string.Equals(role, DeploymentPresets.RecordingHost, StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, WorkstationRoles.CameraMonitor, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "monitor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "camera", StringComparison.OrdinalIgnoreCase))
                return DeploymentPresets.RecordingHost;
            if (string.Equals(role, DeploymentPresets.MobileBackupHost, StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, WorkstationRoles.PrintStation, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "print", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "printer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "order", StringComparison.OrdinalIgnoreCase))
                return DeploymentPresets.MobileBackupHost;
            if (string.Equals(role, DeploymentPresets.ViewerClient, StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "viewer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "client", StringComparison.OrdinalIgnoreCase))
                return DeploymentPresets.ViewerClient;
            return "";
        }

        private static IntPtr ShutdownWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            string? shutdownSource = RuntimeLog.ClassifyShutdownWindowMessage(message, wParam);
            if (shutdownSource != null)
            {
                RuntimeLog.RecordShutdownRequest(
                    shutdownSource,
                    $"hwnd=0x{hwnd.ToInt64():X}, message=0x{message:X4}, wParam=0x{wParam.ToInt64():X}, lParam=0x{lParam.ToInt64():X}");
            }

            return IntPtr.Zero;
        }

        private static void RegisterRuntimeExceptionLogging()
        {
            Current.DispatcherUnhandledException += (_, e) =>
            {
                RuntimeLog.RecordShutdownRequest("DispatcherUnhandledException", e.Exception.GetType().FullName ?? e.Exception.GetType().Name);
                RuntimeLog.Error("Unhandled", "DispatcherUnhandledException", e.Exception);
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.IsTerminating)
                    RuntimeLog.RecordShutdownRequest("AppDomainUnhandledException", e.ExceptionObject?.GetType().FullName ?? "unknown");
                RuntimeLog.Error("Unhandled", $"AppDomain unhandled exception, terminating={e.IsTerminating}", e.ExceptionObject as Exception);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                RuntimeLog.Error("Unhandled", "UnobservedTaskException", e.Exception);
                e.SetObserved();
            };
        }
    }
}
