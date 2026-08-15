using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

internal static class ClipboardHelper
{
    public static bool TrySetDataObject(
        string text,
        out Exception error,
        int maxAttempts = 5)
    {
        error = null!;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                RuntimeLog.Warn(
                    "Clipboard",
                    $"复制网址写入剪贴板失败，第 {attempt + 1}/{maxAttempts} 次：{ex.Message}; {DescribeClipboardOwner()}");
                if (attempt < maxAttempts - 1)
                    Thread.Sleep(80);
            }
        }

        return false;
    }

    private static string DescribeClipboardOwner()
    {
        try
        {
            IntPtr window = GetOpenClipboardWindow();
            if (window == IntPtr.Zero)
                return "当前没有窗口持有剪贴板打开句柄（占用可能已瞬时释放）";

            uint threadId = GetWindowThreadProcessId(window, out uint processId);
            string processName = "unknown";
            try
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
            }
            catch
            {
            }

            var className = new StringBuilder(256);
            GetClassName(window, className, className.Capacity);
            return $"打开剪贴板的窗口 hwnd=0x{window.ToInt64():X}, pid={processId}, thread={threadId}, process={processName}, class={className}";
        }
        catch (Exception ex)
        {
            return $"读取剪贴板占用窗口失败：{ex.Message}";
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
}
