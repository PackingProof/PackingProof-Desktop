using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Themes;
using ExpressPackingMonitoring.ViewModels;
using System;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace ExpressPackingMonitoring
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private WorkstationInstanceCoordinator? _instanceCoordinator;
        private CancellationTokenSource? _launcherUpdateCancellation;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (!WorkstationNetwork.WaitForRestartParentExit(e.Args, 15000, out string restartWaitError))
            {
                AppDialog.ShowMessage(
                    null,
                    restartWaitError,
                    "切换用途失败",
                    AppDialogSeverity.Error);
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
            AppLanguage.EnableAutomaticWpfLocalization();
            ThemeManager.ApplyConfiguredTheme(config.Theme);
            RegisterRuntimeExceptionLogging();
            RuntimeLog.Info("App", "Application startup");
            RuntimeLog.LogSessionStart(e.Args);
            RuntimeLog.LogBuildInfo();
            _launcherUpdateCancellation = new CancellationTokenSource();
            _ = new LauncherUpdateService().CheckAndApplyAsync(
                config.EnableAutoCheckUpdate,
                _launcherUpdateCancellation.Token);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (AudioProbe.TryHandleCommandLine(e.Args, out int exitCode))
            {
                RuntimeLog.Info("App", $"AudioProbe command handled, exitCode={exitCode}");
                RuntimeLog.RecordShutdownRequest("AudioProbeCompleted", $"exitCode={exitCode}");
                Shutdown(exitCode);
                return;
            }

            if (!WorkstationInstanceCoordinator.TryCreate(out _instanceCoordinator))
            {
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
            if (!DeploymentPresets.IsKnown(startupPreset) || requiresDeploymentSetup)
            {
                var selector = new WorkstationSelectionWindow();
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
                    Shutdown(0);
                    return;
                }

                if (!WorkstationConfigStore.TrySave(configuredRecordingHost, out string saveError))
                {
                    AppDialog.ShowMessage(
                        null,
                        $"配置保存失败，程序无法安全启动。\n\n{saveError}",
                        "启动失败",
                        AppDialogSeverity.Error);
                    Shutdown(1);
                    return;
                }

                config = configuredRecordingHost;
            }

            AutoStartService.Apply(config.AutoStartOnBoot);
            bool allowLanAccessSetup = !IsLanAccessSetupDisabled();
            MainViewModel.AllowLanAccessSetupOnStartup = allowLanAccessSetup;

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
            window.SourceInitialized += (_, _) =>
            {
                if (PresentationSource.FromVisual(window) is HwndSource source)
                    source.AddHook(ShutdownWindowProc);
            };
            MainWindow = window;
            _instanceCoordinator?.StartActivationListener(window);
            window.Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
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

            AppDialog.ShowMessage(
                null,
                $"配置保存失败，程序无法安全启动。\n\n请检查磁盘空间和配置目录权限。\n{error}",
                "启动失败",
                AppDialogSeverity.Error);
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
