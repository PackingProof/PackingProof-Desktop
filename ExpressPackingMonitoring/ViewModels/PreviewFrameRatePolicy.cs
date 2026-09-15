namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 预览发布节奏。预览只是给人看的画面：每发布一帧都要把整帧克隆一份、再在 UI 线程
    /// 写进 WriteableBitmap（1080p 一帧约 6MB 的拷贝），长时间没人动鼠标时没必要按满帧率
    /// 跑。降下来省的是实打实的 CPU 与内存带宽；录像管线完全不受影响，始终按录制帧率走。
    /// </summary>
    internal static class PreviewFrameRatePolicy
    {
        /// <summary>有人操作时的预览上限（12fps）。</summary>
        internal static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(1000.0 / 12.0);

        /// <summary>长时间无人操作时的预览上限（4fps）：占用降到三分之一，画面仍在动。</summary>
        internal static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(1000.0 / 4.0);

        /// <summary>多久没有鼠标/键盘/扫码操作算"没人看"。</summary>
        internal static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(60);

        internal static TimeSpan ResolveInterval(TimeSpan sinceLastActivity) =>
            sinceLastActivity >= IdleAfter ? IdleInterval : ActiveInterval;
    }
}
