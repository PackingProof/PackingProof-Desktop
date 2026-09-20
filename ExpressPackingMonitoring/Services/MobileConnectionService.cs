using System.Net;

namespace ExpressPackingMonitoring.Services;

internal static class MobileConnectionService
{
    public static bool ContainsAccessKey(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        return uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Any(pair => pair.Length == 2
                && string.Equals(Uri.UnescapeDataString(pair[0]), "key", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(Uri.UnescapeDataString(pair[1])));
    }

    public static string BuildAccessUrl(string address, bool requireAccessKey, string? accessKey)
    {
        string url = WorkstationNetwork.ToUrl(address).TrimEnd('/');
        // App pairing always needs the key for mobile-backup-v1, even when the
        // browser-facing video page itself does not require authentication.
        if (string.IsNullOrWhiteSpace(accessKey))
            return url;

        return $"{url}/?key={Uri.EscapeDataString(accessKey.Trim())}";
    }

    public static bool TryBuildUsableAccessUrl(
        string address,
        bool requireAccessKey,
        string? accessKey,
        out string url)
    {
        url = "";
        string normalizedAddress = WorkstationNetwork.NormalizeAddress(address);
        if (string.IsNullOrWhiteSpace(normalizedAddress))
            return false;

        string candidate = BuildAccessUrl(normalizedAddress, requireAccessKey, accessKey);
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.IsLoopback
            || IsUnusableIpAddress(uri.Host))
        {
            return false;
        }

        url = candidate;
        return true;
    }

    public static string CreateQrDataUri(string url, int size = 260)
        => QrCodeRenderer.CreateDataUri(url, size);

    private static bool IsUnusableIpAddress(string host)
    {
        if (!IPAddress.TryParse(host, out IPAddress? address))
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

        return IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6None);
    }
}
