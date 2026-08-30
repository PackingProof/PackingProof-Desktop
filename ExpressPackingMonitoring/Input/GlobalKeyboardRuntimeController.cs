using ExpressPackingMonitoring.Config;

namespace ExpressPackingMonitoring.Input;

internal static class GlobalKeyboardListeningPolicy
{
    internal static bool ShouldPromptBeforeTray(bool enabled, string? trayBehavior) =>
        enabled && TrayKeyboardListeningBehaviors.Normalize(trayBehavior) ==
            TrayKeyboardListeningBehaviors.Ask;

    internal static bool ShouldListen(
        bool enabled,
        bool isInTray,
        string? trayBehavior,
        bool? sessionTrayOverride = null) =>
        enabled &&
        (!isInTray ||
         (sessionTrayOverride ??
          TrayKeyboardListeningBehaviors.Normalize(trayBehavior) !=
          TrayKeyboardListeningBehaviors.Pause));
}

internal sealed class GlobalKeyboardRuntimeController : IDisposable
{
    private readonly IGlobalKeyboardHook _hook;
    private readonly Action<string> _barcodeScanned;
    private bool _enabled;
    private string _trayBehavior = TrayKeyboardListeningBehaviors.Ask;
    private bool _isInTray;
    private bool? _sessionTrayOverride;
    private bool _disposed;

    internal GlobalKeyboardRuntimeController(
        IGlobalKeyboardHook hook,
        Action<string> barcodeScanned)
    {
        _hook = hook ?? throw new ArgumentNullException(nameof(hook));
        _barcodeScanned = barcodeScanned ?? throw new ArgumentNullException(nameof(barcodeScanned));
        _hook.BarcodeScanned += _barcodeScanned;
    }

    internal void Apply(AppConfig config, Func<string, bool> isAutoSubmitCandidate)
    {
        if (_disposed)
            return;

        _enabled = config.EnableGlobalKeyboard;
        _trayBehavior = TrayKeyboardListeningBehaviors.Normalize(
            config.TrayKeyboardListeningBehavior);
        _hook.ConfigureAutoSubmit(
            config.EnableScannerAutoSubmit,
            config.ScannerAutoSubmitMinLength,
            config.ScannerAutoSubmitQuietMs,
            config.ScannerAutoSubmitMaxAverageIntervalMs,
            config.ScannerAutoSubmitMaxKeyIntervalMs,
            isAutoSubmitCandidate);
        RefreshListeningState();
    }

    internal void SetTrayState(bool isInTray, bool? sessionTrayOverride = null)
    {
        if (_disposed)
            return;

        _isInTray = isInTray;
        _sessionTrayOverride = isInTray ? sessionTrayOverride : null;
        RefreshListeningState();
    }

    private void RefreshListeningState()
    {
        if (GlobalKeyboardListeningPolicy.ShouldListen(
                _enabled,
                _isInTray,
                _trayBehavior,
                _sessionTrayOverride))
        {
            _hook.Start();
        }
        else
        {
            _hook.Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _hook.BarcodeScanned -= _barcodeScanned;
        _hook.Dispose();
    }
}
