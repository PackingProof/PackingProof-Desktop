namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 预览帧率分档。
    ///
    /// 预览链路每帧都要整帧克隆、缩放到显示尺寸、在 UI 线程写位图；<c>CameraFrameRateGate</c>
    /// 会把不需要的帧直接丢掉，未开启事件预录时连帧格式转换都省了。所以按"有没有人在看"分档：
    ///
    /// - 有人在看且最近有操作（鼠标/键盘/扫码）：满帧，跟采集帧率一致；
    /// - 界面还在显示，但 1 分钟没人操作：降到 15fps（现场打包台常见状态，画面不卡但省资源）；
    /// - 界面还在显示，5 分钟没人操作：降到 4fps；
    /// - 没有可见预览消费方（主界面最小化或隐藏、且小窗也没显示）：降到 2fps 保活。
    ///   保留这一档而不是完全停，是为了避免"状态没刷新到就彻底黑屏"，真有人在看时画面只是变慢。
    ///
    /// 用户主动在设置里关闭实时预览时由 <see cref="PreviewPublishPolicy"/> 完全停掉，不走这里。
    /// </summary>
    internal static class PreviewFrameRatePolicy
    {
        internal const int FallbackCameraFps = 15;

        /// <summary>空闲多久后降到 <see cref="ReducedFps"/>。</summary>
        internal static readonly TimeSpan ReducedAfter = TimeSpan.FromMinutes(1);

        /// <summary>空闲多久后降到 <see cref="LowFps"/>。</summary>
        internal static readonly TimeSpan LowAfter = TimeSpan.FromMinutes(5);

        internal const int ReducedFps = 15;
        internal const int LowFps = 4;

        /// <summary>没有可见预览消费方时的保活帧率。</summary>
        internal const int KeepAliveFps = 2;

        internal static int ResolveTargetFps(int cameraFps) =>
            Math.Clamp(cameraFps > 0 ? cameraFps : FallbackCameraFps, 1, 120);

        internal static int ResolveTargetFps(int cameraFps, bool hasVisibleConsumer, TimeSpan idle)
        {
            if (!hasVisibleConsumer)
                return KeepAliveFps;

            int fullRate = ResolveTargetFps(cameraFps);
            if (idle >= LowAfter)
                return Math.Min(LowFps, fullRate);
            if (idle >= ReducedAfter)
                return Math.Min(ReducedFps, fullRate);
            return fullRate;
        }
    }
}
