namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>预览始终跟随摄像头，不以空闲时间或焦点状态降低帧率。</summary>
    internal static class PreviewFrameRatePolicy
    {
        internal const int FallbackCameraFps = 15;

        internal static int ResolveTargetFps(int cameraFps) =>
            Math.Clamp(cameraFps > 0 ? cameraFps : FallbackCameraFps, 1, 120);
    }
}
