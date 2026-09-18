using ExpressPackingMonitoring.Logging;
using OpenCvSharp;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.Services.MediaFoundation;

internal sealed class MfFrameEventArgs : EventArgs
{
    internal MfFrameEventArgs(Mat frame)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    /// <summary>BGR24 帧。所有权交给订阅方，用完必须 Dispose。</summary>
    internal Mat Frame { get; }
}

internal sealed class MfSourceErrorEventArgs : EventArgs
{
    internal MfSourceErrorEventArgs(string description, bool deviceLost)
    {
        Description = description ?? "";
        DeviceLost = deviceLost;
    }

    internal string Description { get; }

    /// <summary>true 表示设备掉线（被拔掉、被别的程序独占），需要重连而不只是重试。</summary>
    internal bool DeviceLost { get; }
}

/// <summary>
/// 基于 Media Foundation 的摄像头采集源。
///
/// 与 AForge 那条路径的区别在于它**申请摄像头的原生格式**（优先 YUY2/NV12），
/// 自己完成 YUV→BGR，从而：
/// - 不再经由 DirectShow 固定按 BT.601 的系统转换器（高清源发灰的根因）
/// - 少一次 6MB 中间 RGB24 的跨边界搬运和一次 Bitmap.Clone
/// - 能看到设备的全部原生格式（实测同一台摄像头 MF 报 32 种、AForge 只报 1 种）
///
/// 对外契约刻意与 <c>NetworkCameraSource</c> 对齐（<c>FrameReady</c> + <c>Start</c>/<c>Stop</c>
/// + <c>Actual*</c>），这样接入点几乎不用改。
///
/// 线程模型：帧在 MF 的工作线程上到达并在那里完成转换，事件也在那个线程触发，
/// 订阅方需要自己切到 UI 线程 —— 和现有两条采集路径的约定一致。
/// </summary>
public sealed partial class MfCameraSource : IDisposable
{
    private readonly string _symbolicLink;
    private readonly int _targetWidth;
    private readonly int _targetHeight;
    private readonly int _targetFps;
    private readonly string _colorMatrixMode;
    private readonly object _sync = new();

    /// <summary>等读取线程退出的上限。超时只记日志，不阻塞摄像头切换。</summary>
    private static readonly TimeSpan ReadThreadJoinTimeout = TimeSpan.FromSeconds(2);

    private MfPlatform? _platform;
    private IMFMediaSource? _mediaSource;
    private IMFSourceReader? _reader;
    private Thread? _readThread;
    private Mat? _conversionBuffer;
    private Guid _activeSubtype;
    private bool _useBt709;
    private bool _stopping;
    private bool _disposed;
    private int _loggedConversionFailure;
    private int _readCount;
    private int _sampleCount;
    private int _emptyCount;
    private int _lastFlags;
    private int _lastHr;

    internal MfCameraSource(
        string symbolicLink,
        int targetWidth,
        int targetHeight,
        int targetFps,
        string? colorMatrixMode = null)
    {
        _symbolicLink = symbolicLink ?? throw new ArgumentNullException(nameof(symbolicLink));
        _targetWidth = targetWidth;
        _targetHeight = targetHeight;
        _targetFps = targetFps;
        _colorMatrixMode = colorMatrixMode ?? "auto";
    }

    internal event EventHandler<MfFrameEventArgs>? FrameReady;
    internal event EventHandler<MfSourceErrorEventArgs>? SourceError;

    internal int ActualWidth { get; private set; }
    internal int ActualHeight { get; private set; }
    internal double ActualFps { get; private set; }

    /// <summary>实际采用的格式名（YUY2/NV12/RGB24…），用于日志与诊断。</summary>
    internal string ActualFormat { get; private set; } = "";

    /// <summary>是否按 BT.709 解码。现场排查发灰问题时先看这一项。</summary>
    internal bool UsesBt709 => _useBt709;

