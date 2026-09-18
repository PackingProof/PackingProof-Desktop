using System.Diagnostics;
using System.Runtime.InteropServices;
using ExpressPackingMonitoring.Services.Gpu;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// GPU 路径的可行性验证。
///
/// 这一步必须先做：整条 GPU 方案（着色器、渲染目标、与 WPF 共享纹理）是上千行的工程，
/// 而它的收益前提是"上传纹理 + GPU 转换"总成本低于现在的 CPU 转换（约 2ms/帧）。
/// 上传是 CPU 侧的纯内存拷贝，跑不过这一关的话整条路就不成立，
/// 应该趁早知道而不是写完才发现。
/// </summary>
public sealed class D3D11FeasibilityTests
{
    private const int Width = 1920;
    private const int Height = 1080;

    /// <summary>D3D11 设备能否创建。创建不了就整条 GPU 路径不可用，必须能干净回退。</summary>
    [Fact]
    public void CreatesHardwareDeviceOrFallsBackCleanly()
    {
        using D3D11DeviceHolder? holder = D3D11DeviceHolder.TryCreate();
        if (holder == null)
            return; // 没有可用显卡：这台机器走 CPU 路径，不算失败

        Assert.True(holder.IsHardware);
        Assert.True(holder.FeatureLevel >= D3dFeatureLevel.Level_9_3);
    }

    /// <summary>上传纹理能否创建，且 YUY2 的两通道格式可用。</summary>
    [Fact]
    public void CreatesUploadTextureForYuy2()
    {
        using D3D11DeviceHolder? holder = D3D11DeviceHolder.TryCreate();
        if (holder == null)
            return;

        // YUY2 每两像素 4 字节，按 R8G8 上传时宽度就是像素宽。
        ID3D11Texture2D? texture = holder.TryCreateUploadTexture(
            Width,
            Height,
            DxgiFormat.R8G8_UNorm);
        Assert.NotNull(texture);
        try
        {
            texture!.GetDesc(out D3d11Texture2DDescription description);
            Assert.Equal((uint)Width, description.Width);
            Assert.Equal((uint)Height, description.Height);
        }
        finally
        {
            Marshal.ReleaseComObject(texture!);
        }
    }

    /// <summary>
    /// 决定性的一条：把一帧 YUY2 上传到 GPU 的成本。
    ///
    /// 这是 CPU 侧的纯内存拷贝（4MB/帧），GPU 路径省不掉它。
    /// 如果上传本身就接近甚至超过现在 CPU 转换的 2ms/帧，
    /// 那么"搬到 GPU"只是把开销换了个地方，不值得做。
    /// </summary>
    [Fact]
    public void UploadCostIsWorthwhile()
    {
        using D3D11DeviceHolder? holder = D3D11DeviceHolder.TryCreate();
        if (holder == null)
            return;

        ID3D11Texture2D? texture = holder.TryCreateUploadTexture(
            Width,
            Height,
            DxgiFormat.R8G8_UNorm);
        if (texture == null)
            return;

        byte[] frame = new byte[Width * 2 * Height];
        GCHandle handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            IntPtr source = handle.AddrOfPinnedObject();
            int stride = Width * 2;

            for (int index = 0; index < 5; index++)
                holder.TryUpload(texture, source, stride, Height, stride);

            const int iterations = 60;
            var stopwatch = Stopwatch.StartNew();
            for (int index = 0; index < iterations; index++)
            {
                Assert.True(holder.TryUpload(texture, source, stride, Height, stride));
            }
            double perFrameMs = stopwatch.Elapsed.TotalMilliseconds / iterations;

            // 实测本机（4070S，功能级别 11_1）：上传 0.194ms/帧，
            // 而 CPU 直接转换约 2.0ms/帧 —— 约 10 倍差距，GPU 路径的净收益成立。
            // 阈值留到 1.0ms：上传是纯内存拷贝，一旦退化到接近 CPU 转换的成本，
            // 整条 GPU 方案就失去意义，应当被这条用例拦住。
            Assert.True(
                perFrameMs < 1.0,
                $"1080p YUY2 上传耗时 {perFrameMs:F3} ms/帧"
                    + $"（设备 {holder.DriverType}，功能级别 {holder.FeatureLevel}），"
                    + "已接近 CPU 直接转换的 2.0ms，把转换搬到 GPU 得不到净收益");
        }
        finally
        {
            handle.Free();
            Marshal.ReleaseComObject(texture);
        }
    }
}
