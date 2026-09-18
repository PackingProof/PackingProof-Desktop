using ExpressPackingMonitoring.Logging;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.Services.Gpu;

/// <summary>
/// D3D11 设备的创建与能力探测。
///
/// 目标是把 YUV→RGB 与缩放搬到 GPU：现在这两件事在 CPU 上约 2ms/帧，
/// 60fps 下白吃一个核心的 12%，而且预览还要再把 6MB 位图搬给 WPF。
///
/// 三档回退，覆盖"有独显 / 只有集显 / 完全没有 GPU"三种现场：
/// 1. 硬件驱动（独显或集显）
/// 2. WARP 软件光栅化 —— 仍然比我们自己逐像素快，但不一定比 OpenCV SIMD 快，
///    所以只在硬件不可用时才考虑，且要实测过才启用
/// 3. 都不行就返回 null，调用方走纯 CPU 路径
///
/// 创建失败不是异常情况：远程桌面会话、精简版系统镜像、显卡驱动崩溃后
/// 都可能拿不到设备，必须干净回退。
/// </summary>
internal sealed class D3D11DeviceHolder : IDisposable
{
    private static readonly D3dFeatureLevel[] FeatureLevels =
    [
        // 从高到低尝试。9_3 已经够做 YUV 转换与缩放，这样老集显也能用上。
        D3dFeatureLevel.Level_11_1,
        D3dFeatureLevel.Level_11_0,
        D3dFeatureLevel.Level_10_1,
        D3dFeatureLevel.Level_10_0,
        D3dFeatureLevel.Level_9_3,
    ];

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private bool _disposed;

    private D3D11DeviceHolder(
        ID3D11Device device,
        ID3D11DeviceContext context,
        D3dFeatureLevel featureLevel,
        D3dDriverType driverType)
    {
        _device = device;
        _context = context;
        FeatureLevel = featureLevel;
        DriverType = driverType;
    }

    internal ID3D11Device Device =>
        _device ?? throw new ObjectDisposedException(nameof(D3D11DeviceHolder));

    internal ID3D11DeviceContext Context =>
        _context ?? throw new ObjectDisposedException(nameof(D3D11DeviceHolder));

    internal D3dFeatureLevel FeatureLevel { get; }

    internal D3dDriverType DriverType { get; }

    /// <summary>是否跑在真实显卡上。WARP 是软件光栅化，收益要另行实测。</summary>
    internal bool IsHardware => DriverType == D3dDriverType.Hardware;

    /// <summary>
    /// 创建设备。返回 null 表示这台机器上 GPU 路径不可用，调用方走纯 CPU 路径。
    /// </summary>
    /// <param name="allowWarp">
    /// 硬件不可用时是否退到 WARP 软件光栅化。默认不允许：
    /// WARP 未必比 OpenCV 的 SIMD 快，没实测过就不该启用。
    /// </param>
    internal static D3D11DeviceHolder? TryCreate(bool allowWarp = false)
    {
        D3D11DeviceHolder? hardware = TryCreateFor(D3dDriverType.Hardware);
        if (hardware != null)
            return hardware;

        if (!allowWarp)
        {
            RuntimeLog.Info("Camera", "没有可用的硬件 D3D11 设备，使用 CPU 转换路径");
            return null;
        }

        return TryCreateFor(D3dDriverType.Warp);
    }

    private static D3D11DeviceHolder? TryCreateFor(D3dDriverType driverType)
    {
        try
        {
            int hr = D3D11Interop.D3D11CreateDevice(
                IntPtr.Zero,
                driverType,
                IntPtr.Zero,
                // BGRA 支持是与 WPF 共享纹理的前提。
                D3D11Interop.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                FeatureLevels,
                (uint)FeatureLevels.Length,
                D3D11Interop.D3D11_SDK_VERSION,
                out ID3D11Device device,
                out D3dFeatureLevel featureLevel,
                out ID3D11DeviceContext context);
            if (hr < 0 || device == null || context == null)
            {
                RuntimeLog.Info(
                    "Camera",
                    $"创建 D3D11 设备失败（{driverType}）：HRESULT=0x{hr:X8}");
                return null;
            }

            RuntimeLog.Info(
                "Camera",
                $"D3D11 设备就绪：{driverType}，功能级别 {featureLevel}");
            return new D3D11DeviceHolder(device, context, featureLevel, driverType);
        }
        catch (Exception ex)
        {
            // DllNotFoundException（精简镜像缺 d3d11.dll）也走这里。
            RuntimeLog.Info("Camera", $"创建 D3D11 设备异常（{driverType}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 建一个可由 CPU 每帧写入、GPU 采样的纹理。原始帧就上传到这里。
    /// 返回 null 表示建不出来，调用方应回退 CPU 路径。
    /// </summary>
    internal ID3D11Texture2D? TryCreateUploadTexture(int width, int height, DxgiFormat format)
    {
        if (width <= 0 || height <= 0)
            return null;

        var description = new D3d11Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleCount = 1,
            SampleQuality = 0,
            // Dynamic + WriteDiscard：每帧全新内容，不必等 GPU 读完上一帧。
            Usage = D3d11Usage.Dynamic,
            BindFlags = D3d11BindFlag.ShaderResource,
            CpuAccessFlags = D3d11CpuAccessFlag.Write,
            MiscFlags = D3d11ResourceMiscFlag.None,
        };

        try
        {
            int hr = Device.CreateTexture2D(ref description, IntPtr.Zero, out ID3D11Texture2D texture);
            if (hr < 0 || texture == null)
            {
                RuntimeLog.Info("Camera", $"创建上传纹理失败：HRESULT=0x{hr:X8}");
                return null;
            }
            return texture;
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("Camera", $"创建上传纹理异常：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把一帧原始数据上传到纹理。<paramref name="sourceStride"/> 是源行距，
    /// 逐行拷贝而不是整块拷贝 —— GPU 给的行距通常与源不同，整块拷会错位。
    /// </summary>
    internal unsafe bool TryUpload(
        ID3D11Texture2D texture,
        IntPtr source,
        int sourceStride,
        int height,
        int bytesPerRow)
    {
        if (texture == null || source == IntPtr.Zero || height <= 0 || bytesPerRow <= 0)
            return false;

        try
        {
            int hr = Context.Map(texture, 0, D3d11Map.WriteDiscard, 0, out D3d11MappedSubresource mapped);
            if (hr < 0 || mapped.Data == IntPtr.Zero)
                return false;

            try
            {
                int copyBytes = Math.Min(bytesPerRow, (int)mapped.RowPitch);
                for (int row = 0; row < height; row++)
                {
                    Buffer.MemoryCopy(
                        (void*)(source + (long)row * sourceStride),
                        (void*)(mapped.Data + (long)row * mapped.RowPitch),
                        mapped.RowPitch,
                        (uint)copyBytes);
                }
            }
            finally
            {
                Context.Unmap(texture, 0);
            }
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Info("Camera", $"上传纹理失败：{ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_context != null)
        {
            try { Marshal.ReleaseComObject(_context); } catch { }
            _context = null;
        }
        if (_device != null)
        {
            try { Marshal.ReleaseComObject(_device); } catch { }
            _device = null;
        }
    }
}
