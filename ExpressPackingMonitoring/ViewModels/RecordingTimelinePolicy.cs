using System.Diagnostics;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 录像时间轴对齐。
    ///
    /// rawvideo 管道的帧率是固定的：编码器按 -framerate 给每帧生成时间戳，我们喂进去多少帧，
    /// 文件时间轴就是多长。所以只要实际喂帧速度低于声明帧率，录出来的文件就会比真实时间短、
    /// 也就是"快放"——而音频是真实时间，两者越到后面差得越多。
    ///
    /// 现场实测：10.29 秒的真实录制只有 492 帧（8.2 秒文件），即 47.8fps 喂给 60fps 编码器，
    /// 每条录像都快 20% 以上，这就是音画不同步的来源。
    ///
    /// 这里提供按墙钟推算"此刻文件里应该有多少帧"的能力，写入端据此补齐重复帧，
    /// 保证文件时长等于真实时长。
    /// </summary>
    internal static class RecordingTimelinePolicy
    {
        /// <summary>
        /// 单帧最多补几帧。落后太多时不要用重复帧把卡顿摊平（那只会得到一个更长的假文件），
        /// 留一半秒的量给正常抖动，更长的停顿交给录制看门狗处理。
        /// </summary>
        internal static int MaxCatchUpFrames(int fps) => Math.Max(1, fps / 2);

        /// <summary>
        /// 按墙钟算出到此刻为止文件里应该有多少帧。时间轴起点未知或时间未前进时返回 0（不补帧）。
        /// </summary>
        internal static int CalculateExpectedFrameCount(long timelineStartTicks, long nowTicks, int fps)
        {
            if (timelineStartTicks <= 0 || nowTicks <= timelineStartTicks || fps <= 0)
                return 0;

            double seconds = StopwatchTicksToSeconds(nowTicks - timelineStartTicks);
            if (seconds <= 0)
                return 0;

            return (int)Math.Min(int.MaxValue, Math.Floor(seconds * fps));
        }

        /// <summary>需要补几帧：落后为正，不落后补 0，并受单帧上限约束。</summary>
        internal static int CalculateCatchUpFrames(int expectedFrames, int writtenFrames, int fps)
        {
            int behind = expectedFrames - writtenFrames;
            if (behind <= 0 || fps <= 0)
                return 0;

            return Math.Min(behind, MaxCatchUpFrames(fps));
        }

        /// <summary>
        /// 已经写进文件的实时帧数。预录帧属于时间轴最前面那一段，不参与实时段的补齐判断，
        /// 否则开头会凭空多出一段"领先"，补齐要等预录时长跑完才生效。
        /// </summary>
        internal static int CalculateLiveWrittenFrames(long writtenFrames, int preRecordFrames) =>
            (int)Math.Clamp(writtenFrames - Math.Max(0, preRecordFrames), 0, int.MaxValue);

        /// <summary>文件时间轴长度：写入帧数按声明帧率折算。</summary>
        internal static double CalculateFileSeconds(long writtenFrames, int fps) =>
            fps <= 0 ? 0 : writtenFrames / (double)fps;

        /// <summary>真实时间轴长度：预录时长 + 实时段已经过的时间。</summary>
        internal static double CalculateWallSeconds(int preRecordFrames, int fps, long liveElapsedTicks) =>
            (fps <= 0 ? 0 : Math.Max(0, preRecordFrames) / (double)fps)
            + StopwatchTicksToSeconds(liveElapsedTicks);

        /// <summary>把 Stopwatch 计时差换成秒。</summary>
        internal static double StopwatchTicksToSeconds(long ticks) =>
            ticks <= 0 ? 0 : ticks / (double)Stopwatch.Frequency;
    }
}
