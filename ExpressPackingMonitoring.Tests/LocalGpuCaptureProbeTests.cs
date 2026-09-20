using System.Diagnostics;
using ExpressPackingMonitoring.Services.MediaFoundation;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 本机真设备上的采集开销实测。只打印不断言：这台机器上有什么摄像头
/// 不属于代码契约，断言它会让别的机器上无故失败。
///
/// 存在的意义是把"接入 GPU 之后 CPU 到底降了多少"落成可复现的过程，
/// 而不是只留一句结论。之前正是因为拿微基准当实测，报出去的数字差了一个数量级。
/// </summary>
public sealed class LocalGpuCaptureProbeTests
{
    private readonly ITestOutputHelper _output;

    public LocalGpuCaptureProbeTests(ITestOutputHelper output) => _output = output;

    private const int MeasureMs = 4000;

    /// <summary>
    /// 这两个用例要独占摄像头并跑十秒，默认跑常规测试时不该被卷进来。
    /// 设 PACKINGPROOF_CAPTURE_PROBE=1 才执行。
    /// </summary>
    private bool SkipUnlessOptedIn()
    {
        if (Environment.GetEnvironmentVariable("PACKINGPROOF_CAPTURE_PROBE") == "1")
            return false;

        _output.WriteLine("skipped: set PACKINGPROOF_CAPTURE_PROBE=1 to run local capture probes");
        return true;
    }

    /// <summary>列出本机 Media Foundation 能看到的采集设备。</summary>
    [Fact]
    public void ReportsLocalMediaFoundationDevices()
    {
        if (SkipUnlessOptedIn())
            return;

        using MfPlatform? platform = MfPlatform.TryStart();
        if (platform == null)
        {
            _output.WriteLine("Media Foundation unavailable");
            return;
        }

        var devices = MfCaptureDevice.Enumerate();
        _output.WriteLine($"MF devices: {devices.Count}");
        foreach (MfCaptureDevice device in devices)
        {
            var formats = MfCaptureDevice.ReadNativeFormats(device);
            _output.WriteLine($"  name={device.Name} formats={formats.Count}");
            _output.WriteLine($"    link={device.SymbolicLink}");
        }
    }

    /// <summary>
    /// 同一台设备上跑两遍，对照 GPU 与 CPU 转换的实际 CPU 占用。
    ///
    /// CPU 时间取自进程自身而不是采样任务管理器：它把采集线程、转换、
    /// 回读全算进来，且不受别的进程干扰。两遍都用同一台设备、同一段时长，
    /// 否则帧率差异会直接冒充成收益。
    /// </summary>
    [Fact]
    public void ReportsGpuVersusCpuCaptureCost()
    {
        if (SkipUnlessOptedIn())
            return;

        using MfPlatform? platform = MfPlatform.TryStart();
        if (platform == null)
        {
            _output.WriteLine("Media Foundation unavailable");
            return;
        }

        var devices = MfCaptureDevice.Enumerate();
        if (devices.Count == 0)
        {
            _output.WriteLine("no MF capture device on this machine");
            return;
        }

        MfCaptureDevice device = devices[0];
        Measurement? gpu = Measure(device, useGpu: true);
        Measurement? cpu = Measure(device, useGpu: false);
        if (gpu == null || cpu == null)
            return;

        _output.WriteLine($"device={device.Name} format={gpu.Format} {gpu.Width}x{gpu.Height}");
        _output.WriteLine($"GPU: gpuActive={gpu.GpuActive} reason='{gpu.Reason}' fps={gpu.Fps:F1} cpu={gpu.CpuPercentOfOneCore:F1}% cpuPerFrame={gpu.CpuMsPerFrame:F3} ms");
        _output.WriteLine($"CPU: gpuActive={cpu.GpuActive} reason='{cpu.Reason}' fps={cpu.Fps:F1} cpu={cpu.CpuPercentOfOneCore:F1}% cpuPerFrame={cpu.CpuMsPerFrame:F3} ms");
        _output.WriteLine($"GPU stages: read={gpu.Stages.ReadMs:F3} convert={gpu.Stages.ConvertMs:F3} publish={gpu.Stages.PublishMs:F3} ms/frame over {gpu.Stages.Frames} frames");
        _output.WriteLine($"CPU stages: read={cpu.Stages.ReadMs:F3} convert={cpu.Stages.ConvertMs:F3} publish={cpu.Stages.PublishMs:F3} ms/frame over {cpu.Stages.Frames} frames");

        if (gpu.CpuMsPerFrame > 0)
        {
            _output.WriteLine(
                $"saving per frame: {cpu.CpuMsPerFrame - gpu.CpuMsPerFrame:F3} ms "
                    + $"({cpu.CpuMsPerFrame / gpu.CpuMsPerFrame:F2}x)");
        }
    }

    private Measurement? Measure(MfCaptureDevice device, bool useGpu)
    {
        using var source = new MfCameraSource(device.SymbolicLink, 1920, 1080, 30);
        if (!useGpu)
            source.DisableGpuConversionUpFront("实测对照：强制 CPU");

        int frames = 0;
        source.FrameReady += (_, args) =>
        {
            Interlocked.Increment(ref frames);
            args.Frame?.Dispose();
        };

        if (!source.Start())
        {
            _output.WriteLine($"start failed (useGpu={useGpu}): {source.LastStartFailure}");
            return null;
        }

        // 丢掉开头一段：首帧要等设备曝光收敛，着色器也要首次编译。
        Thread.Sleep(1200);
        Interlocked.Exchange(ref frames, 0);
        source.ResetStageTimings();

        Process self = Process.GetCurrentProcess();
        TimeSpan cpuBefore = self.TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        Thread.Sleep(MeasureMs);
        stopwatch.Stop();
        TimeSpan cpuAfter = self.TotalProcessorTime;

        // 必须在 Stop 之前读：Stop 会释放转换器，之后 UsesGpuConversion 一律是 false。
        var stages = source.StageTimings;
        bool gpuActive = source.UsesGpuConversion;
        string reason = source.GpuDisableReason;
        string format = source.ActualFormat;
        int width = source.ActualWidth;
        int height = source.ActualHeight;
        source.Stop();

        double wallMs = stopwatch.Elapsed.TotalMilliseconds;
        double cpuMs = (cpuAfter - cpuBefore).TotalMilliseconds;
        int counted = Volatile.Read(ref frames);
        return new Measurement(
            gpuActive,
            reason,
            format,
            width,
            height,
            counted / (wallMs / 1000.0),
            cpuMs / wallMs * 100,
            counted > 0 ? cpuMs / counted : 0,
            stages);
    }

    private sealed record Measurement(
        bool GpuActive,
        string Reason,
        string Format,
        int Width,
        int Height,
        double Fps,
        double CpuPercentOfOneCore,
        double CpuMsPerFrame,
        (double ReadMs, double ConvertMs, double PublishMs, long Frames) Stages);
}
