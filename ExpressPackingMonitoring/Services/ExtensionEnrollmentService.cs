using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExpressPackingMonitoring.Services;

internal sealed record ExtensionEnrollmentRequest
{
    internal string RequestId { get; init; } = "";
    internal string RequestSecret { get; init; } = "";
    internal string ExtensionInstanceId { get; init; } = "";
    internal string ProviderId { get; init; } = "";
    internal string DisplayName { get; init; } = "";
    internal string Version { get; init; } = "";
    internal string Source { get; init; } = "";
    internal string RemoteAddress { get; init; } = "";
    internal IReadOnlyList<string> RequestedPermissions { get; init; } = [];
    internal IReadOnlyList<string> RequestedCapabilities { get; init; } = [];
}

internal enum ExtensionEnrollmentApprovalDisposition
{
    Approved,
    Denied,
    Unavailable
}

internal sealed record ExtensionEnrollmentApprovalResult
{
    internal ExtensionEnrollmentApprovalDisposition Disposition { get; init; }
    internal IReadOnlyList<string> ApprovedPermissions { get; init; } = [];
    internal IReadOnlyList<string> ApprovedCapabilities { get; init; } = [];
    internal ExtensionRoutingScope RoutingScope { get; init; }
    internal IReadOnlyList<string> BoundOriginNodeIds { get; init; } = [];
}

internal enum ExtensionEnrollmentDisposition
{
    Approved,
    Denied,
    Unavailable,
    RequestConflict
}

internal sealed record ExtensionEnrollmentOutcome(
    ExtensionEnrollmentDisposition Disposition,
    ExtensionEnrollmentCredential? Enrollment = null);

/// <summary>
/// Coordinates an untrusted enrollment request with an explicit host approval. Request retries are
/// idempotent for a short window and an approval callback can only reduce, never expand, requested access.
/// </summary>
internal sealed class ExtensionEnrollmentService
{
    internal static readonly TimeSpan DefaultResultRetention = TimeSpan.FromMinutes(2);

    private static readonly Regex IdentifierPattern = new(
        "^[A-Za-z0-9._:-]{8,128}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ProviderPattern = new(
        "^[a-z0-9][a-z0-9._-]{2,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly ExtensionAuthorizationStore _authorizations;
    private readonly Func<ExtensionEnrollmentRequest, ExtensionEnrollmentApprovalResult> _approver;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _resultRetention;
    private readonly Dictionary<string, CachedOutcome> _outcomes = new(StringComparer.Ordinal);

    internal ExtensionEnrollmentService(
        ExtensionAuthorizationStore authorizations,
        Func<ExtensionEnrollmentRequest, ExtensionEnrollmentApprovalResult> approver,
        TimeProvider? timeProvider = null,
        TimeSpan? resultRetention = null)
    {
        _authorizations = authorizations ?? throw new ArgumentNullException(nameof(authorizations));
        _approver = approver ?? throw new ArgumentNullException(nameof(approver));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _resultRetention = resultRetention ?? DefaultResultRetention;
        if (_resultRetention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(resultRetention));
    }

    internal ExtensionEnrollmentOutcome Enroll(ExtensionEnrollmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        NormalizedRequest normalized = Normalize(request);
        string cacheKey = CreateCacheKey(normalized.RequestId, normalized.RequestSecret);
        string fingerprint = CreateFingerprint(normalized);

        lock (_gate)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            Prune(now);
            if (_outcomes.TryGetValue(cacheKey, out CachedOutcome? cached))
            {
                return string.Equals(cached.RequestFingerprint, fingerprint, StringComparison.Ordinal)
                    ? cached.Outcome
                    : new ExtensionEnrollmentOutcome(ExtensionEnrollmentDisposition.RequestConflict);
            }

            ExtensionEnrollmentApprovalResult approval = _approver(ToRequest(normalized))
                ?? new ExtensionEnrollmentApprovalResult
                {
                    Disposition = ExtensionEnrollmentApprovalDisposition.Unavailable
                };
            ExtensionEnrollmentOutcome outcome = Complete(normalized, approval);
            _outcomes[cacheKey] = new CachedOutcome(
                fingerprint,
                outcome,
                now + _resultRetention);
            return outcome;
        }
    }

    private ExtensionEnrollmentOutcome Complete(
        NormalizedRequest request,
        ExtensionEnrollmentApprovalResult approval)
    {
        if (approval.Disposition == ExtensionEnrollmentApprovalDisposition.Denied)
            return new ExtensionEnrollmentOutcome(ExtensionEnrollmentDisposition.Denied);
        if (approval.Disposition != ExtensionEnrollmentApprovalDisposition.Approved)
            return new ExtensionEnrollmentOutcome(ExtensionEnrollmentDisposition.Unavailable);

        string[] permissions = NormalizeApprovedSubset(
            approval.ApprovedPermissions,
            request.RequestedPermissions,
            "批准权限");
        string[] capabilities = NormalizeApprovedSubset(
            approval.ApprovedCapabilities,
            request.RequestedCapabilities,
            "批准能力");
        var authorizationApproval = new ExtensionAuthorizationApproval
        {
            ExtensionInstanceId = request.ExtensionInstanceId,
            ProviderId = request.ProviderId,
            DisplayName = request.DisplayName,
            Version = request.Version,
            Source = request.Source,
            Permissions = permissions,
            Capabilities = capabilities,
            RoutingScope = approval.RoutingScope,
            BoundOriginNodeIds = approval.BoundOriginNodeIds
        };
        ExtensionEnrollmentCredential enrollment = _authorizations.Approve(authorizationApproval);
        return new ExtensionEnrollmentOutcome(
            ExtensionEnrollmentDisposition.Approved,
            enrollment);
    }

