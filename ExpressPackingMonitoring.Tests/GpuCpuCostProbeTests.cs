using System.Diagnostics;
using System.Runtime.InteropServices;
using ExpressPackingMonitoring.Services.Gpu;
using ExpressPackingMonitoring.Services.MediaFoundation;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class GpuCpuCostProbeTests(ITestOutputHelper output)
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);

    [Fact]
    public void ReportsRealCaptureCpuCycles()
    {
        if (Environment.GetEnvironmentVariable("PACKINGPROOF_CAPTURE_PROBE") != "1")
            return;
        using var platform = MfPlatform.TryStart();
        Assert.NotNull(platform);
        var devices = MfCaptureDevice.Enumerate();
        Assert.NotEmpty(devices);
        foreach (bool gpu in new[] { true, false, false, true })
        {
            using var source = new MfCameraSource(devices[0].SymbolicLink, 1920, 1080, 30);
            if (!gpu) source.DisableGpuConversionUpFront("CPU probe");
            int frames = 0;
            source.FrameReady += (_, e) => { e.Frame.Dispose(); Interlocked.Increment(ref frames); };
            Assert.True(source.Start(), source.LastStartFailure);
            Thread.Sleep(2000);
            int framesBefore = Volatile.Read(ref frames);
            using var process = Process.GetCurrentProcess();
            Assert.True(QueryProcessCycleTime(process.Handle, out ulong before));
            var wall = Stopwatch.StartNew();
            Thread.Sleep(6000);
            Assert.True(QueryProcessCycleTime(process.Handle, out ulong after));
            double elapsed = wall.Elapsed.TotalSeconds;
            int count = Volatile.Read(ref frames) - framesBefore;
            Assert.True(count > 0, source.LastFrameFailure);
            output.WriteLine($"gpuRequested={gpu} active={source.UsesGpuConversion} format={source.ActualFormat} size={source.ActualWidth}x{source.ActualHeight} fps={count / elapsed:F2} Mcycles/frame={(after - before) / (count * 1e6):F3}");
            source.Stop();
        }
    }

    [Fact]
    public void ReportsProductionConversionCpuCost()
    {
        if (Environment.GetEnvironmentVariable("PACKINGPROOF_CAPTURE_PROBE") != "1")
            return;

        const int width = 1920, height = 1080;
        using var converter = GpuFrameConverter.TryCreate(width, height, width, height, false);
        Assert.NotNull(converter);
        using var destination = new Mat(height, width, MatType.CV_8UC3);
        byte[] bytes = new byte[width * height * 2];
        Array.Fill(bytes, (byte)128);
        GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            IntPtr source = handle.AddrOfPinnedObject();
            Action gpu = () =>
            {
                Assert.True(converter.TryRender(source, width * 2, true));
                Assert.True(converter.TryReadBackInto(destination));
            };
            Action cpu = () => MfFrameConverter.ConvertYuy2(source, width * 2, width, height, destination, true);
            for (int i = 0; i < 20; i++) { gpu(); cpu(); }
            output.WriteLine($"OpenCV threads={Cv2.GetNumThreads()}");
            foreach (bool paced in new[] { false, true })
            {
                Measure("GPU", gpu, paced);
                Measure("CPU", cpu, paced);
                Measure("CPU", cpu, paced);
                Measure("GPU", gpu, paced);
            }
        }
        finally { handle.Free(); }
    }

    [Fact]
    public void ReportsPreviewResizeCpuCost()
    {
        if (Environment.GetEnvironmentVariable("PACKINGPROOF_CAPTURE_PROBE") != "1")
            return;

        using var source = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(64, 128, 192));
        using var destination = new Mat();
        int originalThreads = Cv2.GetNumThreads();
        using var gpu = GpuFrameConverter.TryCreate(1920, 1080, 1488, 837, false, isBgr24: true);
        Assert.NotNull(gpu);
        using var gpuOutput = new Mat(837, 1488, MatType.CV_8UC3);
        Action gpuResize = () =>
        {
            Assert.True(gpu.TryRender(source.Data, (int)source.Step(), false));
            Assert.True(gpu.TryReadBackInto(gpuOutput));
        };
        for (int i = 0; i < 10; i++) gpuResize();
        Measure("PreviewResize GPU", gpuResize, paced: true);
        try
        {
            foreach (int threads in new[] { originalThreads, 1, originalThreads })
            {
                Cv2.SetNumThreads(threads);
                Action resize = () => Cv2.Resize(source, destination, new Size(1488, 837),
                    interpolation: InterpolationFlags.Area);
                for (int i = 0; i < 10; i++) resize();
                Measure($"PreviewResize threads={threads}", resize, paced: true);
            }
        }
        finally { Cv2.SetNumThreads(originalThreads); }
    }

    private void Measure(string name, Action convert, bool paced)
    {
        using var process = Process.GetCurrentProcess();
        Thread.Sleep(500);
        TimeSpan before = process.TotalProcessorTime;
        Assert.True(QueryProcessCycleTime(process.Handle, out ulong cyclesBefore));
        var wall = Stopwatch.StartNew();
        for (int i = 0; i < 120; i++)
        {
            convert();
            if (paced)
            {
                int delay = (int)((i + 1) * (1000.0 / 30) - wall.Elapsed.TotalMilliseconds);
                if (delay > 0) Thread.Sleep(delay);
            }
        }
        double elapsed = wall.Elapsed.TotalMilliseconds;
        Assert.True(QueryProcessCycleTime(process.Handle, out ulong cyclesAfter));
        process.Refresh();
        double used = (process.TotalProcessorTime - before).TotalMilliseconds;
        output.WriteLine($"{name} paced={paced} wallMs/frame={elapsed / 120:F3} cpuMs/frame={used / 120:F3} Mcycles/frame={(cyclesAfter - cyclesBefore) / 120e6:F3}");
    }
}
