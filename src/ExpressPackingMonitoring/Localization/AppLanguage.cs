using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.UpdateCore;
using System.Globalization;
using System.Resources;

namespace ExpressPackingMonitoring.Localization;

public static class AppLanguage
{
    public const string Auto = "Auto";
    public const string Chinese = "zh-Hans";
    public const string English = "en-US";
    public const string Japanese = "ja-JP";

    private static readonly ResourceManager Resources =
        new("ExpressPackingMonitoring.Resources.Strings", typeof(AppLanguage).Assembly);

    public static string Current { get; private set; } = Chinese;
    public static bool IsChinese =>
        string.Equals(
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            "zh",
            StringComparison.OrdinalIgnoreCase);
    public static bool IsJapanese => Current == Japanese;
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
        Japanese => Japanese,
        Auto => Auto,
        _ => Auto
    };

    public static string Resolve(string? preference, CultureInfo? systemCulture = null)
    {
        string normalized = NormalizePreference(preference);
        if (normalized != Auto) return normalized;
        string language = (systemCulture ?? CultureInfo.InstalledUICulture).TwoLetterISOLanguageName;
        if (string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase)) return Chinese;
        if (string.Equals(language, "ja", StringComparison.OrdinalIgnoreCase)) return Japanese;
        return English;
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

    public static string Get(string key) => BrandText.Apply(GetRaw(key, CultureInfo.CurrentUICulture));

    internal static string Get(string key, CultureInfo culture) => BrandText.Apply(GetRaw(key, culture));

    private static string GetRaw(string key, CultureInfo culture) =>
        Resources.GetString(key, culture) ?? key;

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static string Translate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        CultureInfo culture = CultureInfo.CurrentUICulture;
        string? exact = Resources.GetString(value, culture);
        if (exact != null) return BrandText.Apply(exact);
        // 中文界面下原文就是中文，直接返回；这里按当前文化判断，避免受全局语言状态影响。
        if (string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase))
            return BrandText.Apply(value);

        PhraseTable table = GetPhraseTable(culture);

        string templated = TranslateTemplate(value, table, culture);
        if (!ReferenceEquals(templated, value)) return BrandText.Apply(templated);

        // “快递单号：SF001”这类“标签＋冒号＋内容”的拼接串，只翻标签，内容保持原样。
        string translated = TranslateLabel(value, table, culture);
        if (!ReferenceEquals(translated, value)) return BrandText.Apply(translated);

        translated = value;
        translated = System.Text.RegularExpressions.Regex.Replace(translated, @"(?<=\d)\s*分钟$", Get("分钟"));
        translated = System.Text.RegularExpressions.Regex.Replace(translated, @"(?<=\d)\s*秒$", Get("秒"));
        translated = System.Text.RegularExpressions.Regex.Replace(translated, @"(?<=\d)\s*倍$", Get("倍"));
        translated = System.Text.RegularExpressions.Regex.Replace(translated, @"(?<=\d)\s*位$", Get("位"));
        if (translated.StartsWith("版本 ", StringComparison.Ordinal))
            translated = Get("版本") + translated[2..];
        return BrandText.Apply(translated);
    }

    /// <summary>
    /// 翻译绑定到界面的运行时字符串：支持整条词条和带参数的完整句式，
    /// 但不去拆“标签：用户数据”，避免把设备名、单号或备注当成界面文案。
    /// </summary>
    public static string TranslateRuntime(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        CultureInfo culture = CultureInfo.CurrentUICulture;
        string? exact = Resources.GetString(value, culture);
        if (exact != null) return BrandText.Apply(exact);
        if (string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase))
            return BrandText.Apply(value);
        return BrandText.Apply(TranslateTemplate(value, GetPhraseTable(culture), culture));
    }

    private sealed record PhraseTable(
        IReadOnlyDictionary<string, string> Labels,
        IReadOnlyList<(string Prefix, System.Text.RegularExpressions.Regex Pattern, string Template)> Templates);

    private static readonly Dictionary<string, PhraseTable> PhraseTables = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object PhraseTableLock = new();

    private static PhraseTable GetPhraseTable(CultureInfo culture)
    {
        lock (PhraseTableLock)
        {
            if (PhraseTables.TryGetValue(culture.Name, out PhraseTable? cached)) return cached;
            PhraseTable built = BuildPhraseTable(culture);
            PhraseTables[culture.Name] = built;
            return built;
        }
    }

    /// <summary>
    /// 从当前语言的资源里取出“中文原文 → 译文”的映射，用于翻译代码里拼出来的中文。
    /// 只收中文词条，模板词条单独编译成正则。
    /// </summary>
    private static PhraseTable BuildPhraseTable(CultureInfo culture)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var templateKeys = new List<KeyValuePair<string, string>>();

        System.Collections.IDictionaryEnumerator? enumerator =
            Resources.GetResourceSet(culture, true, false)?.GetEnumerator();
        if (enumerator == null)
            return new PhraseTable(labels,
                Array.Empty<(string, System.Text.RegularExpressions.Regex, string)>());

        while (enumerator.MoveNext())
        {
            if (enumerator.Key is not string key || enumerator.Value is not string value) continue;
            if (key.Length < 2 || !ContainsCjk(key) || string.Equals(key, value, StringComparison.Ordinal)) continue;
            if (key.Contains('{'))
            {
                if (TryBuildTemplateRegex(key) != null)
                    templateKeys.Add(new KeyValuePair<string, string>(key, value));
                continue;
            }
            labels[key] = value;
        }

        var templates = new List<(string, System.Text.RegularExpressions.Regex, string)>();
        foreach (KeyValuePair<string, string> pair in templateKeys.OrderByDescending(item => item.Key.Length))
        {
            System.Text.RegularExpressions.Regex pattern = TryBuildTemplateRegex(pair.Key)!;
            templates.Add((GetTemplatePrefix(pair.Key), pattern, pair.Value));
        }

        return new PhraseTable(labels, templates);
    }

    /// <summary>模板里第一个占位符之前的固定文本，用于快速排除不可能匹配的字符串。</summary>
    private static string GetTemplatePrefix(string key)
    {
        int index = key.IndexOf('{');
        return index <= 0 ? "" : key[..index];
    }

    private static System.Text.RegularExpressions.Regex? TryBuildTemplateRegex(string key)
    {
        // 只有形如“已连接 {0}”“存储初始化失败: {ex.Message}”的完整句式才做模板匹配，
        // 花括号里的名字只是占位，运行时按捕获顺序替换成 {0}、{1}…
        if (!key.Contains('{')) return null;
        if (key.Length < 4) return null;

        var builder = new System.Text.StringBuilder("^");
        int index = 0;
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(key, @"\{[^{}]+\}"))
        {
            builder.Append(System.Text.RegularExpressions.Regex.Escape(key[index..match.Index]));
            builder.Append("(.+?)");
            index = match.Index + match.Length;
        }
        if (index == 0) return null;
        builder.Append(System.Text.RegularExpressions.Regex.Escape(key[index..]));
        builder.Append('$');
        return new System.Text.RegularExpressions.Regex(
            builder.ToString(),
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static string TranslateLabel(string value, PhraseTable table, CultureInfo culture)
    {
        if (value.IndexOf('\n') < 0) return TranslateSingleLabel(value, table, culture);

        // 悬浮提示是多行“标签：内容”，逐行翻译标签。
        string[] lines = value.Replace("\r\n", "\n").Split('\n');
        bool changed = false;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string translatedLine = Resources.GetString(line, culture)
                ?? TranslateTemplate(line, table, culture);
            if (ReferenceEquals(translatedLine, line))
                translatedLine = TranslateSingleLabel(line, table, culture);
            if (ReferenceEquals(translatedLine, lines[index])) continue;
            lines[index] = translatedLine;
            changed = true;
        }
        return changed ? string.Join(Environment.NewLine, lines) : value;
    }

    /// <summary>按资源里的完整句式匹配运行时常量，例如“已连接 {0}”。</summary>
    private static string TranslateTemplate(string value, PhraseTable table, CultureInfo culture)
    {
        foreach ((string prefix, System.Text.RegularExpressions.Regex pattern, string template) in table.Templates)
        {
            // 先做一次廉价的前缀判断，避免每个界面字符串都跑几十条正则。
            if (prefix.Length > 0 && !value.StartsWith(prefix, StringComparison.Ordinal)) continue;
            System.Text.RegularExpressions.Match match = pattern.Match(value);
            if (!match.Success) continue;
            var arguments = new object[match.Groups.Count - 1];
            for (int index = 1; index < match.Groups.Count; index++)
            {
                string argument = match.Groups[index].Value;
                // 模式名、编码器名等参数本身也可能是资源词条；模板翻译后仍要让这些
                // 已知界面词跟随当前语言，普通单号、设备名等用户数据则保持原样。
                arguments[index - 1] = Resources.GetString(argument, culture) ?? argument;
            }
            return string.Format(culture, template, arguments);
        }
        return value;
    }

    private static string TranslateSingleLabel(string value, PhraseTable table, CultureInfo culture)
    {
        int separator = value.IndexOf('：');
        int separatorLength = 1;
        if (separator < 0)
        {
            separator = value.IndexOf(": ", StringComparison.Ordinal);
            separatorLength = 2;
        }
        if (separator <= 0) return value;

        string label = value[..separator].Trim();
        if (!table.Labels.TryGetValue(label, out string? translatedLabel)) return value;

        string content = value[(separator + separatorLength)..].TrimStart();
        // 内容本身也是词条时（例如发/退货、状态词）一起翻掉，否则保留原始数据。
        string trimmedContent = content.TrimEnd();
        if (trimmedContent.Length > 0)
        {
            string? translatedContent = Resources.GetString(trimmedContent, culture);
            if (translatedContent != null) content = translatedContent + content[trimmedContent.Length..];
        }
        return translatedLabel + ": " + content;
    }

    private static bool ContainsCjk(string value)
    {
        foreach (char character in value)
        {
            if (character is >= '\u4e00' and <= '\u9fff') return true;
        }
        return false;
    }

}
