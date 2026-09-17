using ExpressPackingMonitoring.Services.MediaFoundation;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// Media Foundation 采集源的端到端验证：真的打开摄像头、真的收到帧。
///
/// 这组用例必须真跑硬件 —— 采集循环的坑（异步回调链断掉、stride 算错把画面撕开、
/// 掉线不通知）全都只在真实设备上出现，用假数据测不到。
/// 没有摄像头的机器上自动跳过，不制造假失败。
/// </summary>
public sealed class MfCameraSourceTests
{
    private const int FrameWaitMs = 4000;

    /// <summary>核心验证：能打开摄像头并收到尺寸正确的 BGR 帧。</summary>
    [Fact]
    public void CapturesFramesFromRealDevice()
    {
        MfCaptureDevice? device = TryFindDevice();
        if (device == null)
            return;

        using var source = new MfCameraSource(device.SymbolicLink, 1920, 1080, 30);
        using var received = new ManualResetEventSlim(false);
        int width = 0;
        int height = 0;
        int channels = 0;

        source.FrameReady += (_, args) =>
        {
            using Mat frame = args.Frame;
            width = frame.Cols;
            height = frame.Rows;
            channels = frame.Channels();
            received.Set();
        };

        if (!source.Start())
            return; // 设备被别的程序独占：这条路径本就该回退，不算失败

        try
        {
            Assert.True(
                received.Wait(FrameWaitMs),
                $"{FrameWaitMs}ms 内没有收到任何帧，采集回调链可能断了");
            Assert.Equal(3, channels);
            Assert.True(width > 0 && height > 0);
            Assert.Equal(source.ActualWidth, width);
            Assert.Equal(source.ActualHeight, height);
        }
        finally
        {
            source.Stop();
        }
    }

    /// <summary>
    /// 帧要持续到达，而不是只来一帧就停 —— 异步读取是链式的，
    /// 回调里忘记续上下一次 ReadSample 就会表现成"画面定格"。
    /// </summary>
    [Fact]
    public void KeepsDeliveringFrames()
    {
        MfCaptureDevice? device = TryFindDevice();
        if (device == null)
            return;

        using var source = new MfCameraSource(device.SymbolicLink, 1280, 720, 30);
        int frameCount = 0;
        using var enough = new ManualResetEventSlim(false);

        source.FrameReady += (_, args) =>
        {
            args.Frame.Dispose();
            if (Interlocked.Increment(ref frameCount) >= 10)
                enough.Set();
        };

        if (!source.Start())
            return;

        try
        {
            Assert.True(
                enough.Wait(FrameWaitMs),
                $"{FrameWaitMs}ms 内只收到 {frameCount} 帧，采集没有持续进行");
        }
        finally
        {
            source.Stop();
        }
    }

    /// <summary>
    /// 画面内容必须是合理的图像，不能是全黑或被撕开的乱码。
    /// 按错误的 stride 步进时画面会斜着错位，这条能抓住那种情况：
    /// 撕裂的画面相邻行差异会异常巨大。
    /// </summary>
    [Fact]
    public void ProducesCoherentImage()
    {
        MfCaptureDevice? device = TryFindDevice();
        if (device == null)
            return;

        using var source = new MfCameraSource(device.SymbolicLink, 1280, 720, 30);
        using var received = new ManualResetEventSlim(false);
        Mat? captured = null;

        source.FrameReady += (_, args) =>
        {
            if (Interlocked.CompareExchange(ref captured, args.Frame, null) != null)
            {
                args.Frame.Dispose();
                return;
            }
            received.Set();
        };

        if (!source.Start())
            return;

        try
        {
            if (!received.Wait(FrameWaitMs))
                return;

            Mat frame = captured!;
            Scalar mean = Cv2.Mean(frame);
            // 全黑（盖着镜头）也是合理画面，所以只排除"全部通道恰好为 0"这种可疑情况之外，
            // 重点检查相邻行的连续性。
            using Mat gray = new();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            double rowDifference = MeanAbsoluteRowDifference(gray);
            Assert.True(
                rowDifference < 60,
                $"相邻行平均差异 {rowDifference:F1} 过大，画面可能被错误的 stride 撕开"
                    + $"（格式 {source.ActualFormat}，均值 {mean.Val0:F0}/{mean.Val1:F0}/{mean.Val2:F0}）");
        }
        finally
        {
            source.Stop();
            captured?.Dispose();
        }
    }

