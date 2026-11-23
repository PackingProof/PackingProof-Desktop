using LibVLCSharp.Shared;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ExpressPackingMonitoring.UI;

/// <summary>
/// 最小化的 LibVLC 视频宿主，只创建回放窗口自己的子 HWND。
/// 不使用 LibVLCSharp.WPF.VideoView，因为该控件会额外创建一个独立的透明 WPF
/// ForegroundWindow；在部分 DPI/显卡/打包环境里，这个辅助窗口会残留且无法关闭。
/// </summary>
public sealed class VlcVideoHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int SwpNoMove = 0x0002;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;

    private IntPtr _videoHwnd;

    internal IntPtr VideoHwnd => _videoHwnd;

    internal bool Attach(MediaPlayer player) => Attach(player, 0, 0);

    internal bool Attach(MediaPlayer player, double width, double height)
    {
        if (_videoHwnd == IntPtr.Zero)
            return false;

        if (width > 0 && height > 0)
        {
            System.Windows.DpiScale dpi =
                System.Windows.Media.VisualTreeHelper.GetDpi(this);
            SetWindowPos(
                _videoHwnd,
                IntPtr.Zero,
                0,
                0,
                DipToPixels(width, dpi.DpiScaleX),
                DipToPixels(height, dpi.DpiScaleY),
                SwpNoMove | SwpNoZOrder | SwpNoActivate);
        }

        player.Hwnd = _videoHwnd;
        return player.Hwnd == _videoHwnd;
    }

    internal static int DipToPixels(double value, double scale) =>
        Math.Max(1, (int)Math.Ceiling(value * Math.Max(0.01, scale)));

    internal void Detach(MediaPlayer? player)
    {
        if (player == null)
            return;

        try
        {
            player.Hwnd = IntPtr.Zero;
        }
        catch
        {
            // 播放器可能已经被原生层释放，解绑失败不能阻止窗口关闭。
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _videoHwnd = CreateWindowEx(
            0,
            "static",
            "",
            WsChild | WsVisible | WsClipChildren,
            0,
            0,
            0,
            0,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (_videoHwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 LibVLC 视频子窗口");

        return new HandleRef(this, _videoHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
            DestroyWindow(hwnd.Handle);
        _videoHwnd = IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);
}
