namespace ExpressPackingMonitoring.Config;

/// 摄像头条码识别频率的友好档位。值会持久化到配置，不要随意改动字符串。
public static class CameraBarcodeSpeed
{
    public const string Realtime = "Realtime";
    public const string Standard = "Standard";
    public const string Intermittent = "Intermittent";

    public static bool IsValid(string? value) =>
        string.Equals(value, Realtime, StringComparison.Ordinal)
        || string.Equals(value, Standard, StringComparison.Ordinal)
        || string.Equals(value, Intermittent, StringComparison.Ordinal);

    public static string Normalize(string? value) =>
        IsValid(value) ? value! : Standard;

    /// 取景框识别的间隔：标准固定约 4 次/秒；实时约帧率一半（不低于 10 次/秒）；
    /// 间歇约帧率四分之一（不低于 5 次/秒）。帧率未知时按下限值。
    public static TimeSpan GuideIntervalFor(string? speed) =>
        GuideIntervalFor(speed, frameRate: 0);

    public static TimeSpan GuideIntervalFor(string? speed, double frameRate) => speed switch
    {
        Realtime => TimeSpan.FromMilliseconds(
            frameRate > 0 ? Math.Min(100, 2000.0 / frameRate) : 100),
        Intermittent => TimeSpan.FromMilliseconds(
            frameRate > 0 ? Math.Min(200, 4000.0 / frameRate) : 200),
        _ => TimeSpan.FromMilliseconds(250)
    };

    /// <summary>智能视觉跟随刷新 OBB 的独立节流：最高约 4 FPS，视觉连续性交给界面补帧。</summary>
    public static TimeSpan VisionFollowIntervalFor(double frameRate)
    {
        const int maximumFps = 4;
        double effectiveFps = frameRate > 0
            ? Math.Min(frameRate, maximumFps)
            : maximumFps;
        return TimeSpan.FromMilliseconds(
            1000.0 / Math.Max(1, effectiveFps));
    }

    /// <summary>候选单号出现后，在不超过用户档位的前提下临时切到实时间隔。</summary>
    public static TimeSpan ActiveDecodeInterval(
        TimeSpan configuredInterval,
        TimeSpan realtimeInterval)
        => realtimeInterval > TimeSpan.Zero
           && realtimeInterval < configuredInterval
            ? realtimeInterval
            : configuredInterval;
}
