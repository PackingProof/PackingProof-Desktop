using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 剪贴板写入。
///
/// 剪贴板同一时刻只能被一个进程打开：远程桌面 / 串流客户端的剪贴板同步、输入法、
/// 剪贴板管理器都会周期性抢占，此时 OpenClipboard 直接失败（CLIPBRD_E_CANT_OPEN）。
///
/// 不能拿 WPF 的 Clipboard.SetDataObject 做重试：那是 OLE，失败时内部会自己重试到约 1 秒
/// （实测 980ms/次），在 UI 线程上重试几次就是几秒假死——现场反馈"复制失败时整页卡住"就是这么来的。
///
/// 这里的做法：
///   1) 先在调用线程用 Win32 写一次：成功只要几十微秒，被占用时失败也是瞬时；
///   2) 失败就把重试丢到后台线程（每 100ms 一次，最多 5 秒），
///      成功或最终放弃时回调调用方，界面全程不阻塞。
/// </summary>
internal static class ClipboardHelper
{
    /// <summary>后台继续重试的时长：串流客户端的剪贴板同步通常几百毫秒到一两秒就会放开。</summary>
    private static readonly TimeSpan BackgroundRetryWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BackgroundRetryInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan BusyLogThrottle = TimeSpan.FromMinutes(1);

    private const uint CF_TEXT = 1;
    private const uint CF_DIB = 8;
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    private static readonly object Gate = new();
    private static PendingCopy? _pendingCopy;
    private static bool _backgroundRetryRunning;
    private static DateTime _lastBusyLogAtUtc;

    /// <summary>
    /// 测试用：替换实际写入，用来验证"剪贴板一直被占用"时的后台重试与不阻塞。
    /// 返回 null 表示写入成功，否则返回失败原因。生产路径始终是下面的 Win32 实现。
    /// </summary>
    internal static Func<string, Exception?>? WriteAttemptForTests { get; set; }

    /// <summary>
    /// 把文本写进剪贴板，界面永远不会被这个调用卡住。
    /// 返回 true 表示已经写好，并在当前线程回调了 <paramref name="onCopied"/>；
    /// 返回 false 表示剪贴板此刻被别的程序占着，已转入后台重试，
    /// 之后成功或放弃时会在后台线程回调一次（调用方自己切回 UI 线程再提示）。
    /// </summary>
    public static bool TryCopyText(string text, Action onCopied, Action<Exception>? onFailed = null)
    {
        ArgumentNullException.ThrowIfNull(onCopied);

        if (string.IsNullOrEmpty(text))
        {
            onFailed?.Invoke(new ArgumentException("没有可复制的内容"));
            return false;
        }

        if (TryWriteText(text, out Exception error))
        {
            onCopied();
            return true;
        }

        LogBusyOnce(error);
        QueueBackgroundRetry(text, onCopied, onFailed);
        return false;
    }

