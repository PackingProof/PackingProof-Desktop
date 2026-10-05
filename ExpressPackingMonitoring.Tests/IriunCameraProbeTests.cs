using System.Diagnostics;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services.MediaFoundation;
using ExpressPackingMonitoring.ViewModels;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 真实摄像头冒烟：Media Foundation 能不能枚举到设备、能不能协商出格式、
/// 有没有真的出帧、GPU 转换是否生效、以及旋转后 Actual 宽高是否交换。
///
/// 只在现场有摄像头时才有意义：默认跳过，设 <c>EPM_CAMERA_SMOKE=1</c> 才运行
/// （与网页 UI 用例要求浏览器可执行文件的约定一致），避免在别的机器上因为摄像头
/// 被别的程序占用而产生假失败。
/// </summary>
[Collection("Camera tests")]
public sealed class IriunCameraProbeTests
{
    private const string EnableVariable = "EPM_CAMERA_SMOKE";

    private static void SkipUnlessEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal))
            Assert.Skip($"未设置 {EnableVariable}=1，跳过真实摄像头冒烟");
    }

    [Fact]
    public void Camera_StartsDeliversFramesAndRotatesDimensions()
    {
        SkipUnlessEnabled();
        IReadOnlyList<MfCaptureDevice> devices = MfCaptureDevice.Enumerate();
        var report = new System.Text.StringBuilder();
        report.AppendLine("MF devices: " + string.Join(" | ", devices.Select(d => d.Name)));

        MfCaptureDevice? camera = devices.FirstOrDefault(
            d => d.Name.Contains("Iriun", StringComparison.OrdinalIgnoreCase));
        if (camera == null)
            Assert.Skip("Media Foundation 没有枚举到 Iriun 摄像头：" + report);

        ProbeResult zero = CaptureSingle(camera.SymbolicLink, rotationDegrees: 0, report);
        ProbeResult ninety = CaptureSingle(camera.SymbolicLink, rotationDegrees: 90, report);

        WriteReport(report);

        Assert.True(zero.Frames >= 3, $"0 度采集只拿到 {zero.Frames} 帧");
        Assert.True(ninety.Frames >= 3, $"90 度采集只拿到 {ninety.Frames} 帧");
        Assert.Equal(zero.FrameWidth, ninety.FrameHeight);
        Assert.Equal(zero.FrameHeight, ninety.FrameWidth);
    }

    /// <summary>
    /// 双摄的真实形态：两台摄像头同时被两个采集源打开。
    /// 这是把"副摄接到主摄旁边"真正跑起来才会暴露的问题（设备独占、带宽、互相抢帧）。
    /// </summary>
    [Fact]
    public void TwoCameras_DeliverFramesAtTheSameTime()
    {
        SkipUnlessEnabled();
        IReadOnlyList<MfCaptureDevice> devices = MfCaptureDevice.Enumerate();
        if (devices.Count < 2)
            Assert.Skip($"Media Foundation 只枚举到 {devices.Count} 台摄像头，无法验证双路");

        string[] names = devices.Select(d => d.Name).ToArray();
        var report = new System.Text.StringBuilder();
        report.AppendLine("MF devices: " + string.Join(" | ", names));

        using var main = new Probe(devices[0].SymbolicLink, rotationDegrees: 0);
        using var secondary = new Probe(devices[1].SymbolicLink, rotationDegrees: 90);
        bool mainOk = main.WaitForFrames(3, TimeSpan.FromSeconds(20));
        bool secondaryOk = secondary.WaitForFrames(3, TimeSpan.FromSeconds(20));

        report.AppendLine("main      " + main.Describe());
        report.AppendLine("secondary " + secondary.Describe());
        WriteReport(report);

        Assert.True(mainOk, "主摄没有拿到帧");
        Assert.True(secondaryOk, "副摄没有拿到帧");
    }

    /// <summary>
    /// 真实主摄帧 + 副画面（整幅）的合成：小窗矩形之外一个像素都不许变，
    /// 小窗之内必须被副画面完整覆盖。副画面用纯色，结果可精确断言。
    /// </summary>
    [Fact]
    public void RealMainFrame_ComposesTheOverlayExactly()
    {
        SkipUnlessEnabled();
        IReadOnlyList<MfCaptureDevice> devices = MfCaptureDevice.Enumerate();
        if (devices.Count == 0)
            Assert.Skip("Media Foundation 没有枚举到摄像头");

        using Mat mainFrame = CaptureSingleFrame(devices[0].SymbolicLink, rotationDegrees: 0);
        using Mat composed = mainFrame.Clone();

        // 副画面用竖屏尺寸的纯红色块：贴合"面单摄像头竖装"的常见形态，也能精确断言像素。
        using var secondaryFrame = new Mat(720, 480, MatType.CV_8UC3, new Scalar(0, 0, 255));

        CameraOverlayRect? overlay = CameraOverlayLayout.Resolve(
            mainFrame.Width,
            mainFrame.Height,
            secondaryFrame.Width,
            secondaryFrame.Height,
            widthRatio: 0.4,
            margin: 16,
            allowUpscale: false);
        Assert.NotNull(overlay);

        bool composedOk = CameraOverlayComposer.TryCompose(
            composed,
            secondaryFrame,
            new Rect(0, 0, secondaryFrame.Width, secondaryFrame.Height),
            overlay!.Value);
        Assert.True(composedOk, "合成失败");

        // 小窗之外必须与主摄原帧逐像素一致：一个像素都不许被副画面碰到。
        using Mat outsideMask = new Mat(mainFrame.Size(), MatType.CV_8UC1, Scalar.All(255));
        Cv2.Rectangle(
            outsideMask,
            new Rect(overlay!.Value.X, overlay.Value.Y, overlay.Value.Width, overlay.Value.Height),
            Scalar.All(0),
            thickness: -1);
        using Mat difference = new();
        Cv2.Absdiff(mainFrame, composed, difference);
        Scalar outsideMean = Cv2.Mean(difference, outsideMask);
        Assert.Equal(0.0, outsideMean.Val0);
        Assert.Equal(0.0, outsideMean.Val1);
        Assert.Equal(0.0, outsideMean.Val2);

        // 小窗内部（避开 2 像素边框）必须就是副画面的红色。
        var inner = new Rect(
            overlay.Value.X + 4,
            overlay.Value.Y + 4,
            overlay.Value.Width - 8,
            overlay.Value.Height - 8);
        using var composedRegion = new Mat(composed, inner);
        Scalar composedMean = Cv2.Mean(composedRegion);
        Assert.True(
            composedMean.Val0 < 8 && composedMean.Val1 < 8 && composedMean.Val2 > 247,
            $"小窗内不是副画面内容：B={composedMean.Val0:F1} G={composedMean.Val1:F1} R={composedMean.Val2:F1}");
    }

    private static Mat CaptureSingleFrame(string symbolicLink, int rotationDegrees)
    {
        using var probe = new Probe(symbolicLink, rotationDegrees);
        Assert.True(probe.Started, $"启动失败：{probe.LastStartFailure}");
        Assert.True(
            probe.WaitForFrames(1, TimeSpan.FromSeconds(15)),
            $"15 秒内没有拿到帧：{probe.LastFrameFailure}");
        return probe.TakeLatestFrame();
    }

    private static ProbeResult CaptureSingle(string symbolicLink, int rotationDegrees, System.Text.StringBuilder report)
    {
        using var probe = new Probe(symbolicLink, rotationDegrees);
        bool got = probe.WaitForFrames(3, TimeSpan.FromSeconds(15));
        report.AppendLine($"single {probe.Describe()}");

        Assert.True(probe.Started, $"启动失败：{probe.LastStartFailure}");
        Assert.True(got, $"15 秒内没有拿到 3 帧，最后失败原因：{probe.LastFrameFailure}");
        return new ProbeResult(probe.Frames, probe.FrameWidth, probe.FrameHeight);
    }

    private static void WriteReport(System.Text.StringBuilder report)
    {
        string reportPath = Path.Combine(Path.GetTempPath(), "epm-iriun-probe", "report.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        File.AppendAllText(reportPath, report.ToString());
    }

    private readonly record struct ProbeResult(int Frames, int FrameWidth, int FrameHeight);

    /// <summary>打开一路摄像头并统计出帧，供单路与双路两种验证共用。</summary>
    private sealed class Probe : IDisposable
    {
        private readonly MfCameraSource _source;
        private readonly ManualResetEventSlim _received = new(false);
        private readonly object _frameLock = new();
        private Mat? _latestFrame;
        private int _frames;

        internal Probe(string symbolicLink, int rotationDegrees)
        {
            _source = new MfCameraSource(
                symbolicLink,
                targetWidth: 640,
                targetHeight: 480,
                targetFps: 10,
                colorMatrixMode: "auto",
                rotationDegrees: rotationDegrees);
            RotationDegrees = rotationDegrees;
            _source.FrameReady += (_, e) =>
            {
                using Mat frame = e.Frame;
                FrameWidth = frame.Width;
                FrameHeight = frame.Height;
                lock (_frameLock)
                {
                    _latestFrame?.Dispose();
                    _latestFrame = frame.Clone();
                }

                if (Interlocked.Increment(ref _frames) >= 3)
                    _received.Set();
            };

            var watch = Stopwatch.StartNew();
            Started = _source.Start();
            StartElapsedMs = watch.ElapsedMilliseconds;
        }

        internal int RotationDegrees { get; }

        internal bool Started { get; }

        internal long StartElapsedMs { get; }

        internal int Frames => Volatile.Read(ref _frames);

        internal int FrameWidth { get; private set; }

        internal int FrameHeight { get; private set; }

        internal string LastStartFailure => _source.LastStartFailure;

        internal string LastFrameFailure => _source.LastFrameFailure;

        internal bool WaitForFrames(int count, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < timeout)
            {
                if (Frames >= count)
                    return true;
                _received.Wait(TimeSpan.FromMilliseconds(200));
            }

            return Frames >= count;
        }

        /// <summary>取走最近一帧的所有权；没有则返回空 Mat。</summary>
        internal Mat TakeLatestFrame()
        {
            lock (_frameLock)
            {
                Mat? frame = _latestFrame;
                _latestFrame = null;
                return frame ?? new Mat();
            }
        }

        internal string Describe() =>
            $"rotation={RotationDegrees} started={Started} startMs={StartElapsedMs} frames={Frames} "
            + $"actual={_source.ActualWidth}x{_source.ActualHeight} frame={FrameWidth}x{FrameHeight} "
            + $"format={_source.ActualFormat} gpu={_source.UsesGpuConversion} "
            + $"gpuReason={_source.GpuDisableReason} bt709={_source.UsesBt709} "
            + $"startFailure={LastStartFailure} frameFailure={LastFrameFailure}";

        public void Dispose()
        {
            _source.Dispose();
            lock (_frameLock)
            {
                _latestFrame?.Dispose();
                _latestFrame = null;
            }

            _received.Dispose();
        }
    }
}
