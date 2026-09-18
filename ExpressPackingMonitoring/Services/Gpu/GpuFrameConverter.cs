using ExpressPackingMonitoring.Logging;
using OpenCvSharp;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ExpressPackingMonitoring.Services.Gpu;

/// <summary>
/// GPU 帧转换器：把原始 YUY2/NV12 渲染成 BGRA 纹理。
///
/// 一次 draw 同时完成 YUV 解码、BT.709/601 校正、缩放 —— 这三件事现在在 CPU 上
/// 是三次全帧遍历。实测真实程序 1080p@60 空闲预览吃 2.15 个核心，
/// 而 OBS 做同样的事只用 0.11 个：差别就在于它一帧都不让 CPU 摸像素。
///
/// 为什么用 Vortice.Windows（MIT）而不是自己写 COM 互操作：手写那版连续踩了
/// byte[] 封送、SIZE_T 长度类型、vtable 占位对齐三个坑，最后在
/// ID3D11DeviceContext 的 60 多个占位方法某处触发访问违例崩溃。
/// 这段代码跑在录像取证软件的采集线程上，崩溃就是丢录像 ——
/// 把原生互操作交给经过大量项目验证的库，风险收益比明显更合适。
///
/// 任何一步创建失败都返回 null，调用方回退 CPU 转换。
/// </summary>
internal sealed class GpuFrameConverter : IDisposable
{
    /// <summary>常量缓冲要 16 字节对齐，一个 float4 刚好一组。</summary>
    private const int ConstantBufferSize = 16;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Texture2D _renderTarget;
    private readonly ID3D11RenderTargetView _renderTargetView;
    private readonly ID3D11Texture2D _lumaTexture;
    private readonly ID3D11ShaderResourceView _lumaView;
    private readonly ID3D11Texture2D? _chromaTexture;
    private readonly ID3D11ShaderResourceView? _chromaView;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly bool _isNv12;
    private readonly object _sync = new();

    /// <summary>回读用的暂存纹理，首次回读时才建，之后复用。</summary>
    private ID3D11Texture2D? _stagingTexture;
    private bool _disposed;

    private GpuFrameConverter(
        ID3D11Device device,
        ID3D11DeviceContext context,
        ID3D11VertexShader vertexShader,
        ID3D11PixelShader pixelShader,
        ID3D11Buffer constantBuffer,
        ID3D11SamplerState sampler,
        ID3D11Texture2D renderTarget,
        ID3D11RenderTargetView renderTargetView,
        ID3D11Texture2D lumaTexture,
        ID3D11ShaderResourceView lumaView,
        ID3D11Texture2D? chromaTexture,
        ID3D11ShaderResourceView? chromaView,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        bool isNv12,
        FeatureLevel featureLevel)
    {
        _device = device;
        _context = context;
        _vertexShader = vertexShader;
        _pixelShader = pixelShader;
        _constantBuffer = constantBuffer;
        _sampler = sampler;
        _renderTarget = renderTarget;
        _renderTargetView = renderTargetView;
        _lumaTexture = lumaTexture;
        _lumaView = lumaView;
        _chromaTexture = chromaTexture;
        _chromaView = chromaView;
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        TargetWidth = targetWidth;
        TargetHeight = targetHeight;
        _isNv12 = isNv12;
        FeatureLevel = featureLevel;
    }

    internal int TargetWidth { get; }

    internal int TargetHeight { get; }

    internal FeatureLevel FeatureLevel { get; }

    /// <summary>渲染结果纹理（BGRA）。预览与录像都从这里取，不回读到 CPU。</summary>
    internal ID3D11Texture2D RenderTarget => _renderTarget;

    /// <summary>最近一次创建失败的原因，供诊断与日志使用。</summary>
    internal static string LastCreateFailure { get; private set; } = "";

