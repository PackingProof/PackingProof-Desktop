using System.Diagnostics;

namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>
/// 采集各阶段的耗时统计。
///
/// 存在的理由：接入 GPU 之后实测每帧仍然花掉 12.7 ms CPU 时间，而转换本身
/// 只占 1.0 ms。没有分段数据就只能猜剩下的在哪，而猜错方向会让下一轮优化
/// 完全做空。分段之后才能说清哪一段是我们的代码、哪一段是设备自己的开销。
///
/// 只累加 Stopwatch 计数，不做取平均或滑动窗口：热路径上每帧都要走，
/// 留给读取侧去算。计数用 <see cref="Interlocked"/> 而不是加锁，
/// 采集线程不该为了统计去抢锁。
/// </summary>
public sealed partial class MfCameraSource
{
    private long _readTicks;
    private long _convertTicks;
    private long _publishTicks;
    private long _timedFrames;

    /// <summary>
    /// 分段耗时（毫秒/帧）与统计帧数。
    ///
    /// Read 是 <c>ReadSample</c> 的等待加设备自身开销；Convert 是解码与校正；
    /// Publish 是复制一份给订阅方再触发事件。
    /// </summary>
    internal (double ReadMs, double ConvertMs, double PublishMs, long Frames) StageTimings
    {
        get
        {
            long frames = Interlocked.Read(ref _timedFrames);
            if (frames == 0)
                return (0, 0, 0, 0);

            double perTick = 1000.0 / Stopwatch.Frequency / frames;
            return (
                Interlocked.Read(ref _readTicks) * perTick,
                Interlocked.Read(ref _convertTicks) * perTick,
                Interlocked.Read(ref _publishTicks) * perTick,
                frames);
        }
    }

    /// <summary>清零统计。实测时用来丢掉预热阶段的数据。</summary>
    internal void ResetStageTimings()
    {
        Interlocked.Exchange(ref _readTicks, 0);
        Interlocked.Exchange(ref _convertTicks, 0);
        Interlocked.Exchange(ref _publishTicks, 0);
        Interlocked.Exchange(ref _timedFrames, 0);
    }
}
