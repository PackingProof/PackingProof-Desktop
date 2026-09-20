using OpenCvSharp;
using ExpressPackingMonitoring.Services.Gpu;

namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>摄像头原生采样格式。只有它能省掉 NV12→BGR 那一次转换。</summary>
internal enum CameraRawPixelFormat
{
    Nv12 = 0,
    Yuy2 = 1
}

/// <summary>
/// 一帧原始采样的只读视图（NV12 / YUY2）。
///
/// 只在采集回调期间有效：底层就是 MF 那个被锁住的缓冲区，解锁后下一帧会把它覆盖掉。
/// 要留下来必须当场拷走 —— 见 <see cref="CopyToCompactMat"/> 与 PreRecordFrameRing.AddRaw。
///
/// 存在这一路的意义：NV12 是 1.5 字节/像素，BGR24 是 3 字节/像素。预录这种"只存不处理"的
/// 消费者直接留原始采样，内存和拷贝都减半，转换推迟到真正要画面的时候（写入端）。
/// </summary>
internal readonly record struct CameraRawFrame(
    IntPtr Data,
    int Stride,
    int Width,
    int Height,
    CameraRawPixelFormat Format,
    bool UsesBt709)
{
    /// <summary>紧凑存储的行数：NV12 的 UV 平面只有半高。</summary>
    internal int CompactRows => Format == CameraRawPixelFormat.Nv12 ? Height + Height / 2 : Height;

    /// <summary>紧凑存储的元素类型：NV12 逐字节，YUY2 每像素 2 字节。</summary>
    internal MatType CompactType => Format == CameraRawPixelFormat.Nv12 ? MatType.CV_8UC1 : MatType.CV_8UC2;

    /// <summary>紧凑存储的行距（等于宽度 × 每像素字节），与源头的 pitch 无关。</summary>
    internal int CompactStride => Format == CameraRawPixelFormat.Nv12 ? Width : Width * 2;

    internal Mat CreateCompactBuffer() => new(CompactRows, Width, CompactType);

    /// <summary>把这一帧按紧凑布局拷进目标缓冲（复用槽位时就地覆盖写）。</summary>
    internal void CopyTo(Mat compactTarget)
    {
        using Mat wrapped = Mat.FromPixelData(CompactRows, Width, CompactType, Data, Stride);
        wrapped.CopyTo(compactTarget);
    }

    internal Mat CopyToCompactMat()
    {
        Mat compact = CreateCompactBuffer();
        try
        {
            CopyTo(compact);
            return compact;
        }
        catch
        {
            compact.Dispose();
            throw;
        }
    }

    /// <summary>把紧凑缓冲重新当成原始采样视图（写入端解码时用）。</summary>
    internal static CameraRawFrame FromCompactBuffer(Mat buffer, int width, int height, CameraRawPixelFormat format, bool usesBt709)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return new CameraRawFrame(buffer.Data, (int)buffer.Step(), width, height, format, usesBt709);
    }
}

/// <summary>长期保存的原始采样：紧凑缓冲 + 解码需要的元信息。</summary>
internal sealed class PreRecordRawPayload : IDisposable
{
    internal PreRecordRawPayload(Mat buffer, int width, int height, CameraRawPixelFormat format, bool usesBt709)
    {
        Buffer = buffer;
        Width = width;
        Height = height;
        Format = format;
        UsesBt709 = usesBt709;
    }

    internal Mat Buffer { get; }

    internal int Width { get; }

    internal int Height { get; }

    internal CameraRawPixelFormat Format { get; }

    internal bool UsesBt709 { get; }

    internal CameraRawFrame AsView() =>
        CameraRawFrame.FromCompactBuffer(Buffer, Width, Height, Format, UsesBt709);

    public void Dispose() => Buffer.Dispose();
}

/// <summary>
/// 原始采样 → BGR24 的解码器。GPU 可用先走 GPU（一次 draw 连 BT.709 校正一起做完），
/// 失败就这一帧回退 CPU；创建不出 GPU 转换器就永久走 CPU（与采集端同一条策略）。
/// </summary>
internal sealed class CameraRawFrameDecoder : IDisposable
{
    private readonly CameraRawPixelFormat _format;
    private readonly int _width;
    private readonly int _height;
    private GpuFrameConverter? _gpu;
    private bool _gpuUnavailable;

    internal CameraRawFrameDecoder(CameraRawPixelFormat format, int width, int height)
    {
        _format = format;
        _width = width;
        _height = height;
        _gpuUnavailable = GpuFrameConverter.TryCreate(
            width,
            height,
            width,
            height,
            format == CameraRawPixelFormat.Nv12) == null;
    }

    internal bool UsesGpu => !_gpuUnavailable && _gpu != null;

    internal void DecodeToBgr(CameraRawFrame raw, Mat destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Create(raw.Height, raw.Width, MatType.CV_8UC3);

        if (!_gpuUnavailable && raw.Format == _format && raw.Width == _width && raw.Height == _height)
        {
            _gpu ??= GpuFrameConverter.TryCreate(
                _width,
                _height,
                _width,
                _height,
                _format == CameraRawPixelFormat.Nv12);
            if (_gpu == null)
            {
                _gpuUnavailable = true;
            }
            else if (_gpu.TryRender(raw.Data, raw.Stride, raw.UsesBt709)
                && _gpu.TryReadBackInto(destination))
            {
                return;
            }
        }

        if (raw.Format == CameraRawPixelFormat.Nv12)
        {
            MfFrameConverter.ConvertNv12(
                raw.Data, raw.Stride, raw.Width, raw.Height, destination, raw.UsesBt709);
        }
        else
        {
            MfFrameConverter.ConvertYuy2(
                raw.Data, raw.Stride, raw.Width, raw.Height, destination, raw.UsesBt709);
        }
    }

    public void Dispose()
    {
        _gpu?.Dispose();
        _gpu = null;
    }
}