    internal bool IsRunning
    {
        get
        {
            lock (_sync)
                return _reader != null && !_stopping;
        }
    }

    /// <summary>最近一次启动失败的原因，供诊断与日志使用。</summary>
    internal string LastStartFailure { get; private set; } = "";

    /// <summary>最近一次帧处理失败的原因。采集在跑却没有帧时先看这一项。</summary>
    internal string LastFrameFailure { get; private set; } = "";

    /// <summary>读取循环的诊断计数：调用次数、拿到样本数、空结果数。</summary>
    internal (int Reads, int Samples, int Empty, int LastFlags, int LastHr) ReadStats =>
        (_readCount, _sampleCount, _emptyCount, _lastFlags, _lastHr);

    /// <summary>
    /// 启动采集。返回 false 表示这条路径不可用（MF 缺失、设备被占、格式协商失败），
    /// 调用方应当回退到 AForge 路径 —— 绝不能因为新路径失败就录不了像。
    /// </summary>
    internal bool Start()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                LastStartFailure = "已释放";
                return false;
            }
            if (_reader != null)
                return true;

            LastStartFailure = "";
            _platform = MfPlatform.TryStart();
            if (_platform == null)
            {
                LastStartFailure = "Media Foundation 平台不可用";
                return false;
            }

            try
            {
                if (!TryOpenReader())
                {
                    if (LastStartFailure.Length == 0)
                        LastStartFailure = "打开 SourceReader 失败";
                    ReleaseCore();
                    return false;
                }

                _stopping = false;
                _readThread = new Thread(ReadLoop)
                {
                    IsBackground = true,
                    Name = "MfCameraRead",
                };
                _readThread.Start();
                return true;
            }
            catch (Exception ex)
            {
                LastStartFailure = $"{ex.GetType().Name}: {ex.Message}";
                RuntimeLog.Warn("Camera", $"Media Foundation 采集启动失败：{ex.Message}");
                ReleaseCore();
                return false;
            }
        }
    }

    internal void Stop()
    {
        Thread? readThread;
        lock (_sync)
        {
            if (_reader == null)
                return;

            _stopping = true;
            readThread = _readThread;
            _readThread = null;
        }

        // 在锁外等读取线程退出：它在循环里要拿 _sync，持锁等待就是死锁。
        if (readThread != null && readThread.IsAlive)
        {
            if (!readThread.Join(ReadThreadJoinTimeout))
                RuntimeLog.Warn("Camera", "Media Foundation 读取线程未能在超时内退出");
        }

        lock (_sync)
            ReleaseCore();
    }

    public void Dispose()
    {
        Stop();
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _stopping = true;
            ReleaseCore();
        }
    }

    /// <summary>
    /// 读取循环。用同步 <c>ReadSample</c> 而不是异步回调，原因见 <c>IMFSourceReader.ReadSample</c>
    /// 的注释：托管回调注册不进原生层，协商成功却一帧都不来。
    /// </summary>
    private void ReadLoop()
    {
        while (true)
        {
            IMFSourceReader? reader;
            lock (_sync)
            {
                if (_stopping || _reader == null)
                    return;

                reader = _reader;
            }

            uint actualStreamIndex;
            int streamFlags;
            IntPtr samplePointer;
            int hr;
            try
            {
                Interlocked.Increment(ref _readCount);
                hr = reader.ReadSample(
                    MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                    0,
                    out actualStreamIndex,
                    out streamFlags,
                    out _,
                    out samplePointer);
                _lastHr = hr;
                _lastFlags = streamFlags;
                if (samplePointer != IntPtr.Zero)
                    Interlocked.Increment(ref _sampleCount);
                else
                    Interlocked.Increment(ref _emptyCount);
            }
            catch (Exception ex)
            {
                RaiseError($"读取摄像头帧抛出异常：{ex.Message}", deviceLost: true);
                return;
            }

            if (hr < 0)
            {
                RaiseError($"读取摄像头帧失败：HRESULT=0x{hr:X8}", DeviceLostFrom(hr));
                return;
            }

            try
            {
                if ((streamFlags & MfSourceReaderFlags.Error) != 0)
                {
                    RaiseError("摄像头数据流报错", deviceLost: true);
                    return;
                }

                if ((streamFlags & MfSourceReaderFlags.EndOfStream) != 0)
                {
                    // 摄像头被拔掉、或被别的程序抢走时走这里。
                    RaiseError("摄像头数据流结束", deviceLost: true);
                    return;
                }

                if ((streamFlags & MfSourceReaderFlags.CurrentMediaTypeChanged) != 0)
                    RefreshFormatAfterChange();

                if (samplePointer != IntPtr.Zero
                    && (streamFlags & MfSourceReaderFlags.StreamTick) == 0)
                {
                    ConvertAndPublish(samplePointer);
                }
            }
            finally
            {
                if (samplePointer != IntPtr.Zero)
                    Marshal.Release(samplePointer);
            }
        }
    }

    private bool TryOpenReader()
    {
        _mediaSource = MfCaptureSourceFactory.TryCreateSource(_symbolicLink);
        if (_mediaSource == null)
        {
            LastStartFailure = "打不开采集设备";
            RuntimeLog.Warn("Camera", "Media Foundation 打不开采集设备，回退旧采集路径");
            return false;
        }

        // 不传任何属性：实测带上 MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING 时，
        // 这台设备格式协商全部成功却一帧都不来（那个属性会让 SourceReader 插入
        // 视频处理器，某些设备在处理器链路上不出帧）。我们自己完成 YUV→BGR，
        // 本来就不需要它替我们转换。
        int hr = MfInterop.MFCreateSourceReaderFromMediaSource(
            _mediaSource,
            null!,
            out IMFSourceReader reader);
        if (hr < 0)
        {
            LastStartFailure = $"创建 SourceReader 失败 0x{hr:X8}";
            RuntimeLog.Warn("Camera", $"创建 SourceReader 失败：HRESULT=0x{hr:X8}");
            return false;
        }

        _reader = reader;
        return TryNegotiateFormat();
    }

    /// <summary>
    /// 选一个原生格式并设为当前格式。
    ///
    /// 只从设备**原生清单**里选，不凭空构造媒体类型：构造出来的类型如果设备不支持，
    /// SetCurrentMediaType 会成功但之后读不到帧，表现为"摄像头黑屏且没有报错"。
    /// </summary>
    private bool TryNegotiateFormat()
    {
        IMFSourceReader reader = _reader!;
        var formats = new List<(MfNativeFormat Format, uint TypeIndex)>();
        for (uint typeIndex = 0; ; typeIndex++)
        {
            int enumerateResult = reader.GetNativeMediaType(
                MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                typeIndex,
                out IMFMediaType candidate);
            if (enumerateResult < 0 || candidate == null)
                break;

            try
            {
                MfNativeFormat? parsed = MfMediaTypeReader.Read(candidate);
                if (parsed != null)
                    formats.Add((parsed, typeIndex));
            }
            finally
            {
                Marshal.ReleaseComObject(candidate);
            }
        }

        if (formats.Count == 0)
        {
            LastStartFailure = "读不到原生格式";
            RuntimeLog.Warn("Camera", "Media Foundation 读不到摄像头原生格式，回退旧采集路径");
            return false;
        }

        MfNativeFormat? chosen = MfFormatSelector.Select(
            formats.Select(entry => entry.Format).ToArray(),
            _targetWidth,
            _targetHeight,
            _targetFps);
        if (chosen == null)
        {
            LastStartFailure = "没有可用的原生格式";
            RuntimeLog.Warn("Camera", "摄像头没有可用的原生格式，回退旧采集路径");
            return false;
        }

        uint chosenIndex = formats.First(entry => ReferenceEquals(entry.Format, chosen)).TypeIndex;
        int hr = reader.GetNativeMediaType(
            MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
            chosenIndex,
            out IMFMediaType selected);
        if (hr < 0 || selected == null)
        {
            LastStartFailure = $"取回所选格式失败 0x{hr:X8}";
            RuntimeLog.Warn("Camera", $"重新取回所选格式失败：HRESULT=0x{hr:X8}");
            return false;
        }

        try
        {
            hr = reader.SetCurrentMediaType(
                MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                IntPtr.Zero,
                selected);
            if (hr < 0)
            {
                RuntimeLog.Warn(
                    "Camera",
                    $"摄像头拒绝格式 {chosen}：HRESULT=0x{hr:X8}，改用设备默认格式");
                LastStartFailure = $"设备拒绝格式 {chosen} (0x{hr:X8})";
            }

            // 无论设置成功与否，都回读"当前真实格式"再据此建缓冲区。
            // 不能直接用我们选中的那个：某些设备（尤其虚拟摄像头）接受了 SetCurrentMediaType
            // 却仍按自己的格式出帧，按我们以为的尺寸去解就会错位甚至读越界。
            return TryApplyCurrentFormat(reader);
        }
        finally
        {
            Marshal.ReleaseComObject(selected);
        }
    }

    /// <summary>回读 SourceReader 的当前格式并据此准备缓冲区。</summary>
    private bool TryApplyCurrentFormat(IMFSourceReader reader)
    {
        // 明确选中视频流：SetCurrentMediaType 之后某些设备会把流置为未选中，
        // 此时 ReadSample 会一直返回没有样本的空结果（既不报错也不给帧）。
        int selectResult = reader.SetStreamSelection(
            MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
            true);
        if (selectResult < 0)
        {
            RuntimeLog.Warn("Camera", $"选中视频流失败：HRESULT=0x{selectResult:X8}");
        }

        int hr = reader.GetCurrentMediaType(
            MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
            out IMFMediaType current);
        if (hr < 0 || current == null)
        {
            LastStartFailure = $"读不到当前格式 0x{hr:X8}";
            return false;
        }

        try
        {
            MfNativeFormat? format = MfMediaTypeReader.Read(current);
            if (format == null)
            {
                LastStartFailure = "当前格式无法解析";
                return false;
            }

            MfColorInfo colorInfo = MfMediaTypeReader.ReadColorInfo(current);
            ApplyNegotiatedFormat(format, colorInfo);
            LastStartFailure = "";
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(current);
        }
    }

    private void ApplyNegotiatedFormat(MfNativeFormat format, MfColorInfo colorInfo)
    {
        ActualWidth = format.Width;
        ActualHeight = format.Height;
        ActualFps = format.FrameRate;
        ActualFormat = format.SubtypeName;
        _activeSubtype = format.Subtype;

        // 优先信任设备声明的色彩空间；没有声明时才按分辨率推断（旧路径只能猜）。
        bool? declared = colorInfo.DeclaresBt709;
        _useBt709 = declared
            ?? CameraColorSpacePolicy.InferBt709(_colorMatrixMode, format.Width);

        _conversionBuffer?.Dispose();
        _conversionBuffer = new Mat(format.Height, format.Width, MatType.CV_8UC3);

        SetUpGpuConversion(format);

        RuntimeLog.Info(
            "Camera",
            $"Media Foundation 采集：{format}，bt709={_useBt709}"
                + $"（{(declared.HasValue ? "设备声明" : "按分辨率推断")}），色彩信息：{colorInfo}");
    }


    private void ConvertAndPublish(IntPtr samplePointer)
    {
        // 用显式 QueryInterface 取 IMFSample，而不是 GetObjectForIUnknown + as：
        // IMFSample 在声明上继承 IMFAttributes，运行时按继承链做类型转换会失败，
        // 于是这里静默返回、一帧都发不出去（实测样本明明已经拿到 85 个）。
        Guid sampleId = typeof(IMFSample).GUID;
        if (Marshal.QueryInterface(samplePointer, ref sampleId, out IntPtr samplePointerTyped) < 0
            || samplePointerTyped == IntPtr.Zero)
        {
            LastFrameFailure = "样本不支持 IMFSample";
            return;
        }

        IMFSample sample;
        try
        {
            sample = (IMFSample)Marshal.GetTypedObjectForIUnknown(
                samplePointerTyped,
                typeof(IMFSample));
        }
        catch (Exception ex)
        {
            LastFrameFailure = $"取 IMFSample 失败：{ex.Message}";
            Marshal.Release(samplePointerTyped);
            return;
        }

        IMFMediaBuffer? buffer = null;
        bool locked2D = false;
        bool locked = false;
        IMF2DBuffer? buffer2D = null;
        try
        {
            int contiguousResult = sample.ConvertToContiguousBuffer(out buffer);
            if (contiguousResult < 0 || buffer == null)
            {
                LastFrameFailure = $"取连续缓冲区失败 0x{contiguousResult:X8}";
                return;
            }

            IntPtr scanline0;
            int pitch;
            // 优先用 2D 锁：它给出真实行距，按 width 硬算 stride 会把带边距的帧撕开。
            buffer2D = buffer as IMF2DBuffer;
            if (buffer2D != null && buffer2D.Lock2D(out scanline0, out pitch) >= 0)
            {
                locked2D = true;
            }
            else if (buffer.Lock(out scanline0, out _, out _) >= 0)
            {
                locked = true;
                pitch = DefaultStrideFor(_activeSubtype, ActualWidth);
            }
            else
            {
                LastFrameFailure = "锁定缓冲区失败";
                return;
            }

            Mat? output = ConvertLockedBuffer(scanline0, pitch);
            if (output != null)
                FrameReady?.Invoke(this, new MfFrameEventArgs(output));
            else
                LastFrameFailure = "转换返回空";
        }
        catch (Exception ex)
        {
            // 单帧转换失败不该让采集停掉，但也不能无声无息：只记一次。
            LastFrameFailure = $"{ex.GetType().Name}: {ex.Message}";
            if (Interlocked.Exchange(ref _loggedConversionFailure, 1) == 0)
                RuntimeLog.Warn("Camera", $"Media Foundation 帧转换失败（只提示一次）：{ex.Message}");
        }
        finally
        {
            if (locked2D && buffer2D != null)
            {
                try { buffer2D.Unlock2D(); } catch { }
            }
            if (locked && buffer != null)
            {
                try { buffer.Unlock(); } catch { }
            }
            if (buffer != null)
                Marshal.ReleaseComObject(buffer);
            Marshal.ReleaseComObject(sample);
            Marshal.Release(samplePointerTyped);
        }
    }

    private Mat? ConvertLockedBuffer(IntPtr scanline0, int pitch)
    {
        Mat buffer;
        int width;
        int height;
        bool useBt709;
        Guid subtype;
        lock (_sync)
        {
            if (_conversionBuffer == null || _stopping)
                return null;

            buffer = _conversionBuffer;
            width = ActualWidth;
            height = ActualHeight;
            useBt709 = _useBt709;
            subtype = _activeSubtype;
        }

        // GPU 可用时优先走它：一次 draw 就把解码与 BT.709 校正做完，
        // 本机实测连同整帧回读 1.02 ms/帧，CPU 单是解码就要 2.28 ms/帧。
        // 失败则本帧立即回退 CPU，并永久停用 GPU（见 DisableGpuConversion）。
        if (TryConvertOnGpu(scanline0, pitch, buffer, useBt709, subtype))
            return buffer.Clone();

        if (subtype == MfInterop.MFVideoFormat_YUY2)
        {
            MfFrameConverter.ConvertYuy2(scanline0, pitch, width, height, buffer, useBt709);
        }
        else if (subtype == MfInterop.MFVideoFormat_NV12)
        {
            MfFrameConverter.ConvertNv12(scanline0, pitch, width, height, buffer, useBt709);
        }
        else if (subtype == MfInterop.MFVideoFormat_RGB24)
        {
            // 系统已经转好（按 BT.601），只补色度校正，与旧路径等价。
            using Mat wrapped = Mat.FromPixelData(height, width, MatType.CV_8UC3, scanline0, pitch);
            wrapped.CopyTo(buffer);
            if (useBt709)
                CameraColorSpacePolicy.ApplyBt709Correction(buffer);
        }
        else if (subtype == MfInterop.MFVideoFormat_RGB32)
        {
            using Mat wrapped = Mat.FromPixelData(height, width, MatType.CV_8UC4, scanline0, pitch);
            Cv2.CvtColor(wrapped, buffer, ColorConversionCodes.BGRA2BGR);
            if (useBt709)
                CameraColorSpacePolicy.ApplyBt709Correction(buffer);
        }
        else
        {
            return null;
        }

        // 交给订阅方一份独立副本：共享的转换缓冲区下一帧就会被覆写。
        return buffer.Clone();
    }

    /// <summary>格式在运行中变了（部分摄像头切曝光模式时会）：重新读尺寸并重建缓冲区。</summary>
    private void RefreshFormatAfterChange()
    {
        lock (_sync)
        {
            IMFSourceReader? reader = _reader;
            if (reader == null)
                return;

            if (reader.GetCurrentMediaType(
                    MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                    out IMFMediaType current) < 0
                || current == null)
            {
                return;
            }

            try
            {
                MfNativeFormat? format = MfMediaTypeReader.Read(current);
                if (format == null)
                    return;

                MfColorInfo colorInfo = MfMediaTypeReader.ReadColorInfo(current);
                ApplyNegotiatedFormat(format, colorInfo);
            }
            finally
            {
                Marshal.ReleaseComObject(current);
            }
        }
    }

    private void RaiseError(string description, bool deviceLost)
    {
        RuntimeLog.Warn("Camera", $"Media Foundation 采集错误：{description}, deviceLost={deviceLost}");
        SourceError?.Invoke(this, new MfSourceErrorEventArgs(description, deviceLost));
    }

    /// <summary>MF_E_HW_MFT_FAILURE / MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED 等按掉线处理。</summary>
    private static bool DeviceLostFrom(int hresult) => hresult switch
    {
        unchecked((int)0xC00D3E85) => true, // MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED
        unchecked((int)0xC00D36B3) => true, // MF_E_INVALIDSTREAMNUMBER（设备已消失）
        unchecked((int)0x8007001F) => true, // ERROR_GEN_FAILURE
        _ => false,
    };

    /// <summary>拿不到真实行距时的默认值。只在 2D 锁不可用时使用。</summary>
    private static int DefaultStrideFor(Guid subtype, int width)
    {
        if (subtype == MfInterop.MFVideoFormat_YUY2)
            return width * 2;
        if (subtype == MfInterop.MFVideoFormat_NV12)
            return width;
        if (subtype == MfInterop.MFVideoFormat_RGB32)
            return width * 4;
        return width * 3;
    }

    /// <summary>释放原生资源。必须在持有 <see cref="_sync"/> 时调用。</summary>
    private void ReleaseCore()
    {
        if (_reader != null)
        {
            try { Marshal.ReleaseComObject(_reader); } catch { }
            _reader = null;
        }

        if (_mediaSource != null)
        {
            try { _mediaSource.Shutdown(); } catch { }
            try { Marshal.ReleaseComObject(_mediaSource); } catch { }
            _mediaSource = null;
        }

        _gpuConverter?.Dispose();
        _gpuConverter = null;
        _conversionBuffer?.Dispose();
        _conversionBuffer = null;
        _platform?.Dispose();
        _platform = null;
    }

}
