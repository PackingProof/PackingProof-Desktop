namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 麦克风启动失败时该怎么处理。
    ///
    /// 现场反馈：开启预录后点"开始录制"就录不了。日志显示预录帧入队之后麦克风端点瞬时不可用，
    /// 而原代码会就此 complete 队列、删掉文件、回滚录制状态 —— 一次音频故障赔掉整段录像。
    /// 视频此时已经在写管道了，音频只是附加轨道，缺音频不该让整段录像失败。
    ///
    /// 例外是实时 AAC 直录：音频是 FFmpeg 的第二个输入，管道迟迟不连接会让编码器一直等，
    /// 这种模式下音频起不来只能取消。
    /// </summary>
    internal static class RecordingAudioStartPolicy
    {
        internal enum AudioStartFailureAction
        {
            /// <summary>稍等再试一次：端点偶发不可用，重试通常就能恢复。</summary>
            RetryOnce,

            /// <summary>继续录像，只是这一单没有声音。</summary>
            ContinueVideoOnly,

            /// <summary>取消本次录制。</summary>
            Abort,
        }

        /// <summary>总共尝试几次。第一次失败先重试，再失败才降级或取消。</summary>
        internal const int AudioStartAttempts = 2;

        /// <summary>重试前的等待时间，给音频服务一点恢复时间。</summary>
        internal const int RetryDelayMs = 500;

        internal static AudioStartFailureAction Decide(bool directAac, int attemptIndex)
        {
            if (attemptIndex + 1 < AudioStartAttempts)
                return AudioStartFailureAction.RetryOnce;

            return directAac
                ? AudioStartFailureAction.Abort
                : AudioStartFailureAction.ContinueVideoOnly;
        }
    }
}
