using System.Runtime.InteropServices;
using ExpressPackingMonitoring.Services.Gpu;
using OpenCvSharp;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// GPU 转换的端到端验证：上传原始帧 → 渲染 → 回读，检查颜色。
///
/// 这是整条 GPU 路径唯一的真验证。"绑定顺序对不对、YUY2 解包索引算没算错、
/// 渲染目标有没有真写进去"只有把结果读回来才知道 —— 而这些恰好是最容易错、
/// 错了只表现为画面偏色的地方。
///
/// 回读原本只打算用于测试，实测后改成了产品路径的一部分：整帧回读加渲染
/// 是 1.02 ms/帧，而 CPU 整帧解码要 2.28 ms/帧，所以保留"全分辨率 Mat"
/// 这一对外契约仍然划得来（见 GpuVsCpuPreviewPathTests）。
/// 更省的走法（D3DImage 共享纹理预览、硬件编码器直取显存）是后续的事。
/// </summary>
public sealed class GpuFrameConverterTests
{
    /// <summary>
    /// 纯色 YUY2 帧渲染后必须得到标准 BT.709 的 RGB。
    ///
    /// 三组输入是 BT.709 的纯红/纯绿/纯蓝，标准解码值已在 GpuShaderColorMathTests
    /// 里核对过；这里验证整条管线（上传、绑定、采样、解包、渲染）真的算对了。
    /// </summary>
    [Theory]
    // Y, U, V, 期望 R, G, B
    [InlineData(63, 102, 240, 255, 0, 0)]
    [InlineData(173, 42, 26, 0, 255, 0)]
    [InlineData(32, 240, 118, 0, 0, 255)]
    [InlineData(126, 128, 128, 128, 128, 128)]
    public void RendersYuy2ToStandardColor(
        byte y,
        byte u,
        byte v,
        int expectedR,
        int expectedG,
        int expectedB)
    {
        const int width = 64;
        const int height = 32;
        using GpuFrameConverter? converter = GpuFrameConverter.TryCreate(
            width,
            height,
            width,
            height,
            isNv12: false);
        Assert.NotNull(converter ?? throw new Xunit.Sdk.XunitException($"创建失败：{GpuFrameConverter.LastCreateFailure}"));

        byte[] frame = CreateUniformYuy2(width, height, y, u, v);
        GCHandle handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            Assert.True(
                converter.TryRender(handle.AddrOfPinnedObject(), width * 2, useBt709: true),
                "渲染调用失败");

            // 取画面中心，避开边缘钳制的影响。
            (byte r, byte g, byte b) = ReadPixel(converter, width / 2, height / 2);
            AssertClose(r, expectedR, "R");
            AssertClose(g, expectedG, "G");
            AssertClose(b, expectedB, "B");
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>缩放要真生效，且不因缩放而偏色。</summary>
    [Fact]
    public void ScalesWhileKeepingColor()
    {
        const int sourceWidth = 128;
        const int sourceHeight = 64;
        const int targetWidth = 32;
        const int targetHeight = 16;
        using GpuFrameConverter? converter = GpuFrameConverter.TryCreate(
            sourceWidth,
            sourceHeight,
            targetWidth,
            targetHeight,
            isNv12: false);
        if (converter == null)
            return;

        Assert.Equal(targetWidth, converter.TargetWidth);
        Assert.Equal(targetHeight, converter.TargetHeight);

        byte[] frame = CreateUniformYuy2(sourceWidth, sourceHeight, 126, 128, 128);
        GCHandle handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            Assert.True(converter.TryRender(handle.AddrOfPinnedObject(), sourceWidth * 2, useBt709: true));

            // 中性灰缩放后依然是同一个灰，不该染上颜色。
            (byte r, byte g, byte b) = ReadPixel(converter, targetWidth / 2, targetHeight / 2);
            AssertClose(r, 128, "R");
            AssertClose(g, 128, "G");
            AssertClose(b, 128, "B");
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// BT.709 与 BT.601 必须给出不同结果，否则说明常量缓冲没生效、
    /// 着色器一直在用同一套系数。
    /// </summary>
    [Fact]
    public void ColorMatrixSelectionTakesEffect()
    {
        const int width = 32;
        const int height = 16;
        using GpuFrameConverter? converter = GpuFrameConverter.TryCreate(
            width, height, width, height, isNv12: false);
        if (converter == null)
            return;

        // 饱和红：两种矩阵下差别最明显。
        byte[] frame = CreateUniformYuy2(width, height, 63, 102, 240);
        GCHandle handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            IntPtr source = handle.AddrOfPinnedObject();

            Assert.True(converter.TryRender(source, width * 2, useBt709: true));
            (byte r709, byte g709, byte b709) = ReadPixel(converter, width / 2, height / 2);

            Assert.True(converter.TryRender(source, width * 2, useBt709: false));
            (byte r601, byte g601, byte b601) = ReadPixel(converter, width / 2, height / 2);

            int difference = Math.Abs(r709 - r601) + Math.Abs(g709 - g601) + Math.Abs(b709 - b601);
            Assert.True(
                difference > 5,
                $"两种矩阵的渲染结果几乎一样（差 {difference}），常量缓冲可能没生效");
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>反复渲染不能泄漏或崩溃：实际路径每秒 60 次。</summary>
    [Fact]
    public void SurvivesRepeatedRendering()
    {
        const int width = 640;
        const int height = 360;
        using GpuFrameConverter? converter = GpuFrameConverter.TryCreate(
            width, height, width, height, isNv12: false);
        if (converter == null)
            return;

        byte[] frame = CreateUniformYuy2(width, height, 126, 128, 128);
        GCHandle handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            IntPtr source = handle.AddrOfPinnedObject();
            for (int index = 0; index < 180; index++)
                Assert.True(converter.TryRender(source, width * 2, useBt709: true));
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>NV12 也要能转，中性色度同样产生灰。</summary>
    [Fact]
    public void ConvertsNv12()
    {
        const int width = 64;
        const int height = 32;
        using GpuFrameConverter? converter = GpuFrameConverter.TryCreate(
            width, height, width, height, isNv12: true);
        if (converter == null)
            return;

        // 亮度面全 126，色度面全 128（中性）。
        byte[] frame = new byte[width * height * 3 / 2];
        for (int index = 0; index < width * height; index++)
            frame[index] = 126;
        for (int index = width * height; index < frame.Length; index++)
            frame[index] = 128;

        GCHandle handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            Assert.True(converter.TryRender(handle.AddrOfPinnedObject(), width, useBt709: true));

            (byte r, byte g, byte b) = ReadPixel(converter, width / 2, height / 2);
            AssertClose(r, 128, "R");
            AssertClose(g, 128, "G");
            AssertClose(b, 128, "B");
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// 生产用的回读要把整帧正确搬进 BGR 的 <c>Mat</c>。
    ///
    /// 采集路径就是靠这个方法把 GPU 结果喂给现有的全分辨率 Mat 契约，
    /// 所以它错了不会崩、只会让录像和预览整体偏色 —— 必须按值核对。
    /// </summary>
    [Fact]
    public void ReadsBackWholeFrameAsBgr()
    {
        const int width = 64;
        const int height = 32;
        using GpuFrameConverter? converter = GpuFrameConverter.TryCreate(
            width, height, width, height, isNv12: false);
        Assert.NotNull(converter ?? throw new Xunit.Sdk.XunitException($"创建失败：{GpuFrameConverter.LastCreateFailure}"));

        // BT.709 纯红。
        byte[] frame = CreateUniformYuy2(width, height, 63, 102, 240);
        using var destination = new Mat(height, width, MatType.CV_8UC3);

        GCHandle handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            Assert.True(converter.TryRender(handle.AddrOfPinnedObject(), width * 2, useBt709: true));
            Assert.True(converter.TryReadBackInto(destination), "回读失败");
        }
        finally
        {
            handle.Free();
        }

        // 每个像素都要是红的：只查中心点的话，行距算错会整帧错位而测试照样过。
        Cv2.MinMaxLoc(destination.ExtractChannel(0), out double minB, out double maxB);
        Cv2.MinMaxLoc(destination.ExtractChannel(1), out double minG, out double maxG);
        Cv2.MinMaxLoc(destination.ExtractChannel(2), out double minR, out double maxR);
        Assert.True(maxB <= 4, $"B 通道最大 {maxB}，纯红不该有蓝");
        Assert.True(maxG <= 4, $"G 通道最大 {maxG}，纯红不该有绿");
        Assert.True(minR >= 251, $"R 通道最小 {minR}，整帧该都是红");
        Assert.True(minB >= 0 && minG >= 0 && maxR <= 255);
    }

    /// <summary>反复回读要复用暂存纹理，不能泄漏或在第二次崩。</summary>
    [Fact]
    public void SurvivesRepeatedReadback()
    {
        const int width = 320;
        const int height = 180;
        using GpuFrameConverter? converter = GpuFrameConverter.TryCreate(
            width, height, width, height, isNv12: false);
        Assert.NotNull(converter ?? throw new Xunit.Sdk.XunitException($"创建失败：{GpuFrameConverter.LastCreateFailure}"));

        byte[] frame = CreateUniformYuy2(width, height, 126, 128, 128);
        using var destination = new Mat(height, width, MatType.CV_8UC3);

        GCHandle handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            IntPtr source = handle.AddrOfPinnedObject();
            for (int index = 0; index < 120; index++)
            {
                Assert.True(converter.TryRender(source, width * 2, useBt709: true));
                Assert.True(converter.TryReadBackInto(destination), $"第 {index + 1} 次回读失败");
            }
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// 目标 Mat 不匹配时必须干净拒绝。
    ///
    /// 采集侧的缓冲区在格式变化时会重建，如果尺寸不符却照写，
    /// 就是往错误大小的内存里拷一整帧。
    /// </summary>
    [Fact]
    public void RejectsMismatchedReadbackTarget()
    {
        const int width = 32;
        const int height = 16;
        using GpuFrameConverter? converter = GpuFrameConverter.TryCreate(
            width, height, width, height, isNv12: false);
        Assert.NotNull(converter ?? throw new Xunit.Sdk.XunitException($"创建失败：{GpuFrameConverter.LastCreateFailure}"));

        using var wrongSize = new Mat(height + 1, width, MatType.CV_8UC3);
        using var wrongType = new Mat(height, width, MatType.CV_8UC4);
        using var empty = new Mat();

        Assert.False(converter.TryReadBackInto(wrongSize));
        Assert.False(converter.TryReadBackInto(wrongType));
        Assert.False(converter.TryReadBackInto(empty));
    }

    /// <summary>非法尺寸要干净失败，不能建出半成品转换器。</summary>
    [Theory]
    [InlineData(0, 16, 16, 16)]
    [InlineData(16, 0, 16, 16)]
    [InlineData(16, 16, 0, 16)]
    [InlineData(16, 16, 16, -1)]
    public void RejectsInvalidSizes(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        Assert.Null(GpuFrameConverter.TryCreate(
            sourceWidth,
            sourceHeight,
            targetWidth,
            targetHeight,
            isNv12: false));
    }

    /// <summary>空指针不能崩，要干净返回失败。</summary>
    [Fact]
    public void RejectsNullSource()
    {
        using GpuFrameConverter? converter = GpuFrameConverter.TryCreate(
            32, 16, 32, 16, isNv12: false);
        if (converter == null)
            return;

        Assert.False(converter.TryRender(IntPtr.Zero, 64, useBt709: true));
    }

    /// <summary>填一整帧同样的 YUY2 像素。</summary>
    private static byte[] CreateUniformYuy2(int width, int height, byte y, byte u, byte v)
    {
        byte[] frame = new byte[width * 2 * height];
        for (int row = 0; row < height; row++)
        {
            int offset = row * width * 2;
            for (int pair = 0; pair < width / 2; pair++)
            {
                frame[offset + pair * 4] = y;
                frame[offset + pair * 4 + 1] = u;
                frame[offset + pair * 4 + 2] = y;
                frame[offset + pair * 4 + 3] = v;
            }
        }
        return frame;
    }

    /// <summary>把渲染目标回读到 CPU 取一个像素（仅测试用）。</summary>
    private static unsafe (byte R, byte G, byte B) ReadPixel(
        GpuFrameConverter converter,
        int x,
        int y)
    {
        ID3D11Texture2D renderTarget = converter.RenderTarget;
        ID3D11Device device = renderTarget.Device;
        // 不要 using 这个上下文：ImmediateContext 是设备共享的单例，
        // 释放它会让后续调用拿到已释放对象并抛 NullReferenceException
        // （第二次回读时才炸，看起来像渲染出了问题）。
        ID3D11DeviceContext context = device.ImmediateContext;

        using ID3D11Texture2D staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)converter.TargetWidth,
            Height = (uint)converter.TargetHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            // Staging + CPURead 是唯一能把 GPU 结果映射到 CPU 的用法。
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        context.CopyResource(staging, renderTarget);
        MappedSubresource mapped = context.Map(staging, 0, MapMode.Read);
        try
        {
            byte* row = (byte*)mapped.DataPointer + (long)y * mapped.RowPitch;
            byte* pixel = row + x * 4;
            // BGRA 顺序。
            return (pixel[2], pixel[1], pixel[0]);
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private static void AssertClose(byte actual, int expected, string channel)
    {
        Assert.True(
            Math.Abs(actual - expected) <= 4,
            $"{channel} 通道：GPU 渲染出 {actual}，期望 {expected}，相差超过 4/255");
    }
}
