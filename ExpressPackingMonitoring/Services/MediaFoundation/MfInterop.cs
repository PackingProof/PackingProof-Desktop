using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>
/// Media Foundation 的原生互操作声明。
///
/// 为什么需要它：AForge 的 <c>NewFrame</c> 只给 <see cref="System.Drawing.Bitmap"/>，
/// 拿不到摄像头的原始 YUY2 —— 那次 YUV→RGB 由 DirectShow 自动插入的系统转换器完成，
/// 固定按 BT.601 解码，高清源因此发灰偏色（见 <c>CameraColorMatrixPolicy</c>）。
/// MF 的 <c>IMFSourceReader</c> 能直接交出未转换的原始帧，还能读出媒体类型里的
/// 色彩空间标记，于是转换可以由我们按正确系数做一次，而不是"错一次再掰回来"。
///
/// 这里的接口方法顺序就是 vtable 顺序，**不能重排、不能删减、不能只声明用得到的**：
/// 少一个或换位置都会调用到错误的槽位，表现为随机崩溃或返回垃圾数据。
/// 用不到的成员一律保留占位。
/// </summary>
internal static class MfInterop
{
    internal const int MF_VERSION = 0x00020070;
    internal const int MFSTARTUP_NOSOCKET = 1;

    /// <summary>读取第一个视频流，也用作"读任意流"的哨兵。</summary>
    internal const uint MF_SOURCE_READER_FIRST_VIDEO_STREAM = 0xFFFFFFFC;

    internal const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36B4);
    internal const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);

    // ---- 属性 GUID ----
    internal static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    internal static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    internal static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");

    /// <summary>色彩空间标记：能读到时就不必按分辨率猜 BT.601/709。</summary>
    internal static readonly Guid MF_MT_YUV_MATRIX = new("3e23d0ad-6a3a-4b14-a9dc-2b8e1b0d0a0a");
    internal static readonly Guid MF_MT_VIDEO_PRIMARIES = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    internal static readonly Guid MF_MT_TRANSFER_FUNCTION = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    internal static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");

    // ---- 设备枚举 ----
    internal static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE =
        new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    internal static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID =
        new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    internal static readonly Guid MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME =
        new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    internal static readonly Guid MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK =
        new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");

    /// <summary>让 SourceReader 走异步回调，不用我们自己开线程轮询。</summary>
    internal static readonly Guid MF_SOURCE_READER_ASYNC_CALLBACK =
        new("1e3dbeac-bb43-4c35-b507-cd644464c965");

    /// <summary>允许 SourceReader 插入解码器（MJPG 这类压缩格式需要）。</summary>
    internal static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING =
        new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

    // ---- 主类型与子类型 ----
    internal static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");

    /// <summary>YUY2：每像素 2 字节，色度水平抽样。绝大多数 USB 摄像头的原生格式。</summary>
    internal static readonly Guid MFVideoFormat_YUY2 = new("32595559-0000-0010-8000-00aa00389b71");

    /// <summary>NV12：每像素 1.5 字节，亮度面 + 交错色度面。</summary>
    internal static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");

    internal static readonly Guid MFVideoFormat_MJPG = new("47504a4d-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFVideoFormat_RGB24 = new("00000014-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mf.dll", ExactSpelling = true)]
    internal static extern int MFEnumDeviceSources(
        IMFAttributes attributes,
        out IntPtr devices,
        out int count);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    internal static extern int MFCreateSourceReaderFromMediaSource(
        IMFMediaSource mediaSource,
        IMFAttributes attributes,
        out IMFSourceReader sourceReader);
}

