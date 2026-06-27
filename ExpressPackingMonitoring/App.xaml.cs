using System;
using System.Threading.Tasks;
using System.Windows;

namespace ExpressPackingMonitoring
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            RegisterRuntimeExceptionLogging();
            RuntimeLog.Info("App", "Application startup");

            if (AudioProbe.TryHandleCommandLine(e.Args, out int exitCode))
            {
                RuntimeLog.Info("App", $"AudioProbe command handled, exitCode={exitCode}");
                Shutdown(exitCode);
                return;
            }

            var config = WorkstationConfigStore.Load();
            bool forceChoose = e.Args.Any(a => string.Equals(a, "--choose-workstation", StringComparison.OrdinalIgnoreCase));
            string requestedRole = ResolveRequestedRole(e.Args);
            if (forceChoose)
                config.WorkstationRole = "";
            if (!string.IsNullOrWhiteSpace(requestedRole))
            {
                config.WorkstationRole = requestedRole;
                WorkstationConfigStore.Save(config);
            }

            if (!WorkstationRoles.IsKnown(config.WorkstationRole))
            {
                var selector = new WorkstationSelectionWindow();
                if (selector.ShowDialog() != true || string.IsNullOrWhiteSpace(selector.SelectedRole))
                {
                    Shutdown(0);
                    return;
                }

                config.WorkstationRole = selector.SelectedRole;
                WorkstationConfigStore.Save(config);
                if (WorkstationNetwork.TryRestartApplication())
                    return;
            }

            Window window = string.Equals(config.WorkstationRole, WorkstationRoles.PrintStation, StringComparison.OrdinalIgnoreCase)
                ? new PrintWorkstationWindow(config)
                : new MainWindow();
            MainWindow = window;
            window.Show();
        }

        private static string ResolveRequestedRole(string[] args)
        {
            if (args.Any(a => string.Equals(a, "--monitor", StringComparison.OrdinalIgnoreCase)))
                return WorkstationRoles.CameraMonitor;
            if (args.Any(a => string.Equals(a, "--order-workstation", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(a, "--print-station", StringComparison.OrdinalIgnoreCase)))
                return WorkstationRoles.PrintStation;
            return "";
        }

        private static void RegisterRuntimeExceptionLogging()
        {
            Current.DispatcherUnhandledException += (_, e) =>
            {
                RuntimeLog.Error("Unhandled", "DispatcherUnhandledException", e.Exception);
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
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
