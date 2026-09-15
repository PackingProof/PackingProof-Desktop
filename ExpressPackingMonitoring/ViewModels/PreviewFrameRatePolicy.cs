namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 预览发布节奏。
    ///
    /// 默认按摄像头自己的帧率满帧跑：用户会拿 OBS 之类的软件跟我们的预览对比，
    /// 预览被限流一眼就能看出"卡"。只有在确实没人在看的时候才逐级降下来，
    /// 省掉每帧整帧克隆与 UI 线程写位图的开销（1080p 一帧约 6MB 拷贝）。
    /// 录像管线完全不受影响，始终按录制帧率走。
    /// </summary>
    internal static class PreviewFrameRatePolicy
    {
        /// <summary>摄像头帧率还没测出来时的兜底值。</summary>
        internal const int FallbackCameraFps = 15;

        /// <summary>满帧（不额外限流）在档位判定里用的标记值：真实帧率不可能等于它。</summary>
        internal const int FullRateFpsMarker = -1;

        /// <summary>没人操作满 60 秒后降到 12fps。</summary>
        internal static readonly TimeSpan ReducedAfter = TimeSpan.FromSeconds(60);
        internal static readonly TimeSpan ReducedInterval = TimeSpan.FromMilliseconds(1000.0 / 12.0);

        /// <summary>继续没人操作满 5 分钟后再降到 4fps：画面还在动，占用已经很低。</summary>
        internal static readonly TimeSpan LowAfter = TimeSpan.FromMinutes(5);
        internal static readonly TimeSpan LowInterval = TimeSpan.FromMilliseconds(1000.0 / 4.0);

        /// <summary>
        /// 当前该用的发布间隔，null 表示不额外限流（跟着摄像头帧率满帧跑）。
        /// 程序自己的窗口在前台时一律满帧：这时用户正在看画面，不能因为"没碰鼠标"降帧。
        /// </summary>
        internal static TimeSpan? ResolveInterval(TimeSpan sinceLastActivity, bool appWindowFocused)
        {
            if (appWindowFocused)
                return null;
            if (sinceLastActivity < ReducedAfter)
                return null;
            return sinceLastActivity < LowAfter ? ReducedInterval : LowInterval;
        }

        /// <summary>
        /// 当前档位对应的处理帧率（摄像头采集门限与处理循环共用，保证预览真的跑在档位上）。
        /// 录制时始终跟摄像头帧率，录像质量不受预览档位影响。
        /// </summary>
        internal static int ResolveTargetFps(int cameraFps, TimeSpan? interval, bool isRecording)
        {
            int camera = cameraFps > 0 ? cameraFps : FallbackCameraFps;
            if (isRecording || interval == null)
                return Math.Clamp(camera, 1, 120);

            double intervalMs = interval.Value.TotalMilliseconds;
            if (intervalMs <= 0)
                return Math.Clamp(camera, 1, 120);

            int tierFps = (int)Math.Round(1000.0 / intervalMs);
            return Math.Clamp(Math.Min(camera, tierFps), 1, 120);
        }
    }
}