    /// <summary>
    /// 创建转换器。返回 null 表示 GPU 路径不可用，调用方回退 CPU 转换 ——
    /// 远程桌面、精简镜像、驱动崩溃后都可能走到这里，都不是异常情况。
    /// </summary>
    /// <param name="targetWidth">渲染目标宽度。小于源宽即为缩放，由采样器顺带完成。</param>
    internal static GpuFrameConverter? TryCreate(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        bool isNv12)
    {
        LastCreateFailure = "";
        if (sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
        {
            LastCreateFailure = "尺寸非法";
            return null;
        }

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        try
        {
            // 功能级别从高到低：9_3 已经够做 YUV 转换与缩放，老集显也能用上。
            FeatureLevel[] levels =
            [
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0,
                FeatureLevel.Level_9_3,
            ];
            Result result = D3D11.D3D11CreateDevice(
                IntPtr.Zero,
                DriverType.Hardware,
                // BGRA 支持是与 WPF 共享纹理的前提。
                DeviceCreationFlags.BgraSupport,
                levels,
                out device,
                out FeatureLevel featureLevel,
                out context);
            if (result.Failure || device == null || context == null)
            {
                LastCreateFailure = $"创建 D3D11 设备失败 0x{result.Code:X8}";
                RuntimeLog.Info("Camera", LastCreateFailure + "，使用 CPU 转换路径");
                return null;
            }

            GpuFrameConverter? converter = TryBuild(
                device,
                context,
                featureLevel,
                sourceWidth,
                sourceHeight,
                targetWidth,
                targetHeight,
                isNv12);
            if (converter != null)
            {
                RuntimeLog.Info(
                    "Camera",
                    $"GPU 转换就绪：{sourceWidth}x{sourceHeight} -> {targetWidth}x{targetHeight}"
                        + $"，{(isNv12 ? "NV12" : "YUY2")}，功能级别 {featureLevel}");
                return converter;
            }

            context.Dispose();
            device.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            // DllNotFoundException（精简镜像缺 d3d11.dll）也走这里。
            LastCreateFailure = $"{ex.GetType().Name}: {ex.Message}";
            RuntimeLog.Info("Camera", $"创建 GPU 转换器异常，使用 CPU 路径：{ex.Message}");
            try { context?.Dispose(); } catch { }
            try { device?.Dispose(); } catch { }
            return null;
        }
    }

    private static GpuFrameConverter? TryBuild(
        ID3D11Device device,
        ID3D11DeviceContext context,
        FeatureLevel featureLevel,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        bool isNv12)
    {
        // 着色器在运行时编译：很短、只在摄像头启动时编译一次，
        // 预编译要把 fxc 塞进构建流程并管理产物，不值得。
        if (!TryCompile(GpuConversionShaders.VertexShader, "vs_4_0", out byte[]? vertexBytecode)
            || !TryCompile(
                isNv12 ? GpuConversionShaders.Nv12PixelShader : GpuConversionShaders.Yuy2PixelShader,
                "ps_4_0",
                out byte[]? pixelBytecode))
        {
            return null;
        }

        ID3D11VertexShader vertexShader = device.CreateVertexShader(vertexBytecode!);
        ID3D11PixelShader pixelShader = device.CreatePixelShader(pixelBytecode!);

        // 渲染目标：BGRA 与 WPF 的位图格式一致；Shared 让 D3DImage 能拿到这张纹理，
        // 从而省掉预览那次 6MB 位图搬运。
        ID3D11Texture2D renderTarget = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)targetWidth,
            Height = (uint)targetHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.Shared,
        });
        ID3D11RenderTargetView renderTargetView = device.CreateRenderTargetView(renderTarget);

        // 源纹理：YUY2 按 R8G8 上传（一个纹素 = 一个像素的亮度 + 半边色度）；
        // NV12 拆成亮度面与半尺寸色度面，让采样器天然完成色度上采样。
        ID3D11Texture2D lumaTexture = CreateUploadTexture(
            device,
            sourceWidth,
            sourceHeight,
            isNv12 ? Format.R8_UNorm : Format.R8G8_UNorm);
        ID3D11ShaderResourceView lumaView = device.CreateShaderResourceView(lumaTexture);

        ID3D11Texture2D? chromaTexture = null;
        ID3D11ShaderResourceView? chromaView = null;
        if (isNv12)
        {
            chromaTexture = CreateUploadTexture(
                device,
                sourceWidth / 2,
                sourceHeight / 2,
                Format.R8G8_UNorm);
            chromaView = device.CreateShaderResourceView(chromaTexture);
        }

        ID3D11Buffer constantBuffer = device.CreateBuffer(new BufferDescription
        {
            ByteWidth = ConstantBufferSize,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        ID3D11SamplerState sampler = device.CreateSamplerState(new SamplerDescription
        {
            // 缩放与 NV12 色度上采样都要双线性；YUY2 解包用 Load 精确取纹素，不受影响。
            Filter = Vortice.Direct3D11.Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxAnisotropy = 1,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });

        return new GpuFrameConverter(
            device,
            context,
            vertexShader,
            pixelShader,
            constantBuffer,
            sampler,
            renderTarget,
            renderTargetView,
            lumaTexture,
            lumaView,
            chromaTexture,
            chromaView,
            sourceWidth,
            sourceHeight,
            targetWidth,
            targetHeight,
            isNv12,
            featureLevel);
    }

    private static ID3D11Texture2D CreateUploadTexture(
        ID3D11Device device,
        int width,
        int height,
        Format format) =>
        device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            // Dynamic + WriteDiscard：每帧全新内容，不必等 GPU 读完上一帧。
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

    private static bool TryCompile(string source, string profile, out byte[]? bytecode)
    {
        bytecode = null;
        try
        {
            Result result = Compiler.Compile(
                source,
                "main",
                string.Empty,
                profile,
                out Blob? code,
                out Blob? errors);
            using (code)
            using (errors)
            {
                if (result.Failure || code == null)
                {
                    // 编译错误必须带出来：着色器写错时没有它根本无从下手。
                    string message = errors?.AsString()?.Trim() ?? "";
                    LastCreateFailure = $"编译着色器失败（{profile}）0x{result.Code:X8} {message}";
                    RuntimeLog.Warn("Camera", LastCreateFailure);
                    return false;
                }

                bytecode = code.AsBytes();
                return bytecode.Length > 0;
            }
        }
        catch (Exception ex)
        {
            LastCreateFailure = $"编译着色器异常（{profile}）：{ex.Message}";
            RuntimeLog.Warn("Camera", LastCreateFailure);
            return false;
        }
    }

    /// <summary>
    /// 渲染一帧。<paramref name="source"/> 指向原始 YUY2/NV12 数据。
    /// 返回 false 表示这一帧失败，调用方可以对这一帧回退 CPU 转换。
    /// </summary>
    internal bool TryRender(IntPtr source, int sourceStride, bool useBt709)
    {
        if (source == IntPtr.Zero)
            return false;

        lock (_sync)
        {
            if (_disposed)
                return false;

            try
            {
                if (!UploadSource(source, sourceStride))
                    return false;

                UpdateConstants(useBt709);

                _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
                _context.VSSetShader(_vertexShader);
                _context.PSSetShader(_pixelShader);
                _context.PSSetConstantBuffer(0, _constantBuffer);
                _context.PSSetSampler(0, _sampler);
                _context.PSSetShaderResource(0, _lumaView);
                if (_isNv12 && _chromaView != null)
                    _context.PSSetShaderResource(1, _chromaView);

                _context.OMSetRenderTargets(_renderTargetView);
                _context.RSSetViewport(0, 0, TargetWidth, TargetHeight);
                // 四个顶点画两个三角形，铺满整个渲染目标。
                _context.Draw(4, 0);
                return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"GPU 渲染帧失败：{ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// 把最近一次渲染结果回读成 BGR 三通道 <see cref="Mat"/>。
    ///
    /// 回读要把整帧拉回内存，1080p 是 8MB 走 PCIe。本机实测这一步连同渲染
    /// 共 1.02 ms/帧，而 CPU 整帧解码是 2.28 ms/帧，所以即使保留现有的
    /// 全分辨率 Mat 契约，GPU 路径依然便宜一倍多。
    ///
    /// 暂存纹理只建一次并复用：每帧新建一张会把创建开销加进热路径。
    /// </summary>
    internal bool TryReadBackInto(Mat destination)
    {
        if (destination == null || destination.Empty())
            return false;
        if (destination.Rows != TargetHeight || destination.Cols != TargetWidth)
            return false;
        if (destination.Type() != MatType.CV_8UC3)
            return false;

        lock (_sync)
        {
            if (_disposed)
                return false;

            try
            {
                _stagingTexture ??= _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)TargetWidth,
                    Height = (uint)TargetHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    // Staging + CPURead 是唯一能把 GPU 结果映射回内存的组合。
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                });

                _context.CopyResource(_stagingTexture, _renderTarget);
                MappedSubresource mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
                try
                {
                    // 必须按 RowPitch 包装：GPU 的行距通常大于 宽度×4。
                    using Mat bgra = Mat.FromPixelData(
                        TargetHeight,
                        TargetWidth,
                        MatType.CV_8UC4,
                        mapped.DataPointer,
                        (int)mapped.RowPitch);
                    // 只丢弃 Alpha，不进行色彩转换；避免颜色转换的并行调度开销。
                    Cv2.MixChannels([bgra], [destination], [0, 0, 1, 1, 2, 2]);
                }
                finally
                {
                    _context.Unmap(_stagingTexture, 0);
                }
                return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Camera", $"GPU 结果回读失败：{ex.Message}");
                return false;
            }
        }
    }

    private bool UploadSource(IntPtr source, int sourceStride)
    {
        if (!_isNv12)
        {
            // YUY2：每行字节数是宽度乘二。
            return TryUpload(_lumaTexture, source, sourceStride, _sourceHeight, _sourceWidth * 2);
        }

        // NV12：亮度面在前，色度面紧随其后（行数是亮度面的一半）。
        if (!TryUpload(_lumaTexture, source, sourceStride, _sourceHeight, _sourceWidth))
            return false;

        IntPtr chromaSource = source + (nint)((long)sourceStride * _sourceHeight);
        return TryUpload(
            _chromaTexture!,
            chromaSource,
            sourceStride,
            _sourceHeight / 2,
            _sourceWidth);
    }

    /// <summary>
    /// 逐行拷贝而不是整块：GPU 给的行距（RowPitch）通常与源不同，
    /// 整块拷会让画面逐行错位。
    /// </summary>
    private unsafe bool TryUpload(
        ID3D11Texture2D texture,
        IntPtr source,
        int sourceStride,
        int height,
        int bytesPerRow)
    {
        MappedSubresource mapped = _context.Map(texture, 0, Vortice.Direct3D11.MapMode.WriteDiscard);
        if (mapped.DataPointer == IntPtr.Zero)
            return false;

        try
        {
            int copyBytes = Math.Min(bytesPerRow, (int)mapped.RowPitch);
            for (int row = 0; row < height; row++)
            {
                Buffer.MemoryCopy(
                    (void*)(source + (nint)((long)row * sourceStride)),
                    (void*)(mapped.DataPointer + (nint)((long)row * mapped.RowPitch)),
                    mapped.RowPitch,
                    (uint)copyBytes);
            }
            return true;
        }
        finally
        {
            _context.Unmap(texture, 0);
        }
    }

    private unsafe void UpdateConstants(bool useBt709)
    {
        // 着色器需要源尺寸（YUY2 解包要按列判断奇偶）与矩阵选择。
        MappedSubresource mapped = _context.Map(
            _constantBuffer,
            0,
            Vortice.Direct3D11.MapMode.WriteDiscard);
        try
        {
            float* data = (float*)mapped.DataPointer;
            data[0] = _sourceWidth;
            data[1] = _sourceHeight;
            data[2] = useBt709 ? 1f : 0f;
            data[3] = 0f;
        }
        finally
        {
            _context.Unmap(_constantBuffer, 0);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            // 释放顺序：视图先于资源，上下文与设备最后。
            DisposeSafely(_stagingTexture);
            DisposeSafely(_chromaView);
            DisposeSafely(_chromaTexture);
            DisposeSafely(_lumaView);
            DisposeSafely(_lumaTexture);
            DisposeSafely(_renderTargetView);
            DisposeSafely(_renderTarget);
            DisposeSafely(_sampler);
            DisposeSafely(_constantBuffer);
            DisposeSafely(_pixelShader);
            DisposeSafely(_vertexShader);
            DisposeSafely(_context);
            DisposeSafely(_device);
        }
    }

    private static void DisposeSafely(IDisposable? disposable)
    {
        if (disposable == null)
            return;

        try { disposable.Dispose(); } catch { }
    }
}
