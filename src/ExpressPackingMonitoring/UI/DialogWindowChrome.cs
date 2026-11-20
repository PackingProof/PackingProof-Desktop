using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ExpressPackingMonitoring.UI;

/// <summary>
/// 对话框式窗口只保留关闭按钮，同时保留可缩放边框和右下角缩放手柄。
/// </summary>
internal static class DialogWindowChrome
{
    private const int GwlStyle = -16;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    internal static void UseCloseButtonOnly(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindows())
            return;

        window.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return;

            long style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
            long updated = RemoveMinimizeAndMaximize(style);
            if (updated == style)
                return;

            _ = SetWindowLongPtr(handle, GwlStyle, new IntPtr(updated));
            _ = SetWindowPos(
                handle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        };
    }

    internal static long RemoveMinimizeAndMaximize(long style) =>
        style & ~(WsMinimizeBox | WsMaximizeBox);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(
        IntPtr handle,
        int index,
        IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
