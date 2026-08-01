using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ExpressPackingMonitoring.Services;

internal static class BackupRequestAuthentication
{
    internal const int CurrentVersion = 3;
    internal const string VersionHeader = "X-EPM-Auth-Version";
    internal const string TimestampHeader = "X-EPM-Timestamp";
    internal const string NonceHeader = "X-EPM-Nonce";
    internal const string ContentHashHeader = "X-EPM-Content-SHA256";
    internal const string SignatureHeader = "X-EPM-Signature";
    internal static readonly TimeSpan AllowedClockSkew = TimeSpan.FromMinutes(5);

    internal static string ComputeContentHash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    internal static string CreateRequestSignature(
        string deviceCredential,
        string method,
        string path,
        long timestamp,
        string nonce,
        string contentHash,
        string deviceId)
    {
        string canonical = string.Join('\n',
            method.Trim().ToUpperInvariant(),
            NormalizePath(path),
            timestamp.ToString(CultureInfo.InvariantCulture),
            nonce.Trim(),
            contentHash.Trim().ToLowerInvariant(),
            NormalizeDeviceId(deviceId));
        return ComputeHmac(deviceCredential, canonical);
    }

    internal static string CreateReceiptSignature(
        string deviceCredential,
        string hostNodeId,
        string sourceDeviceId,
        string sourceSessionId,
        string fileSha256,
        long fileSizeBytes,
        long recordId,
        long verifiedAtUnixSeconds)
    {
        string canonical = string.Join('\n',
            "packingproof-verified-receipt-v3",
            NormalizeDeviceId(hostNodeId),
            NormalizeDeviceId(sourceDeviceId),
            sourceSessionId.Trim(),
            fileSha256.Trim().ToLowerInvariant(),
            fileSizeBytes.ToString(CultureInfo.InvariantCulture),
            recordId.ToString(CultureInfo.InvariantCulture),
            verifiedAtUnixSeconds.ToString(CultureInfo.InvariantCulture));
        return ComputeHmac(deviceCredential, canonical);
    }

    internal static bool FixedTimeEquals(string expected, string actual)
    {
        try
        {
            byte[] left = Convert.FromHexString(expected.Trim());
            byte[] right = Convert.FromHexString(actual.Trim());
            return left.Length == right.Length
                && CryptographicOperations.FixedTimeEquals(left, right);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsFresh(long unixSeconds, DateTimeOffset now) =>
        Math.Abs(now.ToUnixTimeSeconds() - unixSeconds) <= AllowedClockSkew.TotalSeconds;

    private static string ComputeHmac(string secret, string canonical) =>
        Convert.ToHexString(HMACSHA256.HashData(
            DecodeSecret(secret),
            Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    private static byte[] DecodeSecret(string secret)
    {
        string value = secret?.Trim() ?? "";
        if (value.Length >= 32 && value.Length % 2 == 0)
        {
            try { return Convert.FromHexString(value); }
            catch { }
        }
        return Encoding.UTF8.GetBytes(value);
    }

    private static string NormalizeDeviceId(string value) =>
        value?.Trim().ToLowerInvariant() ?? "";

    private static string NormalizePath(string value)
    {
        string path = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim();
        int queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
            path = path[..queryIndex];
        return path.StartsWith('/') ? path : "/" + path;
    }
}