[ComImport]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem([In] ref Guid key, IntPtr value);
    [PreserveSig] int GetItemType([In] ref Guid key, out int type);
    [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, out bool result);
    [PreserveSig] int Compare(IMFAttributes attributes, int matchType, out bool result);
    [PreserveSig] int GetUINT32([In] ref Guid key, out int value);
    [PreserveSig] int GetUINT64([In] ref Guid key, out long value);
    [PreserveSig] int GetDouble([In] ref Guid key, out double value);
    [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength([In] ref Guid key, out int length);
    [PreserveSig] int GetString(
        [In] ref Guid key,
        [Out][MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder value,
        int bufferSize,
        out int length);
    [PreserveSig] int GetAllocatedString(
        [In] ref Guid key,
        [MarshalAs(UnmanagedType.LPWStr)] out string value,
        out int length);
    [PreserveSig] int GetBlobSize([In] ref Guid key, out int size);
    [PreserveSig] int GetBlob([In] ref Guid key, byte[] buffer, int bufferSize, out int size);
    [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out int size);
    [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid interfaceId, out IntPtr value);
    [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
    [PreserveSig] int DeleteItem([In] ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32([In] ref Guid key, int value);
    [PreserveSig] int SetUINT64([In] ref Guid key, long value);
    [PreserveSig] int SetDouble([In] ref Guid key, double value);
    [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
    [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob([In] ref Guid key, byte[] buffer, int bufferSize);
    [PreserveSig] int SetUnknown([In] ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
}

/// <summary>
/// 与 <see cref="IMFAttributes"/> 同一个接口，但 <c>SetUnknown</c> 收裸指针。
///
/// 为什么需要它：把托管对象交给 <c>SetUnknown([MarshalAs(IUnknown)] object)</c> 时，
/// 运行时生成的 CCW 暴露的是类接口，原生层 QueryInterface 要
/// IMFSourceReaderCallback 时拿不到，于是回调永远打不进来 ——
/// 表现为格式协商全部成功、一帧都不来、也没有任何错误事件。
/// 用 <c>Marshal.GetComInterfaceForObject</c> 取到确定的接口指针再传进去才可靠。
/// </summary>
[ComImport]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributesWithPointer
{
    [PreserveSig] int GetItem([In] ref Guid key, IntPtr value);
    [PreserveSig] int GetItemType([In] ref Guid key, out int type);
    [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, out bool result);
    [PreserveSig] int Compare(IntPtr attributes, int matchType, out bool result);
    [PreserveSig] int GetUINT32([In] ref Guid key, out int value);
    [PreserveSig] int GetUINT64([In] ref Guid key, out long value);
    [PreserveSig] int GetDouble([In] ref Guid key, out double value);
    [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength([In] ref Guid key, out int length);
    [PreserveSig] int GetString(
        [In] ref Guid key,
        [Out][MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder value,
        int bufferSize,
        out int length);
    [PreserveSig] int GetAllocatedString(
        [In] ref Guid key,
        [MarshalAs(UnmanagedType.LPWStr)] out string value,
        out int length);
    [PreserveSig] int GetBlobSize([In] ref Guid key, out int size);
    [PreserveSig] int GetBlob([In] ref Guid key, byte[] buffer, int bufferSize, out int size);
    [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out int size);
    [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid interfaceId, out IntPtr value);
    [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
    [PreserveSig] int DeleteItem([In] ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32([In] ref Guid key, int value);
    [PreserveSig] int SetUINT64([In] ref Guid key, long value);
    [PreserveSig] int SetDouble([In] ref Guid key, double value);
    [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
    [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob([In] ref Guid key, byte[] buffer, int bufferSize);

    /// <summary>收裸 IUnknown 指针，避免运行时自动生成的 CCW 暴露错误的接口。</summary>
    [PreserveSig] int SetUnknown([In] ref Guid key, IntPtr value);

    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
}

[ComImport]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType : IMFAttributes
{
    [PreserveSig] int GetMajorType(out Guid majorType);
    [PreserveSig] int IsCompressedFormat(out bool compressed);
    [PreserveSig] int IsEqual(IMFMediaType mediaType, out int flags);
    [PreserveSig] int GetRepresentation([In] Guid representation, out IntPtr representationData);
    [PreserveSig] int FreeRepresentation([In] Guid representation, IntPtr representationData);
}

[ComImport]
[Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaSource
{
    [PreserveSig] int GetEvent(int flags, out IntPtr mediaEvent);
    [PreserveSig] int BeginGetEvent(IntPtr callback, IntPtr state);
    [PreserveSig] int EndGetEvent(IntPtr result, out IntPtr mediaEvent);
    [PreserveSig] int QueueEvent(int type, [In] ref Guid extendedType, int status, IntPtr value);
    [PreserveSig] int GetCharacteristics(out int characteristics);
    [PreserveSig] int CreatePresentationDescriptor(out IntPtr presentationDescriptor);
    [PreserveSig] int Start(
        IntPtr presentationDescriptor,
        [In] ref Guid timeFormat,
        IntPtr startPosition);
    [PreserveSig] int Stop();
    [PreserveSig] int Pause();
    [PreserveSig] int Shutdown();
}

[ComImport]
[Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(uint streamIndex, out bool selected);
    [PreserveSig] int SetStreamSelection(uint streamIndex, bool selected);
    [PreserveSig] int GetNativeMediaType(uint streamIndex, uint typeIndex, out IMFMediaType mediaType);
    [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IMFMediaType mediaType);
    [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType mediaType);
    [PreserveSig] int SetCurrentPosition([In] ref Guid timeFormat, IntPtr position);

    /// <summary>
    /// 同步读取一帧。
    ///
    /// 这里刻意用同步读 + 专用读取线程，而不是 <c>MF_SOURCE_READER_ASYNC_CALLBACK</c>
    /// 异步回调：实测把托管回调交给属性存储时（无论用 SetUnknown 的对象封送，
    /// 还是 Marshal.GetComInterfaceForObject 取到的接口指针），原生层都不会回调进来 ——
    /// 格式协商全部成功、一帧都不来、也没有任何错误事件。而同一台设备用同步读
    /// 立刻能读到帧。同步读还更可控：停止时只要让线程退出，不用担心回调在释放后触发。
    /// </summary>
    [PreserveSig] int ReadSample(
        uint streamIndex,
        int controlFlags,
        out uint actualStreamIndex,
        out int streamFlags,
        out long timestamp,
        out IntPtr sample);
    [PreserveSig] int Flush(uint streamIndex);
    [PreserveSig] int GetServiceForStream(
        uint streamIndex,
        [In] ref Guid service,
        [In] ref Guid riid,
        out IntPtr service2);
    [PreserveSig] int GetPresentationAttribute(
        uint streamIndex,
        [In] ref Guid attribute,
        IntPtr value);
}

/// <summary>
/// IMFSample。
///
/// 这里刻意**不用 C# 接口继承**来表达"IMFSample 继承 IMFAttributes"：
/// 运行时对继承链上的 ComImport 接口不会把基接口的方法计入 vtable 偏移，
/// 于是 GetBufferCount 之后的方法全部错位，调用 ConvertToContiguousBuffer
/// 会打到错误的槽位并返回 MF_E_BUFFERTOOSMALL(0xC00D36E6)。
/// 必须把 IMFAttributes 的 33 个方法按顺序原样列在前面。
/// </summary>
[ComImport]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    // ---- IMFAttributes 的 33 个方法，顺序不能动 ----
    [PreserveSig] int GetItem([In] ref Guid key, IntPtr value);
    [PreserveSig] int GetItemType([In] ref Guid key, out int type);
    [PreserveSig] int CompareItem([In] ref Guid key, IntPtr value, out bool result);
    [PreserveSig] int Compare(IntPtr attributes, int matchType, out bool result);
    [PreserveSig] int GetUINT32([In] ref Guid key, out int value);
    [PreserveSig] int GetUINT64([In] ref Guid key, out long value);
    [PreserveSig] int GetDouble([In] ref Guid key, out double value);
    [PreserveSig] int GetGUID([In] ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength([In] ref Guid key, out int length);
    [PreserveSig] int GetString(
        [In] ref Guid key,
        [Out][MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder value,
        int bufferSize,
        out int length);
    [PreserveSig] int GetAllocatedString(
        [In] ref Guid key,
        [MarshalAs(UnmanagedType.LPWStr)] out string value,
        out int length);
    [PreserveSig] int GetBlobSize([In] ref Guid key, out int size);
    [PreserveSig] int GetBlob([In] ref Guid key, byte[] buffer, int bufferSize, out int size);
    [PreserveSig] int GetAllocatedBlob([In] ref Guid key, out IntPtr buffer, out int size);
    [PreserveSig] int GetUnknown([In] ref Guid key, [In] ref Guid interfaceId, out IntPtr value);
    [PreserveSig] int SetItem([In] ref Guid key, IntPtr value);
    [PreserveSig] int DeleteItem([In] ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32([In] ref Guid key, int value);
    [PreserveSig] int SetUINT64([In] ref Guid key, long value);
    [PreserveSig] int SetDouble([In] ref Guid key, double value);
    [PreserveSig] int SetGUID([In] ref Guid key, [In] ref Guid value);
    [PreserveSig] int SetString([In] ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob([In] ref Guid key, byte[] buffer, int bufferSize);
    [PreserveSig] int SetUnknown([In] ref Guid key, IntPtr value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IntPtr destination);

    // ---- IMFSample 自己的方法 ----
    [PreserveSig] int GetSampleFlags(out int flags);
    [PreserveSig] int SetSampleFlags(int flags);
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out int count);
    [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    [PreserveSig] int RemoveBufferByIndex(int index);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out int length);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport]
[Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out int length);
    [PreserveSig] int SetCurrentLength(int length);
    [PreserveSig] int GetMaxLength(out int length);
}

/// <summary>
/// 二维缓冲区：带行边距的帧（NV12 尤其常见）用它拿到真实 stride，
/// 按 width 硬算 stride 会把画面撕开。
/// </summary>
[ComImport]
[Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMF2DBuffer
{
    [PreserveSig] int Lock2D(out IntPtr scanline0, out int pitch);
    [PreserveSig] int Unlock2D();
    [PreserveSig] int GetScanline0AndPitch(out IntPtr scanline0, out int pitch);
    [PreserveSig] int IsContiguousFormat(out bool contiguous);
    [PreserveSig] int GetContiguousLength(out int length);
    [PreserveSig] int ContiguousCopyTo(IntPtr destination, int bufferLength);
    [PreserveSig] int ContiguousCopyFrom(IntPtr source, int bufferLength);
}
