using System.Text.RegularExpressions;

namespace ExpressPackingMonitoring.Services;

/// <summary>京东双条码的同帧关联；不保存跨帧状态，也不改写多包裹身份。</summary>
internal static class JdBarcodePolicy
{
    // JD 开头的字母数字单号统一关联合法包裹后缀，保留完整包裹身份。
    private static readonly Regex WaybillPattern = new(@"\AJD[A-Z0-9]+\z");
    private static readonly Regex PackagePattern = new(@"\A(JD[A-Z0-9]+)-([1-9][0-9]*)-([1-9][0-9]*)-\z");

    internal readonly record struct Package(string RawCode, string Waybill, int Index, int Count);

    public static bool IsBareWaybill(string code) => code.Length is >= 8 and <= 40 && WaybillPattern.IsMatch(code);

    public static Package? Parse(string code)
    {
        if (code.Length > 40) return null;
        Match match = PackagePattern.Match(code);
        if (!match.Success || !IsBareWaybill(match.Groups[1].Value)
            || !int.TryParse(match.Groups[2].Value, out int index)
            || !int.TryParse(match.Groups[3].Value, out int count) || index > count)
            return null;
        return new Package(code, match.Groups[1].Value, index, count);
    }

    public static string Normalize(string? value)
    {
        string code = (value ?? "").Trim().ToUpperInvariant();
        return code;
    }

    public static bool SameRecordingCode(string? left, string? right)
    {
        string a = Normalize(left), b = Normalize(right);
        return a == b || MatchesPackage(a, b) || MatchesPackage(b, a);
    }

    public static string PreferSpecific(string current, string observed) =>
        MatchesPackage(current, observed) ? observed : current;

    public static string Waybill(string? value)
    {
        string code = Normalize(value);
        return Parse(code)?.Waybill ?? code;
    }

    public static bool MatchesPackage(string waybill, string candidate) =>
        IsBareWaybill(waybill) && Parse(candidate) is { } package && package.Waybill == waybill;
}
