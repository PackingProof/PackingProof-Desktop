using System.Runtime.InteropServices;


namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>
/// <c>IMFSourceReaderCallback</c>：SourceReader 的异步回调。
///
/// 走异步而不是自己开线程轮询 <c>ReadSample</c>：同步读会在没有新帧时阻塞，
/// 摄像头掉线时可能一直卡住，而异步回调由 MF 的工作线程驱动，
/// 掉线会以事件形式告知，停止时也能干净退出。
///
/// 实现类必须是 public + <c>[ComVisible(true)]</c> + <c>[ClassInterface(None)]</c>，
/// 否则原生层 QueryInterface 拿不到这个接口，表现为"启动成功但一帧都不来"。
/// </summary>
[ComImport]
[Guid("deec8d99-fa1d-4d82-84c2-2c8969944867")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFSourceReaderCallback
{
    /// <param name="sample">
    /// 可能为 null（只有流标志、没有数据时），所以按 <see cref="IntPtr"/> 接，
    /// 由实现方自己判断并转换 —— 声明成 IMFSample 时 null 会让运行时封送失败。
    /// </param>
    [PreserveSig]
    int OnReadSample(
        int status,
        uint streamIndex,
        int streamFlags,
        long timestamp,
        IntPtr sample);

    [PreserveSig]
    int OnFlush(uint streamIndex);

    [PreserveSig]
    int OnEvent(uint streamIndex, IntPtr mediaEvent);
}

/// <summary>SourceReader 的流标志位，用来识别掉线与格式变化。</summary>
internal static class MfSourceReaderFlags
{
    /// <summary>MF_SOURCE_READERF_ERROR：这条流出错了。</summary>
    internal const int Error = 0x1;

    /// <summary>MF_SOURCE_READERF_ENDOFSTREAM：流结束（摄像头被拔掉时也会走这里）。</summary>
    internal const int EndOfStream = 0x2;

    /// <summary>MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED：格式变了，尺寸要重新读。</summary>
    internal const int CurrentMediaTypeChanged = 0x10;

    /// <summary>MF_SOURCE_READERF_STREAMTICK：只是一个时间戳占位，没有实际数据。</summary>
    internal const int StreamTick = 0x100;
}
