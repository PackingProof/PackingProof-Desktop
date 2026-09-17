using ExpressPackingMonitoring.Logging;
using System.Runtime.InteropServices;
using System.Text;

namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>
/// Media Foundation 平台的启动与关停。
///
/// <c>MFStartup</c>/<c>MFShutdown</c> 必须配对，且整个进程只需要一次。
/// 这里用引用计数保证：多个采集源并存、反复启停摄像头时都不会重复启动或提前关停。
/// </summary>
internal sealed class MfPlatform : IDisposable
{
    private static readonly object Sync = new();
    private static int _referenceCount;
    private static bool _startupFailed;

    private bool _disposed;

    private MfPlatform()
    {
    }

    /// <summary>
    /// 启动平台并拿一个引用。返回 null 表示这台机器上 MF 不可用
    /// （被裁剪过的系统镜像、N 版 Windows 缺少媒体功能），调用方应当回退到旧采集路径。
    /// </summary>
    internal static MfPlatform? TryStart()
    {
        lock (Sync)
        {
            if (_startupFailed)
                return null;

            if (_referenceCount == 0)
            {
                int hr = MfInterop.MFStartup(MfInterop.MF_VERSION, MfInterop.MFSTARTUP_NOSOCKET);
                if (hr < 0)
                {
                    // 记一次就够：不可用是稳定状态，不该每次启动摄像头都刷日志。
                    _startupFailed = true;
                    RuntimeLog.Warn(
                        "Camera",
                        $"Media Foundation 不可用（HRESULT=0x{hr:X8}），摄像头将使用旧采集路径");
                    return null;
                }
            }

            _referenceCount++;
            return new MfPlatform();
        }
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            if (--_referenceCount == 0)
            {
                try { MfInterop.MFShutdown(); }
                catch { /* 关停失败不影响退出流程 */ }
            }
        }
    }
}

/// <summary>一台视频采集设备。<c>SymbolicLink</c> 是稳定标识，重新插拔后仍然一致。</summary>
internal sealed record MfCaptureDevice(string Name, string SymbolicLink)
{
    /// <summary>
    /// 枚举系统里的视频采集设备。
    ///
    /// 必须在 <see cref="MfPlatform"/> 存活期间调用。任何一步失败都返回空列表而不抛：
    /// 采集设备枚举失败应当回退到旧路径，不能让摄像头彻底起不来。
    /// </summary>
    internal static IReadOnlyList<MfCaptureDevice> Enumerate()
    {
        IMFAttributes? attributes = null;
        IntPtr deviceArray = IntPtr.Zero;
        int count = 0;
        try
        {
            if (MfInterop.MFCreateAttributes(out attributes, 1) < 0)
                return [];

            Guid sourceTypeKey = MfInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE;
            Guid videoCapture = MfInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID;
            if (attributes.SetGUID(ref sourceTypeKey, ref videoCapture) < 0)
                return [];

            if (MfInterop.MFEnumDeviceSources(attributes, out deviceArray, out count) < 0
                || deviceArray == IntPtr.Zero
                || count <= 0)
            {
                return [];
            }

            var devices = new List<MfCaptureDevice>(count);
            for (int index = 0; index < count; index++)
            {
                IntPtr activatePointer = Marshal.ReadIntPtr(deviceArray, index * IntPtr.Size);
                if (activatePointer == IntPtr.Zero)
                    continue;

                try
                {
                    if (Marshal.GetObjectForIUnknown(activatePointer) is not IMFAttributes activate)
                        continue;

                    string name = ReadString(activate, MfInterop.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
                    string link = ReadString(
                        activate,
                        MfInterop.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
                    if (link.Length > 0)
                        devices.Add(new MfCaptureDevice(name, link));

                    Marshal.ReleaseComObject(activate);
                }
                finally
                {
                    Marshal.Release(activatePointer);
                }
            }
            return devices;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Camera", $"枚举 Media Foundation 采集设备失败：{ex.Message}");
            return [];
        }
        finally
        {
            if (deviceArray != IntPtr.Zero)
                Marshal.FreeCoTaskMem(deviceArray);
            if (attributes != null)
                Marshal.ReleaseComObject(attributes);
        }
    }

    /// <summary>
    /// 读一台设备的原生格式清单。这是判断"能否拿到原始 YUV"的依据：
    /// 清单里出现 YUY2/NV12 就说明摄像头本身就按这些格式输出，
    /// 我们可以直接申请，不必让系统先转成 RGB24。
    /// </summary>
    internal static IReadOnlyList<MfNativeFormat> ReadNativeFormats(MfCaptureDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var formats = new List<MfNativeFormat>();
        IMFMediaSource? source = null;
        IMFSourceReader? reader = null;
        try
        {
            source = MfCaptureSourceFactory.TryCreateSource(device.SymbolicLink);
            if (source == null)
                return [];

            if (MfInterop.MFCreateSourceReaderFromMediaSource(source, null!, out reader) < 0
                || reader == null)
            {
                return [];
            }

            for (uint typeIndex = 0; ; typeIndex++)
            {
                int hr = reader.GetNativeMediaType(
                    MfInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                    typeIndex,
                    out IMFMediaType mediaType);
                if (hr == MfInterop.MF_E_NO_MORE_TYPES || hr < 0 || mediaType == null)
                    break;

                try
                {
                    MfNativeFormat? format = MfMediaTypeReader.Read(mediaType);
                    if (format != null)
                        formats.Add(format);
                }
                finally
                {
                    Marshal.ReleaseComObject(mediaType);
                }
            }
            return formats;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("Camera", $"读取摄像头原生格式失败：{ex.Message}");
            return formats;
        }
        finally
        {
            if (reader != null)
                Marshal.ReleaseComObject(reader);
            if (source != null)
            {
                try { source.Shutdown(); } catch { }
                Marshal.ReleaseComObject(source);
            }
        }
    }

    private static string ReadString(IMFAttributes attributes, Guid key)
    {
        try
        {
            if (attributes.GetStringLength(ref key, out int length) < 0 || length <= 0)
                return "";

            var builder = new StringBuilder(length + 1);
            return attributes.GetString(ref key, builder, builder.Capacity, out _) < 0
                ? ""
                : builder.ToString();
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>摄像头的一个原生输出格式。</summary>
internal sealed record MfNativeFormat(
    Guid Subtype,
    int Width,
    int Height,
    int FrameRateNumerator,
    int FrameRateDenominator)
{
    internal double FrameRate => FrameRateDenominator > 0
        ? (double)FrameRateNumerator / FrameRateDenominator
        : 0;

    /// <summary>是否是我们能直接处理的原始 YUV（不需要解码器）。</summary>
    internal bool IsRawYuv =>
        Subtype == MfInterop.MFVideoFormat_YUY2 || Subtype == MfInterop.MFVideoFormat_NV12;

    internal string SubtypeName =>
        Subtype == MfInterop.MFVideoFormat_YUY2 ? "YUY2"
        : Subtype == MfInterop.MFVideoFormat_NV12 ? "NV12"
        : Subtype == MfInterop.MFVideoFormat_MJPG ? "MJPG"
        : Subtype == MfInterop.MFVideoFormat_RGB24 ? "RGB24"
        : Subtype == MfInterop.MFVideoFormat_RGB32 ? "RGB32"
        : Subtype.ToString();

    public override string ToString() =>
        $"{Width}x{Height}@{FrameRate:F0} {SubtypeName}";
}
