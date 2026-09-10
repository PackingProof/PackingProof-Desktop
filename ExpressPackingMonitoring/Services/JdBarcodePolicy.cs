using System.Text.RegularExpressions;

namespace ExpressPackingMonitoring.Services;

/// <summary>京东双条码的同帧关联；不保存跨帧状态，也不改写多包裹身份。</summary>
internal static class JdBarcodePolicy
{
    // 仅启用已确认的 JD / JDVA 号型，不猜测其他承运商或历史前缀。
    private static readonly Regex WaybillPattern = new(@"\AJD(?:VA)?[0-9]+\z");
    private static readonly Regex PackagePattern = new(@"\A(JD(?:VA)?[0-9]+)-([1-9][0-9]*)-([1-9][0-9]*)-\z");

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
        return Parse(code) is { Index: 1, Count: 1 } package ? package.Waybill : code;
    }

    public static bool MatchesPackage(string waybill, string candidate) =>
        IsBareWaybill(waybill) && Parse(candidate) is { } package && package.Waybill == waybill;
}
