using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.Services.Gpu;

/// <summary>
/// D3D11 与 WPF 互操作所需的原生声明。
///
/// 目标：把 YUV→RGB 与缩放交给 GPU，让 CPU 只负责把原始帧上传纹理。
/// 现在这两件事都在 CPU 上做（约 2ms/帧，60fps 下白吃一个核心的 12%），
/// 而且预览还要再搬一次 6MB 位图给 WPF。
///
/// 所有接口按 vtable 顺序声明，**不能重排、不能省略中间成员** ——
/// 少一个或换位置都会调用到错误的槽位。这条在 MF 那边已经踩过一次：
/// IMFSample 用 C# 接口继承表达继承关系，导致方法整体错位、返回莫名的 HRESULT。
/// </summary>
internal static class D3D11Interop
{
    internal const uint D3D11_SDK_VERSION = 7;

    /// <summary>D3D11_CREATE_DEVICE_BGRA_SUPPORT：与 WPF 的 D3DImage 共享纹理需要。</summary>
    internal const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    /// <summary>D3D11_CREATE_DEVICE_SINGLETHREADED 不能用：采集线程与 UI 线程都会碰设备。</summary>
    internal const uint D3D11_CREATE_DEVICE_NONE = 0;

    [DllImport("d3d11.dll", ExactSpelling = true)]
    internal static extern int D3D11CreateDevice(
        IntPtr adapter,
        D3dDriverType driverType,
        IntPtr software,
        uint flags,
        [In] D3dFeatureLevel[]? featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out ID3D11Device device,
        out D3dFeatureLevel featureLevel,
        out ID3D11DeviceContext immediateContext);
}

internal enum D3dDriverType
{
    Unknown = 0,
    Hardware = 1,

    /// <summary>参考驱动，极慢，只用于诊断；产品路径不使用。</summary>
    Reference = 2,

    /// <summary>WARP：软件光栅化。比 Reference 快得多，是没有显卡时的可选项。</summary>
    Warp = 5,
}

internal enum D3dFeatureLevel
{
    Level_9_1 = 0x9100,
    Level_9_3 = 0x9300,
    Level_10_0 = 0xa000,
    Level_10_1 = 0xa100,
    Level_11_0 = 0xb000,
    Level_11_1 = 0xb100,
}

internal enum DxgiFormat
{
    Unknown = 0,
    R8G8B8A8_UNorm = 28,
    B8G8R8A8_UNorm = 87,

    /// <summary>单通道 8 位。YUY2 按它上传，采样时自己解包。</summary>
    R8_UNorm = 61,

    /// <summary>双通道 8 位。YUY2 每两字节一组，用它上传更自然。</summary>
    R8G8_UNorm = 49,
}

internal enum D3d11Usage
{
    Default = 0,

    /// <summary>CPU 频繁写、GPU 只读。上传纹理用这个。</summary>
    Dynamic = 2,
}

[Flags]
internal enum D3d11BindFlag : uint
{
    None = 0,
    ShaderResource = 0x8,
    RenderTarget = 0x20,
}

[Flags]
internal enum D3d11CpuAccessFlag : uint
{
    None = 0,
    Write = 0x10000,
}

[Flags]
internal enum D3d11ResourceMiscFlag : uint
{
    None = 0,

