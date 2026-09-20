using ExpressPackingMonitoring.Config;
using System.Globalization;
using System.Resources;

namespace ExpressPackingMonitoring.Localization;

public static partial class AppLanguage
{
    public const string Auto = "Auto";
    public const string Chinese = "zh-Hans";
    public const string English = "en-US";

    private static readonly ResourceManager Resources =
        new("ExpressPackingMonitoring.Resources.Strings", typeof(AppLanguage).Assembly);

    public static string Current { get; private set; } = Chinese;
    public static bool IsChinese => Current == Chinese;
    public static string StartRecordingText => Get("开始录制");
    public static string StopRecordingText => Get("停止录制");
    /// <summary>识别框锁住时锁图标的提示</summary>
    public static string CameraBarcodeGuideLockedTipText => Get("点击解锁后可拖动调整识别框");
    /// <summary>识别框解锁时锁图标的提示</summary>
    public static string CameraBarcodeGuideUnlockedTipText => Get("可拖动调整识别框，点击锁住");

    public static string NormalizePreference(string? value) => value switch
    {
        Chinese => Chinese,
        English => English,
        Auto => Auto,
        _ => Auto
    };

    public static string Resolve(string? preference, CultureInfo? systemCulture = null)
    {
        string normalized = NormalizePreference(preference);
        if (normalized != Auto) return normalized;
        string language = (systemCulture ?? CultureInfo.InstalledUICulture).TwoLetterISOLanguageName;
        return string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? Chinese : English;
    }

    public static void Initialize(string? preference)
    {
        Current = Resolve(preference);
        var culture = CultureInfo.GetCultureInfo(Current);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    public static string Get(string key) => Get(key, CultureInfo.CurrentUICulture);

    internal static string Get(string key, CultureInfo culture) => Resources.GetString(key, culture) ?? key;

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static string Translate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        string? exact = Resources.GetString(value, CultureInfo.CurrentUICulture);
        if (exact != null) return exact;
        if (IsChinese) return value;

        string translated = value;
        translated = System.Text.RegularExpressions.Regex.Replace(translated, @"(?<=\d)\s*分钟$", Get("分钟"));
        translated = System.Text.RegularExpressions.Regex.Replace(translated, @"(?<=\d)\s*秒$", Get("秒"));
        translated = System.Text.RegularExpressions.Regex.Replace(translated, @"(?<=\d)\s*倍$", Get("倍"));
        translated = System.Text.RegularExpressions.Regex.Replace(translated, @"(?<=\d)\s*位$", Get("位"));
        if (translated.StartsWith("版本 ", StringComparison.Ordinal))
            translated = Get("版本") + translated[2..];
        return translated;
    }

}
