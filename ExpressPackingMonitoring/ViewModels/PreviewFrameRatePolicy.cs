namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 预览帧率：窗口可见时始终跟随摄像头，不以空闲时间或焦点状态降低帧率。
    ///
    /// 唯一的例外是"没有可见预览消费方"（主界面被最小化或隐藏、并且小窗也没显示）：
    /// 这时降到保活帧率而不是完全停。预览链路每帧都要整帧克隆、缩放到显示尺寸、在 UI 线程写位图，
    /// 60fps 降到 2fps 能省掉约 97% 的这部分开销；同时因为 <c>CameraFrameRateGate</c> 会直接
    /// 丢掉不需要的帧，未开启事件预录时连帧格式转换都省了。
    /// 保留这一档也避免了"状态没刷新到就彻底黑屏"的风险：真有人在看，画面只是变慢，不会变灰。
    /// </summary>
    internal static class PreviewFrameRatePolicy
    {
        internal const int FallbackCameraFps = 15;

        /// <summary>没有可见预览消费方时的保活帧率。</summary>
        internal const int KeepAliveFps = 2;

        internal static int ResolveTargetFps(int cameraFps) =>
            Math.Clamp(cameraFps > 0 ? cameraFps : FallbackCameraFps, 1, 120);

        internal static int ResolveTargetFps(int cameraFps, bool hasVisibleConsumer) =>
            hasVisibleConsumer ? ResolveTargetFps(cameraFps) : KeepAliveFps;
    }
}