    /// <summary>
    /// 把视频截图以 CF_DIB 写入剪贴板。位图转换和剪贴板争用重试都在后台线程，
    /// 避免 WPF OLE 剪贴板在 UI 线程上等待约 1 秒。
    /// </summary>
    internal static async Task CopyImageAsync(
        BitmapSource image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        byte[] dib = await Task.Run(
            () => CreateDibBytes(image),
            cancellationToken);
        await Task.Run(
            () => WriteClipboardBytesWithRetry(CF_DIB, dib, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// 读一次剪贴板文本：被别的程序占着时立刻失败返回 false，绝不在 UI 线程上等 ——
    /// 与写入同一条原则，粘贴按钮宁可什么都不做也不能让界面卡住。
    /// </summary>
    internal static bool TryReadText(out string text)
    {
        text = string.Empty;
        if (!OpenClipboard(IntPtr.Zero))
            return false;

        try
        {
            IntPtr handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
                return TryReadAnsiText(out text);

            IntPtr pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return false;

            try
            {
                text = Marshal.PtrToStringUni(pointer) ?? string.Empty;
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }

            return true;
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    /// <summary>没有 Unicode 文本时退回 ANSI（少数老工具只放 CF_TEXT）。</summary>
    private static bool TryReadAnsiText(out string text)
    {
        text = string.Empty;
        IntPtr handle = GetClipboardData(CF_TEXT);
        if (handle == IntPtr.Zero)
            return false;

        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
            return false;

        try
        {
            text = Marshal.PtrToStringAnsi(pointer) ?? string.Empty;
        }
        finally
        {
            _ = GlobalUnlock(handle);
        }

        return true;
    }

    /// <summary>
    /// CF_DIB 的像素必须四字节对齐。BGR24 没有透明度歧义，粘贴到聊天工具、
    /// 画图和 Office 时比直接塞 WPF BitmapSource 更稳定。
    /// </summary>
    internal static byte[] CreateDibBytes(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        BitmapSource pixelsSource = image.Format == PixelFormats.Bgr24
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        if (pixelsSource.CanFreeze)
            pixelsSource.Freeze();

        int width = pixelsSource.PixelWidth;
        int height = pixelsSource.PixelHeight;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("截图尺寸无效");

        int stride = checked(((width * 3) + 3) & ~3);
        byte[] pixels = new byte[checked(stride * height)];
        pixelsSource.CopyPixels(pixels, stride, 0);

        using var stream = new MemoryStream(40 + pixels.Length);
        using var writer = new BinaryWriter(stream);
        writer.Write(40);
        writer.Write(width);
        writer.Write(-height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(pixels.Length);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(pixels);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteClipboardBytesWithRetry(
        uint format,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        Exception lastError = new TimeoutException("剪贴板一直被别的程序占用");
        DateTime deadline = DateTime.UtcNow + BackgroundRetryWindow;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryWriteClipboardBytes(format, bytes, out lastError))
                return;
            Thread.Sleep(BackgroundRetryInterval);
        }

        throw new InvalidOperationException(lastError.Message, lastError);
    }

    private static bool TryWriteClipboardBytes(uint format, byte[] bytes, out Exception error)
    {
        error = null!;
        if (!OpenClipboard(IntPtr.Zero))
        {
            int code = Marshal.GetLastWin32Error();
            error = new COMException($"OpenClipboard 失败 (0x{code:X8})", code);
            return false;
        }

        IntPtr handle = IntPtr.Zero;
        try
        {
            if (!EmptyClipboard())
            {
                int code = Marshal.GetLastWin32Error();
                error = new COMException($"EmptyClipboard 失败 (0x{code:X8})", code);
                return false;
            }

            handle = AllocateClipboardMemory(bytes);
            if (SetClipboardData(format, handle) == IntPtr.Zero)
            {
                int code = Marshal.GetLastWin32Error();
                error = new COMException($"SetClipboardData 失败 (0x{code:X8})", code);
                return false;
            }

            handle = IntPtr.Zero;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
                GlobalFree(handle);
            CloseClipboard();
        }
    }

    /// <summary>空闲时的快路径：一次 Win32 写入；被占用时失败也是瞬时，不会像 OLE 那样耗掉约 1 秒。</summary>
    private static bool TryWriteText(string text, out Exception error)
    {
        if (WriteAttemptForTests is { } injected)
        {
            Exception? failure = injected(text);
            error = failure!;
            return failure is null;
        }

        error = null!;
        if (!OpenClipboard(IntPtr.Zero))
        {
            int code = Marshal.GetLastWin32Error();
            error = new COMException($"OpenClipboard 失败 (0x{code:X8})", code);
            return false;
        }

        IntPtr unicodeHandle = IntPtr.Zero;
        IntPtr textHandle = IntPtr.Zero;
        try
        {
            if (!EmptyClipboard())
            {
                int code = Marshal.GetLastWin32Error();
                error = new COMException($"EmptyClipboard 失败 (0x{code:X8})", code);
                return false;
            }

            unicodeHandle = AllocateClipboardMemory(Encoding.Unicode.GetBytes(text + "\0"));
            if (SetClipboardData(CF_UNICODETEXT, unicodeHandle) == IntPtr.Zero)
            {
                int code = Marshal.GetLastWin32Error();
                error = new COMException($"SetClipboardData 失败 (0x{code:X8})", code);
                return false;
            }
            unicodeHandle = IntPtr.Zero; // 所有权交给系统，不能再释放

            // CF_TEXT 让只认 ANSI 的老程序也能粘上；拿不到系统 ANSI 就跳过，
            // CF_UNICODETEXT 已经够现代程序使用。
            if (TryGetAnsiBytes(text, out byte[] ansiBytes))
            {
                textHandle = AllocateClipboardMemory(ansiBytes);
                if (SetClipboardData(CF_TEXT, textHandle) == IntPtr.Zero)
                    textHandle = IntPtr.Zero; // 老格式没写上不影响这次复制
                else
                    textHandle = IntPtr.Zero; // 所有权交给系统
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
        finally
        {
            if (unicodeHandle != IntPtr.Zero) GlobalFree(unicodeHandle);
            if (textHandle != IntPtr.Zero) GlobalFree(textHandle);
            CloseClipboard();
        }
    }

    private static IntPtr AllocateClipboardMemory(byte[] bytes)
    {
        IntPtr handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (handle == IntPtr.Zero)
            throw new OutOfMemoryException("GlobalAlloc 失败");

        IntPtr target = GlobalLock(handle);
        if (target == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new OutOfMemoryException("GlobalLock 失败");
        }

        try
        {
            Marshal.Copy(bytes, 0, target, bytes.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }
        return handle;
    }

    private static bool TryGetAnsiBytes(string text, out byte[] bytes)
    {
        bytes = [];
        try
        {
            Encoding ansi = Encoding.GetEncoding(0);
            byte[] encoded = ansi.GetBytes(text);
            bytes = new byte[encoded.Length + 1];
            encoded.CopyTo(bytes, 0);
            bytes[^1] = 0;
            return true;
        }
        catch (Exception)
        {
            // 没注册代码页提供程序时拿不到系统 ANSI，只写 Unicode 格式
            return false;
        }
    }

    private static void QueueBackgroundRetry(string text, Action onCopied, Action<Exception>? onFailed)
    {
        lock (Gate)
        {
            // 连续点几次复制时只保留最新一次内容：用户要的就是最后点的那个单号。
            _pendingCopy = new PendingCopy(text, onCopied, onFailed, DateTime.UtcNow + BackgroundRetryWindow);
            if (_backgroundRetryRunning)
                return;

            _backgroundRetryRunning = true;
        }

        _ = Task.Run(BackgroundRetryLoop);
    }

    private static void BackgroundRetryLoop()
    {
        while (true)
        {
            PendingCopy pending;
            lock (Gate)
            {
                if (_pendingCopy is null)
                {
                    _backgroundRetryRunning = false;
                    return;
                }
                pending = _pendingCopy;
                _pendingCopy = null;
            }

            Exception lastError = new TimeoutException("剪贴板一直被别的程序占用");
            bool copied = false;
            while (DateTime.UtcNow < pending.DeadlineUtc)
            {
                if (TryWriteText(pending.Text, out lastError))
                {
                    copied = true;
                    break;
                }
                Thread.Sleep(BackgroundRetryInterval);
            }

            if (copied)
            {
                RuntimeLog.Info(
                    "Clipboard",
                    $"剪贴板被占用，后台重试后已写入（{pending.Text.Length} 字符）");
                pending.OnCopied();
            }
            else
            {
                RuntimeLog.Warn(
                    "Clipboard",
                    $"剪贴板一直被占用，放弃复制：{lastError.Message}; {DescribeClipboardOwner()}");
                pending.OnFailed?.Invoke(lastError);
            }
        }
    }

    private static void LogBusyOnce(Exception error)
    {
        DateTime now = DateTime.UtcNow;
        lock (Gate)
        {
            if (now - _lastBusyLogAtUtc < BusyLogThrottle)
                return;
            _lastBusyLogAtUtc = now;
        }

        RuntimeLog.Info(
            "Clipboard",
            $"剪贴板被占用（瞬时失败，已转后台重试）：{error.Message}; {DescribeClipboardOwner()}");
    }

    /// <summary>剪贴板被谁开着：排查现场时这条信息最有用（串流客户端、输入法、剪贴板管理器都出现过）。</summary>
    private static string DescribeClipboardOwner()
    {
        try
        {
            IntPtr window = GetOpenClipboardWindow();
            if (window == IntPtr.Zero)
                return "当前没有窗口持有剪贴板打开句柄（占用可能已释放）";

            var className = new StringBuilder(256);
            _ = GetClassName(window, className, className.Capacity);
            uint threadId = GetWindowThreadProcessId(window, out uint processId);
            string processName = "unknown";
            try
            {
                using Process process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
            }
            catch (Exception)
            {
                // 进程已退出或没有查询权限：保留 pid 已经够用
            }

            return $"打开剪贴板的窗口 hwnd=0x{window.ToInt64():X}, pid={processId}, thread={threadId}, "
                + $"process={processName}, class={className}";
        }
        catch (Exception ex)
        {
            return $"读取剪贴板占用者失败：{ex.Message}";
        }
    }

    private sealed record PendingCopy(
        string Text,
        Action OnCopied,
        Action<Exception>? OnFailed,
        DateTime DeadlineUtc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetOpenClipboardWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
