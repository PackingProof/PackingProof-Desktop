using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Input;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.UI;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Resources;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 托盘图标只用 Windows 原生的 Shell_NotifyIcon，不再引用 WinForms。
/// 主窗口即使隐藏也保留 HWND，托盘消息和 Explorer 重启通知都由它的消息钩子接收。
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIIF_INFO = 0x00000001;
    private const uint MF_STRING = 0x00000000;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int WM_NULL = 0x0000;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_TRAYICON = 0x8001;

    private const uint TrayIconId = 1;
    private const uint MenuOpen = 1;
    private const uint MenuExit = 2;

    private readonly Window _window;
    private readonly Action _exitRequested;
    private readonly Action<bool, bool?>? _trayStateChanged;
    private readonly IntPtr _hwnd;
    private readonly HwndSource? _source;
    private readonly Icon? _icon;
    private readonly uint _taskbarCreatedMessage;
    private NOTIFYICONDATA _data;
    private bool _disposed;
    private bool _visible;
    private bool _hasShownMinimizeTip;

    internal bool IsVisible => _visible;

    public TrayIconService(
        Window window,
        Action exitRequested,
        Action<bool, bool?>? trayStateChanged = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _exitRequested = exitRequested ?? throw new ArgumentNullException(nameof(exitRequested));
        _trayStateChanged = trayStateChanged;

        _hwnd = new WindowInteropHelper(_window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        _icon = LoadTrayIcon();

        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _icon?.Handle ?? IntPtr.Zero,
            szTip = "快递打包监控",
            szInfo = "",
            szInfoTitle = ""
        };
        // 托盘图标从启动后常驻；最小化只切换主窗口可见性，不再决定图标是否存在。
        EnsureIconVisible();
    }

    public void MinimizeToTray(bool? sessionKeyboardListeningOverride = null)
    {
        if (_disposed)
            return;

        EnsureIconVisible();
        _window.ShowInTaskbar = false;
        _window.Hide();
        _trayStateChanged?.Invoke(true, sessionKeyboardListeningOverride);
        if (!_hasShownMinimizeTip)
        {
            _hasShownMinimizeTip = true;
            ShowBalloonTip(
                "快递打包监控",
                AppLanguage.Get("程序仍在后台运行，双击托盘图标可打开主界面"));
        }
    }

    public void RestoreWindow()
    {
        if (_disposed)
            return;

        _window.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
                return;

            EnsureIconVisible();
            _window.ShowInTaskbar = true;
            _window.Show();
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            _window.Activate();
            _trayStateChanged?.Invoke(false, null);
        });
    }

    private void RequestExit()
    {
        RestoreWindow();
        _window.Dispatcher.BeginInvoke(_exitRequested);
    }

    internal static Icon? LoadTrayIcon()
    {
        try
        {
            StreamResourceInfo? resource = Application.GetResourceStream(
                new Uri("pack://application:,,,/app.ico", UriKind.Absolute));
            if (resource == null)
                return null;

            using Stream stream = resource.Stream;
            int size = GetSystemMetrics(SM_CXSMICON);
            if (size is < 16 or > 256)
                size = 16;
            return new Icon(stream, size, size);
        }
        catch
        {
            return null;
        }
    }

    private void EnsureIconVisible()
    {
        if (_visible)
        {
            Shell_NotifyIcon(NIM_MODIFY, ref _data);
            return;
        }

        Shell_NotifyIcon(NIM_ADD, ref _data);
        _visible = true;
    }

    private void RemoveIcon()
    {
        if (!_visible)
            return;

        Shell_NotifyIcon(NIM_DELETE, ref _data);
        _visible = false;
    }

    private void ShowBalloonTip(string title, string message)
    {
        _data.uFlags = NIF_INFO;
        _data.szInfoTitle = title;
        _data.szInfo = message;
        _data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref _data);
        _data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed)
            return IntPtr.Zero;

        if (_taskbarCreatedMessage != 0 && msg == unchecked((int)_taskbarCreatedMessage))
        {
            if (_visible)
            {
                // Explorer 重启后托盘图标会被清掉，需要重新注册。
                _visible = false;
                EnsureIconVisible();
            }
            return IntPtr.Zero;
        }

        if (msg != WM_TRAYICON)
            return IntPtr.Zero;

        int notification = lParam.ToInt32() & 0xFFFF;
        if (notification == WM_LBUTTONDBLCLK)
        {
            RestoreWindow();
            handled = true;
        }
        else if (notification is WM_RBUTTONUP or WM_CONTEXTMENU)
        {
            ShowContextMenu();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            AppendMenuW(menu, MF_STRING, new UIntPtr(MenuOpen), AppLanguage.Get("打开主界面"));
            AppendMenuW(menu, MF_STRING, new UIntPtr(MenuExit), AppLanguage.Get("直接退出"));

            if (!GetCursorPos(out POINT point))
                return;

            // 不先抢前台，菜单点到别处时不会自动消失。
            SetForegroundWindow(_hwnd);
            int command = TrackPopupMenu(
                menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON,
                point.X,
                point.Y,
                0,
                _hwnd,
                IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (command == MenuOpen)
                RestoreWindow();
            else if (command == MenuExit)
                RequestExit();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        RemoveIcon();
        _source?.RemoveHook(WndProc);
        _icon?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}

internal sealed class WindowCloseBehaviorController : IDisposable
{
    private readonly Window _owner;
    private readonly TrayIconService _trayIcon;
    private readonly bool _promptEnabled;

    public WindowCloseBehaviorController(
        Window owner,
        Action exitRequested,
        bool promptEnabled,
        Action<bool, bool?>? trayStateChanged = null)
    {
        _owner = owner;
        _promptEnabled = promptEnabled;
        _trayIcon = new TrayIconService(owner, exitRequested, trayStateChanged);
    }

    public WindowCloseChoice HandleClose(AppConfig config, bool bypassPreference)
    {
        if (bypassPreference || !_promptEnabled)
            return WindowCloseChoice.Exit;

        string behavior = WindowCloseBehaviors.Normalize(config.WindowCloseBehavior);
        WindowCloseChoice choice;
        bool rememberChoice = false;

        if (behavior == WindowCloseBehaviors.Ask)
        {
            var dialog = new CloseBehaviorDialog { Owner = _owner };
            if (dialog.ShowDialog() != true || dialog.Choice == WindowCloseChoice.None)
                return WindowCloseChoice.None;

            choice = dialog.Choice;
            rememberChoice = dialog.RememberChoice;
        }
        else
        {
            choice = behavior == WindowCloseBehaviors.MinimizeToTray
                ? WindowCloseChoice.MinimizeToTray
                : WindowCloseChoice.Exit;
        }

        if (rememberChoice)
        {
            string savedBehavior = choice == WindowCloseChoice.MinimizeToTray
                ? WindowCloseBehaviors.MinimizeToTray
                : WindowCloseBehaviors.Exit;
            if (WorkstationConfigStore.TryUpdate(
                    saved => saved.WindowCloseBehavior = savedBehavior,
                    out AppConfig savedConfig,
                    out string error))
            {
                config.WindowCloseBehavior = savedConfig.WindowCloseBehavior;
            }
            else
            {
                AppDialog.Error(
                    _owner,
                    $"关闭行为保存失败，下次仍会询问：{error}",
                    "设置保存失败");
            }
        }

        if (choice == WindowCloseChoice.MinimizeToTray)
        {
            bool? sessionOverride = ResolveTrayKeyboardListeningPreference(config);
            _trayIcon.MinimizeToTray(sessionOverride);
        }
        return choice;
    }

    private bool? ResolveTrayKeyboardListeningPreference(AppConfig config)
    {
        if (!GlobalKeyboardListeningPolicy.ShouldPromptBeforeTray(
                config.EnableGlobalKeyboard,
                config.TrayKeyboardListeningBehavior))
        {
            return null;
        }

        var dialog = new TrayKeyboardListeningDialog { Owner = _owner };
        if (dialog.ShowDialog() != true ||
            dialog.Choice == TrayKeyboardListeningChoice.None)
        {
            return null;
        }

        bool continueListening = dialog.Choice ==
            TrayKeyboardListeningChoice.ContinueListening;
        if (!dialog.RememberChoice)
            return continueListening;

        string savedBehavior = continueListening
            ? TrayKeyboardListeningBehaviors.Continue
            : TrayKeyboardListeningBehaviors.Pause;
        if (WorkstationConfigStore.TryUpdate(
                saved => saved.TrayKeyboardListeningBehavior = savedBehavior,
                out AppConfig savedConfig,
                out string error))
        {
            config.TrayKeyboardListeningBehavior = savedConfig.TrayKeyboardListeningBehavior;
            return continueListening;
        }

        AppDialog.Error(
            _owner,
            AppLanguage.Format(
                "托盘扫码监听选择保存失败，本次仍按你的选择执行，下次进入托盘时会再次询问：{0}",
                error),
            AppLanguage.Get("设置保存失败"));
        return continueListening;
    }

    public void Dispose() => _trayIcon.Dispose();
}
