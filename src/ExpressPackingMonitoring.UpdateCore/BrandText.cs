namespace ExpressPackingMonitoring.UpdateCore;

public static class BrandText
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static string Apply(string? value)
        => Apply(
            value,
            BrandMetadata.LegacyProductName,
            BrandMetadata.ProductName,
            BrandMetadata.DisplayName,
            BrandMetadata.WindowTitle,
            BrandMetadata.ArtifactPrefix);

    internal static string Apply(
        string? value,
        string legacyProductName,
        string productName,
        string displayName,
        string windowTitle,
        string artifactPrefix)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? "";

        string result = value;
        result = ReplaceStandaloneBrand(result, "PackingProofVideos", artifactPrefix + "Videos");
        result = ReplaceExact(result, "PackingProof 快递打包监控", displayName);
        result = ReplaceExact(result, "PackingProof 包裹留证", windowTitle);
        result = ReplaceStandaloneBrand(result, legacyProductName, productName);
        if (!string.Equals(legacyProductName, "Packing Proof", StringComparison.OrdinalIgnoreCase))
            result = ReplaceStandaloneBrand(result, "Packing Proof", productName);
        return result;
    }

    private static string ReplaceExact(string value, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(oldValue)
            || string.Equals(oldValue, newValue, StringComparison.Ordinal))
            return value;

        return value.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    private static string ReplaceStandaloneBrand(string value, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(oldValue)
            || string.Equals(oldValue, newValue, StringComparison.Ordinal))
            return value;

        string pattern =
            $@"(?<![A-Za-z0-9_./\\@#?=&:%+-]){System.Text.RegularExpressions.Regex.Escape(oldValue)}(?![A-Za-z0-9_/@#?=&:%+-])(?!\.[A-Za-z0-9])";
        return System.Text.RegularExpressions.Regex.Replace(
            value,
            pattern,
            _ => newValue,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            RegexTimeout);
    }
}
