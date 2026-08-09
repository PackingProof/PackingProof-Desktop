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

    /// 取景框识别的间隔：实时约 10 次/秒，标准约 4 次/秒（默认），间歇约 1 次/秒。
    public static TimeSpan GuideIntervalFor(string? speed) => speed switch
    {
        Realtime => TimeSpan.FromMilliseconds(100),
        Intermittent => TimeSpan.FromMilliseconds(1000),
        _ => TimeSpan.FromMilliseconds(250)
    };

    /// 整帧兜底的间隔：与取景框档位按相同节奏缩放，避免间歇档下整帧比框内更频繁。
    public static TimeSpan FullFrameIntervalFor(string? speed) => speed switch
    {
        Realtime => TimeSpan.FromMilliseconds(400),
        Intermittent => TimeSpan.FromMilliseconds(3000),
        _ => TimeSpan.FromMilliseconds(900)
    };
}