    private static NormalizedRequest Normalize(ExtensionEnrollmentRequest request)
    {
        string requestId = NormalizeIdentifier(request.RequestId, "授权请求 ID");
        string secret = request.RequestSecret?.Trim().ToLowerInvariant() ?? "";
        if (secret.Length != 64 || secret.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("授权请求随机秘密格式无效");
        string extensionId = NormalizeIdentifier(request.ExtensionInstanceId, "扩展实例 ID");
        string providerId = request.ProviderId?.Trim().ToLowerInvariant() ?? "";
        if (!ProviderPattern.IsMatch(providerId))
            throw new InvalidDataException("来源标识格式无效");
        string displayName = NormalizeText(request.DisplayName, 100, "扩展名称");
        string version = NormalizeText(request.Version, 32, "扩展版本");
        string source = NormalizeText(request.Source, 256, "扩展来源");
        string remoteAddress = NormalizeText(request.RemoteAddress, 256, "远程地址");
        string[] permissions = NormalizeRequestedValues(
            request.RequestedPermissions,
            ExtensionPermissions.Supported,
            "申请权限");
        string[] capabilities = NormalizeRequestedValues(
            request.RequestedCapabilities,
            ExtensionScanCapabilities.Supported,
            "申请能力");
        if (permissions.Length == 0)
            throw new InvalidDataException("扩展必须申请至少一项权限");
        if (capabilities.Length > 0
            && (!permissions.Contains(ExtensionPermissions.ScanTasksRead, StringComparer.Ordinal)
                || !permissions.Contains(ExtensionPermissions.ScanResultsWrite, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("申请扫码能力时必须同时申请任务读取和结果写入权限");
        }
        if (capabilities.Contains(ExtensionScanCapabilities.MeasurementCapture, StringComparer.Ordinal)
            && !permissions.Contains(ExtensionPermissions.RecordingFieldsWrite, StringComparer.Ordinal))
        {
            throw new InvalidDataException("申请测量能力时必须同时申请录像字段写入权限");
        }
        return new NormalizedRequest(
            requestId,
            secret,
            extensionId,
            providerId,
            displayName,
            version,
            source,
            remoteAddress,
            permissions,
            capabilities);
    }

    private static string[] NormalizeRequestedValues(
        IEnumerable<string>? values,
        IReadOnlySet<string> supported,
        string fieldName)
    {
        string[] normalized = (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        string? unsupported = normalized.FirstOrDefault(value => !supported.Contains(value));
        if (unsupported != null)
            throw new InvalidDataException($"{fieldName}不受支持：{unsupported}");
        return normalized;
    }

    private static string[] NormalizeApprovedSubset(
        IEnumerable<string>? approved,
        IReadOnlyList<string> requested,
        string fieldName)
    {
        string[] values = (approved ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        string? elevated = values.FirstOrDefault(value => !requested.Contains(value, StringComparer.Ordinal));
        if (elevated != null)
            throw new InvalidDataException($"{fieldName}超出扩展申请范围：{elevated}");
        return values;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (string key in _outcomes
            .Where(pair => pair.Value.ExpiresAtUtc <= now)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _outcomes.Remove(key);
        }
    }

    private static string CreateCacheKey(string requestId, string requestSecret)
    {
        byte[] secretHash = SHA256.HashData(Encoding.UTF8.GetBytes(requestSecret));
        return $"{requestId}:{Convert.ToHexString(secretHash)}";
    }

    private static string CreateFingerprint(NormalizedRequest request)
    {
        string json = JsonSerializer.Serialize(new
        {
            request.ExtensionInstanceId,
            request.ProviderId,
            request.DisplayName,
            request.Version,
            request.Source,
            request.RemoteAddress,
            request.RequestedPermissions,
            request.RequestedCapabilities
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static ExtensionEnrollmentRequest ToRequest(NormalizedRequest request) => new()
    {
        RequestId = request.RequestId,
        RequestSecret = "",
        ExtensionInstanceId = request.ExtensionInstanceId,
        ProviderId = request.ProviderId,
        DisplayName = request.DisplayName,
        Version = request.Version,
        Source = request.Source,
        RemoteAddress = request.RemoteAddress,
        RequestedPermissions = request.RequestedPermissions,
        RequestedCapabilities = request.RequestedCapabilities
    };

    private static string NormalizeIdentifier(string? value, string fieldName)
    {
        string normalized = value?.Trim() ?? "";
        if (!IdentifierPattern.IsMatch(normalized))
            throw new InvalidDataException($"{fieldName}格式无效");
        return normalized;
    }

    private static string NormalizeText(string? value, int maxLength, string fieldName)
    {
        string normalized = value?.Trim() ?? "";
        if (normalized.Length == 0
            || normalized.Length > maxLength
            || normalized.Any(char.IsControl))
        {
            throw new InvalidDataException($"{fieldName}格式无效");
        }
        return normalized;
    }

    private sealed record NormalizedRequest(
        string RequestId,
        string RequestSecret,
        string ExtensionInstanceId,
        string ProviderId,
        string DisplayName,
        string Version,
        string Source,
        string RemoteAddress,
        string[] RequestedPermissions,
        string[] RequestedCapabilities);

    private sealed record CachedOutcome(
        string RequestFingerprint,
        ExtensionEnrollmentOutcome Outcome,
        DateTimeOffset ExpiresAtUtc);
}
