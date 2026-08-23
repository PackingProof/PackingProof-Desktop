using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ExpressPackingMonitoring.Services;

internal static class ExtensionRequestSignature
{
    internal const int CurrentVersion = 1;
    internal const string ProtocolName = "packingproof-extension-request-v1";
    internal const string VersionHeader = "X-PackingProof-Extension-Version";
    internal const string InstanceIdHeader = "X-PackingProof-Extension-Id";
    internal const string CredentialGenerationHeader = "X-PackingProof-Extension-Credential-Generation";
    internal const string TimestampHeader = "X-PackingProof-Extension-Timestamp";
    internal const string NonceHeader = "X-PackingProof-Extension-Nonce";
    internal const string ContentHashHeader = "X-PackingProof-Extension-Content-SHA256";
    internal const string SignatureHeader = "X-PackingProof-Extension-Signature";
    internal static readonly TimeSpan AllowedClockSkew = TimeSpan.FromMinutes(5);

    internal static string ComputeContentHash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    internal static string Create(
        string credential,
        string method,
        string requestTarget,
        long timestamp,
        string nonce,
        string contentHash,
        string extensionInstanceId,
        int credentialGeneration)
    {
        string canonical = BuildCanonical(
            method,
            requestTarget,
            timestamp,
            nonce,
            contentHash,
            extensionInstanceId,
            credentialGeneration);
        return Convert.ToHexString(HMACSHA256.HashData(
            DecodeCredential(credential),
            Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    internal static bool FixedTimeEquals(string expected, string actual)
    {
        try
        {
            byte[] left = Convert.FromHexString(expected?.Trim() ?? "");
            byte[] right = Convert.FromHexString(actual?.Trim() ?? "");
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

    private static string BuildCanonical(
        string method,
        string requestTarget,
        long timestamp,
        string nonce,
        string contentHash,
        string extensionInstanceId,
        int credentialGeneration) =>
        string.Join('\n',
            ProtocolName,
            CurrentVersion.ToString(CultureInfo.InvariantCulture),
            credentialGeneration.ToString(CultureInfo.InvariantCulture),
            NormalizeMethod(method),
            NormalizeRequestTarget(requestTarget),
            timestamp.ToString(CultureInfo.InvariantCulture),
            nonce.Trim().ToLowerInvariant(),
            contentHash.Trim().ToLowerInvariant(),
            extensionInstanceId.Trim());

    private static byte[] DecodeCredential(string credential) =>
        Convert.FromHexString(credential?.Trim() ?? "");

    internal static string NormalizeMethod(string value)
    {
        string method = value?.Trim().ToUpperInvariant() ?? "";
        if (method.Length is < 3 or > 10 || method.Any(character => character is < 'A' or > 'Z'))
            throw new InvalidDataException("扩展请求方法格式无效");
        return method;
    }

    internal static string NormalizeRequestTarget(string value)
    {
        string target = value?.Trim() ?? "";
        int fragmentIndex = target.IndexOf('#');
        if (fragmentIndex >= 0)
            target = target[..fragmentIndex];
        if (!target.StartsWith('/'))
            target = "/" + target;
        if (target.Length is < 1 or > 2048 || target.Any(char.IsControl))
            throw new InvalidDataException("扩展请求地址格式无效");
        return target;
    }
}

internal enum ExtensionAuthenticationDisposition
{
    Accepted,
    InvalidRequest,
    UnsupportedVersion,
    StaleTimestamp,
    UnknownOrRevokedExtension,
    CredentialGenerationMismatch,
    ContentHashMismatch,
    InvalidSignature,
    ReplayDetected,
    ReplayCapacityExceeded
}

internal sealed record ExtensionSignedRequest
{
    internal int Version { get; init; }
    internal string ExtensionInstanceId { get; init; } = "";
    internal int CredentialGeneration { get; init; }
    internal long Timestamp { get; init; }
    internal string Nonce { get; init; } = "";
    internal string ContentHash { get; init; } = "";
    internal string Signature { get; init; } = "";
    internal string Method { get; init; } = "";
    internal string RequestTarget { get; init; } = "";
    internal ReadOnlyMemory<byte> Body { get; init; }
}

internal sealed record ExtensionAuthenticationResult(
    ExtensionAuthenticationDisposition Disposition,
    ExtensionAuthorizationContext? Authorization = null);

/// <summary>
/// Verifies extension requests and derives the trusted authorization context.
/// Replay state is intentionally in memory; a restart invalidates active tasks and rotates the replay window naturally.
/// </summary>
internal sealed class ExtensionRequestAuthenticator
{
    internal const int DefaultMaxNoncesPerExtension = 2048;
    private static readonly Regex IdentifierPattern = new(
        "^[A-Za-z0-9._:-]{8,128}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HexPattern = new(
        "^[A-Fa-f0-9]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly ExtensionAuthorizationStore _authorizations;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxNoncesPerExtension;
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> _nonces =
        new(StringComparer.Ordinal);

    internal ExtensionRequestAuthenticator(
        ExtensionAuthorizationStore authorizations,
        TimeProvider? timeProvider = null,
        int maxNoncesPerExtension = DefaultMaxNoncesPerExtension)
    {
        _authorizations = authorizations ?? throw new ArgumentNullException(nameof(authorizations));
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (maxNoncesPerExtension <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxNoncesPerExtension));
        _maxNoncesPerExtension = maxNoncesPerExtension;
    }

    internal ExtensionAuthenticationResult Authenticate(ExtensionSignedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Version != ExtensionRequestSignature.CurrentVersion)
            return new(ExtensionAuthenticationDisposition.UnsupportedVersion);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (!ExtensionRequestSignature.IsFresh(request.Timestamp, now))
            return new(ExtensionAuthenticationDisposition.StaleTimestamp);

        string instanceId = request.ExtensionInstanceId?.Trim() ?? "";
        string nonce = request.Nonce?.Trim().ToLowerInvariant() ?? "";
        string suppliedHash = request.ContentHash?.Trim().ToLowerInvariant() ?? "";
        string suppliedSignature = request.Signature?.Trim().ToLowerInvariant() ?? "";
        string method;
        string requestTarget;
        try
        {
            method = ExtensionRequestSignature.NormalizeMethod(request.Method);
            requestTarget = ExtensionRequestSignature.NormalizeRequestTarget(request.RequestTarget);
        }
        catch (InvalidDataException)
        {
            return new(ExtensionAuthenticationDisposition.InvalidRequest);
        }
        if (!IdentifierPattern.IsMatch(instanceId)
            || request.CredentialGeneration <= 0
            || nonce.Length is < 32 or > 128
            || !HexPattern.IsMatch(nonce)
            || suppliedHash.Length != 64
            || !HexPattern.IsMatch(suppliedHash)
            || suppliedSignature.Length != 64
            || !HexPattern.IsMatch(suppliedSignature))
        {
            return new(ExtensionAuthenticationDisposition.InvalidRequest);
        }

        if (!_authorizations.TryGetActiveCredential(
            instanceId,
            out string credential,
            out ExtensionAuthorizationContext? authorization)
            || authorization == null)
        {
            return new(ExtensionAuthenticationDisposition.UnknownOrRevokedExtension);
        }
        if (authorization.CredentialGeneration != request.CredentialGeneration)
            return new(ExtensionAuthenticationDisposition.CredentialGenerationMismatch);

        string actualHash = ExtensionRequestSignature.ComputeContentHash(request.Body.Span);
        if (!ExtensionRequestSignature.FixedTimeEquals(actualHash, suppliedHash))
            return new(ExtensionAuthenticationDisposition.ContentHashMismatch);

        string expectedSignature;
        try
        {
            expectedSignature = ExtensionRequestSignature.Create(
                credential,
                method,
                requestTarget,
                request.Timestamp,
                nonce,
                suppliedHash,
                instanceId,
                request.CredentialGeneration);
        }
        catch
        {
            return new(ExtensionAuthenticationDisposition.InvalidRequest);
        }
        if (!ExtensionRequestSignature.FixedTimeEquals(expectedSignature, suppliedSignature))
            return new(ExtensionAuthenticationDisposition.InvalidSignature);

        ExtensionAuthenticationDisposition replay = ClaimNonce(instanceId, nonce, now);
        return replay == ExtensionAuthenticationDisposition.Accepted
            ? new ExtensionAuthenticationResult(replay, authorization)
            : new ExtensionAuthenticationResult(replay);
    }

    private ExtensionAuthenticationDisposition ClaimNonce(
        string extensionInstanceId,
        string nonce,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_nonces.TryGetValue(extensionInstanceId, out Dictionary<string, DateTimeOffset>? extensionNonces))
            {
                extensionNonces = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
                _nonces.Add(extensionInstanceId, extensionNonces);
            }

            DateTimeOffset oldestAllowed = now - ExtensionRequestSignature.AllowedClockSkew;
            foreach (string expired in extensionNonces
                .Where(item => item.Value < oldestAllowed)
                .Select(item => item.Key)
                .ToArray())
            {
                extensionNonces.Remove(expired);
            }
            if (extensionNonces.ContainsKey(nonce))
                return ExtensionAuthenticationDisposition.ReplayDetected;
            if (extensionNonces.Count >= _maxNoncesPerExtension)
                return ExtensionAuthenticationDisposition.ReplayCapacityExceeded;

            extensionNonces.Add(nonce, now);
            return ExtensionAuthenticationDisposition.Accepted;
        }
    }
}