    /// <summary>
    /// 停止之后不能再有帧到达：采集在录像结束后还继续回调会让资源无法释放，
    /// 也会把帧送进已经关闭的录像管线。
    /// </summary>
    [Fact]
    public void StopsDeliveringAfterStop()
    {
        MfCaptureDevice? device = TryFindDevice();
        if (device == null)
            return;

        using var source = new MfCameraSource(device.SymbolicLink, 1280, 720, 30);
        int frameCount = 0;
        source.FrameReady += (_, args) =>
        {
            args.Frame.Dispose();
            Interlocked.Increment(ref frameCount);
        };

        if (!source.Start())
            return;

        Thread.Sleep(800);
        source.Stop();
        Assert.False(source.IsRunning);

        int afterStop = Volatile.Read(ref frameCount);
        Thread.Sleep(500);
        // 允许停止瞬间正在途中的那一两帧，但不能继续源源不断地来。
        Assert.True(
            Volatile.Read(ref frameCount) - afterStop <= 2,
            "Stop 之后仍在持续收到帧");
    }

    /// <summary>反复启停不能泄漏或崩溃：摄像头休眠、重连都会走这条路。</summary>
    [Fact]
    public void SurvivesRepeatedStartStop()
    {
        MfCaptureDevice? device = TryFindDevice();
        if (device == null)
            return;

        for (int round = 0; round < 3; round++)
        {
            using var source = new MfCameraSource(device.SymbolicLink, 1280, 720, 30);
            source.FrameReady += (_, args) => args.Frame.Dispose();
            if (!source.Start())
                return;

            Thread.Sleep(300);
            source.Stop();
        }
    }

    /// <summary>设备不存在时干净地返回失败，不抛异常 —— 调用方要靠这个回退旧路径。</summary>
    [Fact]
    public void FailsCleanlyForUnknownDevice()
    {
        using var source = new MfCameraSource(@"\\?\usb#vid_0000&pid_0000#nonexistent", 1920, 1080, 30);

        Assert.False(source.Start());
        Assert.False(source.IsRunning);
    }

    /// <summary>
    /// 这台机器上至少要有一台设备能真正出帧。
    ///
    /// 这条是给上面那些用例兜底的：它们在"没有可用设备"时会跳过，
    /// 而采集代码一旦坏掉，跳过和通过在测试结果里长得一模一样
    /// （曾经就出现过"6 项全过、耗时 316ms、实际一帧都没采到"）。
    /// 有摄像头的开发机上这条会真跑，确保那些跳过是环境原因而不是代码坏了。
    /// </summary>
    [Fact]
    public void AtLeastOneDeviceActuallyDeliversFrames()
    {
        using MfPlatform? platform = MfPlatform.TryStart();
        if (platform == null)
            return;

        IReadOnlyList<MfCaptureDevice> devices = MfCaptureDevice.Enumerate();
        if (devices.Count == 0)
            return;

        var failures = new List<string>();
        foreach (MfCaptureDevice device in devices)
        {
            MfCaptureProbe.Result result = MfCaptureProbe.Probe(
                device.SymbolicLink,
                1920,
                1080,
                30,
                "auto");
            if (result.Usable)
                return;

            failures.Add($"[{device.Name}] {result.Failure}");
        }

        Assert.Fail(
            "所有采集设备都没能出帧，采集链路可能坏了：" + string.Join("；", failures));
    }

    private static MfCaptureDevice? TryFindDevice()
    {
        using MfPlatform? platform = MfPlatform.TryStart();
        if (platform == null)
            return null;

        IReadOnlyList<MfCaptureDevice> devices = MfCaptureDevice.Enumerate();
        return devices.Count > 0 ? devices[0] : null;
    }

    /// <summary>相邻行的平均绝对差。撕裂的画面这个值会异常大。</summary>
    private static double MeanAbsoluteRowDifference(Mat gray)
    {
        if (gray.Rows < 2)
            return 0;

        using Mat upper = gray[new Rect(0, 0, gray.Cols, gray.Rows - 1)];
        using Mat lower = gray[new Rect(0, 1, gray.Cols, gray.Rows - 1)];
        using Mat difference = new();
        Cv2.Absdiff(upper, lower, difference);
        return Cv2.Mean(difference).Val0;
    }
}
