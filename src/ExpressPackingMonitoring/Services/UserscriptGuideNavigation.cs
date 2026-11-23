namespace ExpressPackingMonitoring.Services;

internal static class UserscriptGuideNavigation
{
    private const string GuidePath = "/kuaidizs-install-guide";

    internal static string BuildUrl(string hostAddress)
    {
        string value = hostAddress?.Trim().TrimEnd('/') ?? "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return "";
        }

        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port)
        {
            Path = GuidePath,
            Query = $"refresh={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
            Fragment = ""
        };
        return builder.Uri.AbsoluteUri;
    }

    internal static bool TryOpen(string hostAddress, out string error)
    {
        string url = BuildUrl(hostAddress);
        if (url.Length == 0)
        {
            error = ExpressPackingMonitoring.UpdateCore.BrandText.Apply("PackingProof 主机地址无效");
            return false;
        }

        return WorkstationNetwork.TryOpenUrl(url, out error);
    }
}
