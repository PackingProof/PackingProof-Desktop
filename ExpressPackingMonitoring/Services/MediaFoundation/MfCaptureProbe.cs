using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>
/// 决定一台设备能否走 Media Foundation 采集路径。
///
/// 为什么需要"先试一下"：格式协商成功并不代表能出帧。虚拟摄像头
/// （Iriun、OBS 虚拟摄像头这类）在后端没有画面时会协商成功、却一帧都不给；
/// 实测 Iriun 在手机端未连接时就是 YUY2 1920x1080 协商成功、帧数恒为 0、也没有错误事件。
/// 这种情况必须判定为不可用并回退旧路径，否则用户看到的是永久黑屏。
/// </summary>
internal static class MfCaptureProbe
{
    /// <summary>
    /// 等首帧的时长。虚拟摄像头和某些 USB 摄像头启动较慢，给足时间；
    /// 但这段时间会阻塞摄像头启动流程，所以不能太长。
    /// </summary>
    internal static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(3);

    /// <summary>探测结果。</summary>
    internal sealed record Result(
        bool Usable,
        string Format,
        int Width,
        int Height,
        double Fps,
        bool UsesBt709,
        string Failure)
    {
        internal static Result Unusable(string failure) =>
            new(false, "", 0, 0, 0, false, failure);
    }

    /// <summary>
    /// 试着打开设备并等第一帧。
    ///
    /// 成功后**不保留**这次打开的采集源：探测与正式采集分开，避免探测占着设备
    /// 让正式启动失败。代价是设备要开两次，换来的是判定可靠。
    /// </summary>
    internal static Result Probe(
        string symbolicLink,
        int targetWidth,
        int targetHeight,
        int targetFps,
        string? colorMatrixMode,
        TimeSpan? timeout = null)
    {
        using var source = new MfCameraSource(
            symbolicLink,
            targetWidth,
            targetHeight,
            targetFps,
            colorMatrixMode);
        using var firstFrame = new ManualResetEventSlim(false);
        string? errorDescription = null;

        source.FrameReady += (_, args) =>
        {
            args.Frame.Dispose();
            firstFrame.Set();
        };
        source.SourceError += (_, args) =>
        {
            errorDescription ??= args.Description;
            // 掉线类错误没有必要继续等：立刻结束等待，让调用方回退。
            if (args.DeviceLost)
                firstFrame.Set();
        };

        if (!source.Start())
            return Result.Unusable(source.LastStartFailure);

        try
        {
            bool gotFrame = firstFrame.Wait(timeout ?? FirstFrameTimeout);
            if (!gotFrame)
            {
                RuntimeLog.Info(
                    "Camera",
                    $"Media Foundation 协商成功但没有出帧（{source.ActualFormat} "
                        + $"{source.ActualWidth}x{source.ActualHeight}），判定为不可用");
                return Result.Unusable("协商成功但没有出帧");
            }

            if (errorDescription != null)
                return Result.Unusable(errorDescription);

            return new Result(
                true,
                source.ActualFormat,
                source.ActualWidth,
                source.ActualHeight,
                source.ActualFps,
                source.UsesBt709,
                "");
        }
        finally
        {
            source.Stop();
        }
    }
}
