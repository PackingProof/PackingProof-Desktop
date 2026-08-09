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
}
