using System.Diagnostics;
using System.Runtime.InteropServices;
using ExpressPackingMonitoring.Services.Gpu;
using ExpressPackingMonitoring.Services.MediaFoundation;
using OpenCvSharp;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// GPU 与 CPU 两条路径在"解码 + 缩放到预览尺寸"上的成本对照。
///
/// 这是决定 GPU 接入哪一段的依据。整条链路有五遍全帧 CPU 遍历
/// （系统 CSC、YUV 解码、色度校正、预览缩放、写预览位图），
/// 实测真实程序 1080p@60 吃 2.15 个核心。
///
/// GPU 能一次 draw 完成解码+校正+缩放，但结果在显存；要喂给现有的
/// Mat 契约就得回读。关键问题是：回读一张**预览尺寸的小图**（如 640x360）
/// 是否比在 CPU 上做全帧解码+缩放更便宜。这组用例回答它。
/// </summary>
public sealed class GpuVsCpuPreviewPathTests
{
    private readonly ITestOutputHelper _output;

    public GpuVsCpuPreviewPathTests(ITestOutputHelper output) => _output = output;

    private const int SourceWidth = 1920;
    private const int SourceHeight = 1080;
    private const int PreviewWidth = 640;
    private const int PreviewHeight = 360;

    /// <summary>
    /// GPU（渲染到预览尺寸 + 回读小图）必须比 CPU（全帧解码 + 全帧缩放）更便宜。
    ///
    /// 两边做的事完全等价：都从 YUY2 原始帧出发，都产出预览尺寸的 BGR 数据。
    /// </summary>
    [Fact]
    public void GpuPreviewPathBeatsCpuPreviewPath()
    {
        using GpuFrameConverter? converter = GpuFrameConverter.TryCreate(
            SourceWidth,
            SourceHeight,
            PreviewWidth,
            PreviewHeight,
            isNv12: false);
        Assert.NotNull(converter ?? throw new Xunit.Sdk.XunitException(
            $"GPU 转换器创建失败：{GpuFrameConverter.LastCreateFailure}"));

        byte[] frame = CreateYuy2Frame();
        using var fullFrame = new Mat(SourceHeight, SourceWidth, MatType.CV_8UC3);
        using var previewFrame = new Mat(PreviewHeight, PreviewWidth, MatType.CV_8UC3);
        using ID3D11Texture2D staging = CreateStagingTexture(converter);

        GCHandle handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            IntPtr source = handle.AddrOfPinnedObject();
            const int stride = SourceWidth * 2;

            // 预热两条路径。
            for (int index = 0; index < 5; index++)
            {
                RunGpuPath(converter, staging, source, stride, previewFrame);
                RunCpuPath(source, stride, fullFrame, previewFrame);
            }

            const int iterations = 30;
            var stopwatch = Stopwatch.StartNew();
            for (int index = 0; index < iterations; index++)
                RunGpuPath(converter, staging, source, stride, previewFrame);
            double gpuMs = stopwatch.Elapsed.TotalMilliseconds / iterations;

            stopwatch.Restart();
            for (int index = 0; index < iterations; index++)
                RunCpuPath(source, stride, fullFrame, previewFrame);
            double cpuMs = stopwatch.Elapsed.TotalMilliseconds / iterations;

            _output.WriteLine($"GPU {gpuMs:F3} ms/frame, CPU {cpuMs:F3} ms/frame, ratio {cpuMs / gpuMs:F2}x");
            Assert.True(
                gpuMs < cpuMs,
                $"GPU 路径 {gpuMs:F3} ms/帧，CPU 路径 {cpuMs:F3} ms/帧，GPU 没有更便宜");
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// 全分辨率下的对照：GPU 渲染到 1920x1080 再整帧回读，对比 CPU 整帧解码。
    ///
    /// 这一组决定"能不能原地替换 <c>MfCameraSource.ConvertLockedBuffer</c>"。
    /// 采集对外交出的是全分辨率 Mat（录像要用），所以最省事的接入方式是
    /// 在那里把 CPU 解码换成 GPU 解码 —— 但那就必须整帧回读。
    ///
    /// 用例只记录数字、不断言方向：整帧回读要走 PCIe 把 8MB 拉回内存，
    /// 结果可能比 CPU 解码更贵。贵就说明这条接入方式不成立，
    /// 必须让预览与录像各自取自己需要的尺寸，而不是原地替换。
    /// </summary>
    [Fact]
    public void RecordsFullResolutionReadbackCost()
    {
        using GpuFrameConverter? converter = GpuFrameConverter.TryCreate(
            SourceWidth,
            SourceHeight,
            SourceWidth,
            SourceHeight,
            isNv12: false);
        Assert.NotNull(converter ?? throw new Xunit.Sdk.XunitException(
            $"GPU 转换器创建失败：{GpuFrameConverter.LastCreateFailure}"));

        byte[] frame = CreateYuy2Frame();
        using var gpuFull = new Mat(SourceHeight, SourceWidth, MatType.CV_8UC3);
        using var cpuFull = new Mat(SourceHeight, SourceWidth, MatType.CV_8UC3);
        using ID3D11Texture2D staging = CreateStagingTexture(converter);

        GCHandle handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            IntPtr source = handle.AddrOfPinnedObject();
            const int stride = SourceWidth * 2;

            for (int index = 0; index < 5; index++)
            {
                RunGpuPath(converter, staging, source, stride, gpuFull);
                MfFrameConverter.ConvertYuy2(source, stride, SourceWidth, SourceHeight, cpuFull, useBt709: true);
            }

            const int iterations = 30;
            var stopwatch = Stopwatch.StartNew();
            for (int index = 0; index < iterations; index++)
                RunGpuPath(converter, staging, source, stride, gpuFull);
            double gpuMs = stopwatch.Elapsed.TotalMilliseconds / iterations;

            stopwatch.Restart();
            for (int index = 0; index < iterations; index++)
                MfFrameConverter.ConvertYuy2(source, stride, SourceWidth, SourceHeight, cpuFull, useBt709: true);
            double cpuMs = stopwatch.Elapsed.TotalMilliseconds / iterations;

            _output.WriteLine(
                $"full-res GPU+readback {gpuMs:F3} ms/frame, CPU decode {cpuMs:F3} ms/frame, ratio {cpuMs / gpuMs:F2}x");
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// 两条路径的输出要足够接近，切换时用户不该看到画面跳变。
    ///
    /// 容差放宽到 12/255：CPU 路径本身在饱和色上有损（BT.601 解码再乘校正矩阵，
    /// 纯绿只到 218 而标准是 255），GPU 路径是标准解码，两者在彩色区必然有差异。
    /// 这里用低饱和的渐变图案，主要验证几何对齐与整体亮度一致。
    /// </summary>
    [Fact]
    public void GpuAndCpuOutputsAreVisuallyClose()
    {
        using GpuFrameConverter? converter = GpuFrameConverter.TryCreate(
            SourceWidth,
            SourceHeight,
            PreviewWidth,
            PreviewHeight,
            isNv12: false);
        Assert.NotNull(converter ?? throw new Xunit.Sdk.XunitException(
            $"GPU 转换器创建失败：{GpuFrameConverter.LastCreateFailure}"));

        byte[] frame = CreateLowSaturationFrame();
        using var fullFrame = new Mat(SourceHeight, SourceWidth, MatType.CV_8UC3);
        using var gpuPreview = new Mat(PreviewHeight, PreviewWidth, MatType.CV_8UC3);
        using var cpuPreview = new Mat(PreviewHeight, PreviewWidth, MatType.CV_8UC3);
        using ID3D11Texture2D staging = CreateStagingTexture(converter);

        GCHandle handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            IntPtr source = handle.AddrOfPinnedObject();
            const int stride = SourceWidth * 2;

            RunGpuPath(converter, staging, source, stride, gpuPreview);
            RunCpuPath(source, stride, fullFrame, cpuPreview);

            using Mat difference = new();
            Cv2.Absdiff(gpuPreview, cpuPreview, difference);
            Scalar mean = Cv2.Mean(difference);
            double worst = Math.Max(mean.Val0, Math.Max(mean.Val1, mean.Val2));
            Assert.True(
                worst < 12.0,
                $"两条路径的平均差异 {worst:F1}/255 过大，切换预览路径时画面会跳变");
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>GPU 路径：渲染到预览尺寸后把小图回读成 Mat。</summary>
    private static void RunGpuPath(
        GpuFrameConverter converter,
        ID3D11Texture2D staging,
        IntPtr source,
        int stride,
        Mat destination)
    {
        Assert.True(converter.TryRender(source, stride, useBt709: true));
        ReadBackInto(converter, staging, destination);
    }

    /// <summary>CPU 路径：全帧解码 + 全帧缩放（现在的实际做法）。</summary>
    private static void RunCpuPath(IntPtr source, int stride, Mat fullFrame, Mat destination)
    {
        MfFrameConverter.ConvertYuy2(
            source,
            stride,
            SourceWidth,
            SourceHeight,
            fullFrame,
            useBt709: true);
        Cv2.Resize(fullFrame, destination, new Size(PreviewWidth, PreviewHeight), 0, 0, InterpolationFlags.Area);
    }

    private static ID3D11Texture2D CreateStagingTexture(GpuFrameConverter converter)
    {
        ID3D11Device device = converter.RenderTarget.Device;
        return device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)converter.TargetWidth,
            Height = (uint)converter.TargetHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
    }

    /// <summary>把渲染结果回读成 BGR。BGRA→BGR 的通道丢弃由 OpenCV 做。</summary>
    private static unsafe void ReadBackInto(
        GpuFrameConverter converter,
        ID3D11Texture2D staging,
        Mat destination)
    {
        ID3D11Device device = converter.RenderTarget.Device;
        ID3D11DeviceContext context = device.ImmediateContext;
        context.CopyResource(staging, converter.RenderTarget);

        MappedSubresource mapped = context.Map(staging, 0, MapMode.Read);
        try
        {
            using Mat bgra = Mat.FromPixelData(
                converter.TargetHeight,
                converter.TargetWidth,
                MatType.CV_8UC4,
                mapped.DataPointer,
                (int)mapped.RowPitch);
            Cv2.CvtColor(bgra, destination, ColorConversionCodes.BGRA2BGR);
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private static byte[] CreateYuy2Frame()
    {
        byte[] frame = new byte[SourceWidth * 2 * SourceHeight];
        for (int y = 0; y < SourceHeight; y++)
        {
            int row = y * SourceWidth * 2;
            for (int x = 0; x < SourceWidth * 2; x += 4)
            {
                frame[row + x] = (byte)(16 + (x + y) % 220);
                frame[row + x + 1] = (byte)(64 + y % 128);
                frame[row + x + 2] = (byte)(16 + (x + y + 2) % 220);
                frame[row + x + 3] = (byte)(64 + x % 128);
            }
        }
        return frame;
    }

    /// <summary>低饱和渐变：色度贴近中性，便于比较两条路径的几何与亮度。</summary>
    private static byte[] CreateLowSaturationFrame()
    {
        byte[] frame = new byte[SourceWidth * 2 * SourceHeight];
        for (int y = 0; y < SourceHeight; y++)
        {
            int row = y * SourceWidth * 2;
            byte luma = (byte)(16 + y * 200 / SourceHeight);
            for (int x = 0; x < SourceWidth * 2; x += 4)
            {
                frame[row + x] = luma;
                frame[row + x + 1] = 126;
                frame[row + x + 2] = luma;
                frame[row + x + 3] = 130;
            }
        }
        return frame;
    }
}
