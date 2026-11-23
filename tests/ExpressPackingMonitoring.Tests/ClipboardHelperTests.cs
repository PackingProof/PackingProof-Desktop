using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 剪贴板被别的程序占着时的行为：调用线程必须立刻返回（不能像 OLE 那样每次失败耗约 1 秒、
/// 把界面卡住），并且要转到后台继续重试，等占用方放开后把内容写进去。
/// </summary>
[Collection("WPF render tests")]
public sealed class ClipboardHelperTests
{
    private static Exception ClipboardBusyError() =>
        new COMException("OpenClipboard 失败 (0x800401D0)", unchecked((int)0x800401D0));

    /// <summary>粘贴按钮用的是这条读取路径：写进去什么，读出来就该是什么。</summary>
    [Fact]
    public void TryReadText_ReturnsTheTextThatWasCopied()
    {
        string text = "中通快递  45564646464646464";
        using var clipboardReady = new ManualResetEventSlim(false);
        if (!ClipboardHelper.TryCopyText(text, clipboardReady.Set))
            Assert.True(
                clipboardReady.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
                "剪贴板一直被占用，测试文本没写进去");

        // 同类里"占用后后台重试"的用例可能仍在后台写剪贴板，正好撞上就会读到瞬时失败；
        // 这里重试到读到为止，不能把真实的瞬时占用算成读取功能坏了。
        string read = "";
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (ClipboardHelper.TryReadText(out read) && read == text)
                return;

            Thread.Sleep(50);
        }

        Assert.Fail($"剪贴板读取没有得到刚写入的文本：[{read}]");
    }

    /// <summary>
    /// 占用期间调用要立刻返回（不阻塞界面），随后后台重试要把内容真正写进去。
    /// 真实占用来自别的进程（远程桌面/串流客户端），同进程内 OpenClipboard 是可重入的，
    /// 所以这里注入前几次失败来覆盖同一条重试链路。
    /// </summary>
    [Fact]
    public void TryCopyText_WhenClipboardBusy_ReturnsImmediatelyAndCopiesLater()
    {
        int attempts = 0;
        ClipboardHelper.WriteAttemptForTests = _ =>
            Interlocked.Increment(ref attempts) < 3 ? ClipboardBusyError() : null;
        try
        {
            var copied = new ManualResetEventSlim();
            Exception? failure = null;

            var stopwatch = Stopwatch.StartNew();
            bool copiedNow = ClipboardHelper.TryCopyText(
                "EPM-CLIPBOARD-TEST",
                onCopied: copied.Set,
                onFailed: error => failure = error);
            stopwatch.Stop();

            Assert.False(copiedNow, "剪贴板被占用时不该报告同步成功");
            Assert.True(
                stopwatch.ElapsedMilliseconds < 500,
                $"调用线程被重试拖住了：{stopwatch.ElapsedMilliseconds}ms");

            Assert.True(
                copied.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
                "后台重试没有把内容写进去");
            Assert.Null(failure);
            Assert.True(attempts >= 3);
        }
        finally
        {
            ClipboardHelper.WriteAttemptForTests = null;
        }
    }

    /// <summary>一直占用时：调用线程立刻返回，最终失败由回调告知，全程不阻塞界面。</summary>
    [Fact]
    public void TryCopyText_WhenClipboardStaysBusy_ReportsFailureWithoutBlocking()
    {
        ClipboardHelper.WriteAttemptForTests = _ => ClipboardBusyError();
        try
        {
            var failed = new ManualResetEventSlim();

            var stopwatch = Stopwatch.StartNew();
            bool copiedNow = ClipboardHelper.TryCopyText(
                "EPM-CLIPBOARD-TEST",
                onCopied: () => { },
                onFailed: _ => failed.Set());
            stopwatch.Stop();

            Assert.False(copiedNow);
            Assert.True(
                stopwatch.ElapsedMilliseconds < 500,
                $"调用线程被重试拖住了：{stopwatch.ElapsedMilliseconds}ms");
            Assert.True(
                failed.Wait(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken),
                "放弃复制时没有回调告知");
        }
        finally
        {
            ClipboardHelper.WriteAttemptForTests = null;
        }
    }

    [Fact]
    public void TryCopyText_WhenClipboardFree_CopiesOnTheCallingThread()
    {
        string text = "EPM-CLIPBOARD-" + Guid.NewGuid().ToString("N")[..8];
        bool copiedInline = false;

        bool copiedNow = ClipboardHelper.TryCopyText(text, onCopied: () => copiedInline = true);

        if (!copiedNow)
        {
            // 这条用例的前提是"剪贴板空闲"。被别的进程占着时助手会转后台重试，
            // 等一会儿还是写不进去就按环境跳过，别把别家的占用当成功能坏了。
            for (int attempt = 0; attempt < 100 && !copiedInline; attempt++)
                Thread.Sleep(50);
            Assert.Skip("剪贴板被其他进程占用，后台重试没能写入，跳过本次验证");
        }

        Assert.True(copiedNow);
        Assert.True(copiedInline);

        // 读回来确认内容真的落地了；共用桌面上剪贴板随时可能被别的进程抢走或改写，
        // 这种情况跳过而不是误报成功能坏了（与 InputPasteButtonTests 同一套处理）。
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (ReadClipboardText() == text) return;
            Thread.Sleep(50);
        }

        Assert.Skip("剪贴板被其他进程占用或改写，读不到刚写入的测试文本，跳过本次验证");
    }

    [Fact]
    public void CreateDibBytes_UsesAlignedTopDownBgr24Pixels()
    {
        const int width = 3;
        const int height = 2;
        const int sourceStride = width * 3;
        byte[] pixels = new byte[sourceStride * height];
        for (int index = 0; index < pixels.Length; index++)
            pixels[index] = (byte)index;
        BitmapSource source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgr24,
            null,
            pixels,
            sourceStride);
        source.Freeze();

        byte[] dib = ClipboardHelper.CreateDibBytes(source);

        Assert.Equal(40 + 24, dib.Length);
        Assert.Equal(40, BitConverter.ToInt32(dib, 0));
        Assert.Equal(width, BitConverter.ToInt32(dib, 4));
        Assert.Equal(-height, BitConverter.ToInt32(dib, 8));
        Assert.Equal(1, BitConverter.ToInt16(dib, 12));
        Assert.Equal(24, BitConverter.ToInt16(dib, 14));
        Assert.Equal(24, BitConverter.ToInt32(dib, 20));
    }

    /// <summary>WPF 读剪贴板要求 STA：复用测试宿主的共享 STA 线程读回来。</summary>
    private static string ReadClipboardText()
    {
        string result = "";
        WpfTestHost.Run(() =>
        {
            result = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }, TimeSpan.FromSeconds(10), "读取剪贴板超时");
        return result;
    }
}
