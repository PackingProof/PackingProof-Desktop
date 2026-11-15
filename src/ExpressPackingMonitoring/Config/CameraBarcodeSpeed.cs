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

    /// <summary>
    /// OBB 视觉刷新间隔：实时档不设人为频率上限，视觉 worker 按实际推理耗时吃满；
    /// 标准档限制约 4 FPS，间歇档限制约 1 FPS。硬件跟不上时只丢旧帧，不阻塞 ZXing。
    /// </summary>
    public static TimeSpan VisionFollowIntervalFor(double frameRate, string? speed)
    {
        string normalized = Normalize(speed);
        if (string.Equals(normalized, Realtime, StringComparison.Ordinal))
            return TimeSpan.FromMilliseconds(1);
        if (string.Equals(normalized, Intermittent, StringComparison.Ordinal))
            return TimeSpan.FromMilliseconds(1000);
        return TimeSpan.FromMilliseconds(250);
    }

    public static TimeSpan VisionFollowIntervalFor(double frameRate) =>
        VisionFollowIntervalFor(frameRate, Standard);

    /// <summary>候选单号出现后，在不超过用户档位的前提下临时切到实时间隔。</summary>
    public static TimeSpan ActiveDecodeInterval(
        TimeSpan configuredInterval,
        TimeSpan realtimeInterval)
        => realtimeInterval > TimeSpan.Zero
           && realtimeInterval < configuredInterval
            ? realtimeInterval
            : configuredInterval;
}
