namespace ExpressPackingMonitoring.UI
{
    /// <summary>悬浮小窗底部状态灯的类型。</summary>
    public enum FloatingPreviewIndicator
    {
        /// <summary>蓝色常亮：摄像头开着，等待扫描单号。</summary>
        Idle,

        /// <summary>黄到蓝呼吸：预录制缓冲正在滚动，等待触发正式录制。</summary>
        PreRecording,

        /// <summary>红色常亮：正在录制。</summary>
        Recording,

        /// <summary>灰色常亮：摄像头已按空闲设置休眠，画面是停的。</summary>
        CameraSleeping
    }

    /// <summary>
    /// 悬浮小窗的状态展示结果。
    /// <see cref="Text"/> 为单号时直接显示，否则是需要交给 AppLanguage 翻译的中文原文。
    /// </summary>
    public readonly record struct FloatingPreviewStatus(
        FloatingPreviewIndicator Indicator,
        string Text,
        bool TextIsOrderId);

    /// <summary>
    /// 把录制状态折算成小窗状态灯与文案。独立成纯函数，便于在没有窗口的情况下回归。
    /// </summary>
    internal static class FloatingPreviewStatusPolicy
    {
        internal const string RecordingWithoutOrderText = "录制中";

        // 条码可能来自摄像头识别，也可能来自扫码枪，所以统一说"扫描单号"而不指定来源。
        // 预录制额外点明缓冲已经在滚，店员才知道这一单不会漏掉开头。
        internal const string PreRecordingText = "预录制中，等待扫描单号";
        internal const string IdleText = "等待扫描单号";
        /// <summary>摄像头休眠时画面是停的，必须说明原因，否则看起来像程序卡死。</summary>
        internal const string CameraSleepingText = "摄像头已休眠，扫码或点击唤醒";

        /// <summary>
        /// 正在录制时优先展示单号；单号为空则退回"录制中"，避免状态灯亮着却没有任何说明。
        /// 预录制的开关是 EnableEventRecordingBuffer：PreRecordSeconds 会被配置规范化清零，
        /// 用它判断会让预录制灯永远不亮。缓冲要真的有帧才提示，否则等同待机。
        /// 摄像头休眠时预览不再刷新，这时给灰灯加说明，比继续显示"等待扫描单号"更接近事实。
        /// </summary>
        public static FloatingPreviewStatus Evaluate(
            bool isRecording,
            bool cameraSleeping,
            bool preRecordEnabled,
            bool preRecordHasFrames,
            string? orderId)
        {
            if (isRecording)
            {
                string trimmedOrderId = (orderId ?? string.Empty).Trim();
                return trimmedOrderId.Length > 0
                    ? new FloatingPreviewStatus(FloatingPreviewIndicator.Recording, trimmedOrderId, true)
                    : new FloatingPreviewStatus(FloatingPreviewIndicator.Recording, RecordingWithoutOrderText, false);
            }

            if (cameraSleeping)
                return new FloatingPreviewStatus(FloatingPreviewIndicator.CameraSleeping, CameraSleepingText, false);

            if (preRecordEnabled && preRecordHasFrames)
                return new FloatingPreviewStatus(FloatingPreviewIndicator.PreRecording, PreRecordingText, false);

            return new FloatingPreviewStatus(FloatingPreviewIndicator.Idle, IdleText, false);
        }
    }
}