    /// <summary>共享句柄：WPF 的 D3DImage 需要通过它拿到纹理。</summary>
    Shared = 0x2,
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3d11Texture2DDescription
{
    internal uint Width;
    internal uint Height;
    internal uint MipLevels;
    internal uint ArraySize;
    internal DxgiFormat Format;
    internal uint SampleCount;
    internal uint SampleQuality;
    internal D3d11Usage Usage;
    internal D3d11BindFlag BindFlags;
    internal D3d11CpuAccessFlag CpuAccessFlags;
    internal D3d11ResourceMiscFlag MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3d11MappedSubresource
{
    internal IntPtr Data;
    internal uint RowPitch;
    internal uint DepthPitch;
}

internal enum D3d11Map
{
    /// <summary>整块丢弃重写。每帧都是全新内容，用它避免等 GPU 读完上一帧。</summary>
    WriteDiscard = 4,
}

[ComImport]
[Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ID3D11Device
{
    // ---- ID3D11Device ----
    [PreserveSig] int CreateBuffer(IntPtr desc, IntPtr initialData, out IntPtr buffer);

    [PreserveSig] int CreateTexture1D(IntPtr desc, IntPtr initialData, out IntPtr texture);

    [PreserveSig] int CreateTexture2D(
        [In] ref D3d11Texture2DDescription desc,
        IntPtr initialData,
        out ID3D11Texture2D texture);

    [PreserveSig] int CreateTexture3D(IntPtr desc, IntPtr initialData, out IntPtr texture);

    [PreserveSig] int CreateShaderResourceView(
        ID3D11Texture2D resource,
        IntPtr desc,
        out IntPtr view);

    [PreserveSig] int CreateUnorderedAccessView(IntPtr resource, IntPtr desc, out IntPtr view);

    [PreserveSig] int CreateRenderTargetView(
        ID3D11Texture2D resource,
        IntPtr desc,
        out IntPtr view);

    [PreserveSig] int CreateDepthStencilView(IntPtr resource, IntPtr desc, out IntPtr view);

    [PreserveSig] int CreateInputLayout(
        IntPtr inputElementDescs,
        uint numElements,
        IntPtr shaderBytecodeWithInputSignature,
        IntPtr bytecodeLength,
        out IntPtr inputLayout);

    [PreserveSig] int CreateVertexShader(
        byte[] shaderBytecode,
        IntPtr bytecodeLength,
        IntPtr classLinkage,
        out IntPtr vertexShader);

    [PreserveSig] int CreateGeometryShader(
        byte[] shaderBytecode,
        IntPtr bytecodeLength,
        IntPtr classLinkage,
        out IntPtr geometryShader);

    [PreserveSig] int CreateGeometryShaderWithStreamOutput(
        byte[] shaderBytecode,
        IntPtr bytecodeLength,
        IntPtr soDeclaration,
        uint numEntries,
        IntPtr bufferStrides,
        uint numStrides,
        uint rasterizedStream,
        IntPtr classLinkage,
        out IntPtr geometryShader);

    [PreserveSig] int CreatePixelShader(
        byte[] shaderBytecode,
        IntPtr bytecodeLength,
        IntPtr classLinkage,
        out IntPtr pixelShader);

    // 其余成员（CreateHullShader 起）产品路径用不到，但必须占位以保持 vtable 对齐。
    [PreserveSig] int CreateHullShader(IntPtr a, IntPtr b, IntPtr c, out IntPtr d);
    [PreserveSig] int CreateDomainShader(IntPtr a, IntPtr b, IntPtr c, out IntPtr d);
    [PreserveSig] int CreateComputeShader(IntPtr a, IntPtr b, IntPtr c, out IntPtr d);
    [PreserveSig] int CreateClassLinkage(out IntPtr linkage);
    [PreserveSig] int CreateBlendState(IntPtr desc, out IntPtr state);
    [PreserveSig] int CreateDepthStencilState(IntPtr desc, out IntPtr state);
    [PreserveSig] int CreateRasterizerState(IntPtr desc, out IntPtr state);
    [PreserveSig] int CreateSamplerState(IntPtr desc, out IntPtr state);
    [PreserveSig] int CreateQuery(IntPtr desc, out IntPtr query);
    [PreserveSig] int CreatePredicate(IntPtr desc, out IntPtr predicate);
    [PreserveSig] int CreateCounter(IntPtr desc, out IntPtr counter);
    [PreserveSig] int CreateDeferredContext(uint flags, out IntPtr context);
    [PreserveSig] int OpenSharedResource([In] IntPtr resource, [In] ref Guid riid, out IntPtr resourceOut);
    [PreserveSig] int CheckFormatSupport(DxgiFormat format, out uint formatSupport);
    [PreserveSig] int CheckMultisampleQualityLevels(DxgiFormat format, uint sampleCount, out uint levels);
    [PreserveSig] void CheckCounterInfo(IntPtr counterInfo);
    [PreserveSig] int CheckCounter(IntPtr desc, out uint type, out uint activeCounters, IntPtr name, IntPtr nameLength, IntPtr units, IntPtr unitsLength, IntPtr description, IntPtr descriptionLength);
    [PreserveSig] int CheckFeatureSupport(uint feature, IntPtr featureSupportData, uint featureSupportDataSize);
    [PreserveSig] int GetPrivateData([In] ref Guid guid, ref uint dataSize, IntPtr data);
    [PreserveSig] int SetPrivateData([In] ref Guid guid, uint dataSize, IntPtr data);
    [PreserveSig] int SetPrivateDataInterface([In] ref Guid guid, IntPtr data);
    [PreserveSig] D3dFeatureLevel GetFeatureLevel();
    [PreserveSig] uint GetCreationFlags();
    [PreserveSig] int GetDeviceRemovedReason();
    [PreserveSig] void GetImmediateContext(out ID3D11DeviceContext context);
    [PreserveSig] int SetExceptionMode(uint raiseFlags);
    [PreserveSig] uint GetExceptionMode();
}

[ComImport]
[Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ID3D11Texture2D
{
    // ---- ID3D11DeviceChild ----
    [PreserveSig] void GetDevice(out IntPtr device);
    [PreserveSig] int GetPrivateData([In] ref Guid guid, ref uint dataSize, IntPtr data);
    [PreserveSig] int SetPrivateData([In] ref Guid guid, uint dataSize, IntPtr data);
    [PreserveSig] int SetPrivateDataInterface([In] ref Guid guid, IntPtr data);

    // ---- ID3D11Resource ----
    [PreserveSig] void GetType(out uint resourceDimension);
    [PreserveSig] void SetEvictionPriority(uint evictionPriority);
    [PreserveSig] uint GetEvictionPriority();

    // ---- ID3D11Texture2D ----
    [PreserveSig] void GetDesc(out D3d11Texture2DDescription desc);
}

[ComImport]
[Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ID3D11DeviceContext
{
    // ---- ID3D11DeviceChild ----
    [PreserveSig] void GetDevice(out IntPtr device);
    [PreserveSig] int GetPrivateData([In] ref Guid guid, ref uint dataSize, IntPtr data);
    [PreserveSig] int SetPrivateData([In] ref Guid guid, uint dataSize, IntPtr data);
    [PreserveSig] int SetPrivateDataInterface([In] ref Guid guid, IntPtr data);

    // ---- ID3D11DeviceContext。产品路径只用到 Map/Unmap/Draw 相关，其余占位 ----
    [PreserveSig] void VSSetConstantBuffers(uint startSlot, uint numBuffers, IntPtr buffers);
    [PreserveSig] void PSSetShaderResources(uint startSlot, uint numViews, [In] IntPtr[] views);
    [PreserveSig] void PSSetShader(IntPtr pixelShader, IntPtr classInstances, uint numClassInstances);
    [PreserveSig] void PSSetSamplers(uint startSlot, uint numSamplers, [In] IntPtr[] samplers);
    [PreserveSig] void VSSetShader(IntPtr vertexShader, IntPtr classInstances, uint numClassInstances);
    [PreserveSig] void DrawIndexed(uint indexCount, uint startIndexLocation, int baseVertexLocation);
    [PreserveSig] void Draw(uint vertexCount, uint startVertexLocation);

    [PreserveSig] int Map(
        ID3D11Texture2D resource,
        uint subresource,
        D3d11Map mapType,
        uint mapFlags,
        out D3d11MappedSubresource mappedResource);

    [PreserveSig] void Unmap(ID3D11Texture2D resource, uint subresource);
}
