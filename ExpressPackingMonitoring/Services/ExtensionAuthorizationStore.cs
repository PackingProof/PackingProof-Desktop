using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

internal static class ExtensionPermissions
{
    internal const string OrdersWrite = "orders.write";
    internal const string ScanTasksRead = "scan-tasks.read";
    internal const string ScanResultsWrite = "scan-results.write";
    internal const string RecordingsActiveRead = "recordings.active.read";
    internal const string RecordingFieldsWrite = "recording-fields.write";
    internal const string RecordingsSearch = "recordings.search";
    internal const string RecordingsDownload = "recordings.download";
    internal const string RecordingsDelivery = "recordings.delivery";

    internal static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        OrdersWrite,
        ScanTasksRead,
        ScanResultsWrite,
        RecordingsActiveRead,
        RecordingFieldsWrite,
        RecordingsSearch,
        RecordingsDownload,
        RecordingsDelivery
    };
}

internal enum ExtensionRoutingScope
{
    AllLocalRecordingNodes,
    SelectedRecordingNodes
}

internal sealed record ExtensionAuthorizationApproval
{
    internal string ExtensionInstanceId { get; init; } = "";
    internal string ProviderId { get; init; } = "";
    internal string DisplayName { get; init; } = "";
    internal string Version { get; init; } = "";
    internal string Source { get; init; } = "";
    internal IReadOnlyList<string> Permissions { get; init; } = [];
    internal IReadOnlyList<string> Capabilities { get; init; } = [];
    internal ExtensionRoutingScope RoutingScope { get; init; }
    internal IReadOnlyList<string> BoundOriginNodeIds { get; init; } = [];
}

internal sealed record ExtensionEnrollmentCredential(
    ExtensionAuthorizationContext Authorization,
    string Credential);

internal sealed record ExtensionAuthorizationContext
{
    internal string ExtensionInstanceId { get; init; } = "";
    internal string ProviderId { get; init; } = "";
    internal string DisplayName { get; init; } = "";
    internal string Version { get; init; } = "";
    internal string Source { get; init; } = "";
    internal IReadOnlyList<string> Permissions { get; init; } = [];
    internal IReadOnlyList<string> Capabilities { get; init; } = [];
    internal ExtensionRoutingScope RoutingScope { get; init; }
    internal IReadOnlyList<string> BoundOriginNodeIds { get; init; } = [];
    internal int CredentialGeneration { get; init; }
    internal DateTimeOffset ApprovedAtUtc { get; init; }
    internal DateTimeOffset UpdatedAtUtc { get; init; }
    internal DateTimeOffset? RevokedAtUtc { get; init; }
    internal DateTimeOffset? LastSeenUtc { get; init; }
    internal DateTimeOffset? LastBusinessActivityUtc { get; init; }
    internal int LastBusinessDataCount { get; init; }
    internal string RuntimeVersion { get; init; } = "";

    internal bool HasPermission(string permission) =>
        Permissions.Contains(permission, StringComparer.Ordinal);

    internal bool SupportsCapability(string capability) =>
        Capabilities.Contains(capability, StringComparer.Ordinal);

    internal bool IsBoundToOriginNode(string originNodeId) =>
        RoutingScope == ExtensionRoutingScope.AllLocalRecordingNodes
        || BoundOriginNodeIds.Contains(originNodeId?.Trim() ?? "", StringComparer.Ordinal);
}

/// <summary>
/// Persists extension-scoped credentials and approved access. It deliberately has no HTTP or UI dependency.
/// Credentials use a root key that is independent from Web access and mobile-backup credentials.
/// </summary>
internal sealed class ExtensionAuthorizationStore
{
    private const int SchemaVersion = 1;
    private static readonly Regex IdentifierPattern = new(
        "^[A-Za-z0-9._:-]{8,128}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ProviderPattern = new(
        "^[a-z0-9][a-z0-9._-]{2,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly string _registryPath;
    private readonly byte[] _encryptionKey;
    private readonly Dictionary<string, AuthorizationState> _authorizations =
        new(StringComparer.Ordinal);

    internal ExtensionAuthorizationStore(string persistentStateDirectory, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(persistentStateDirectory))
            throw new ArgumentException("扩展状态目录不能为空", nameof(persistentStateDirectory));

        string directory = Path.Combine(Path.GetFullPath(persistentStateDirectory), "extensions");
        Directory.CreateDirectory(directory);
        _registryPath = Path.Combine(directory, "authorizations.json");
        _encryptionKey = LoadOrCreateRootKey(Path.Combine(directory, "extension-root.key"));
        _timeProvider = timeProvider ?? TimeProvider.System;
        Load();
    }

    internal ExtensionEnrollmentCredential Approve(ExtensionAuthorizationApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ExtensionAuthorizationApproval normalized = NormalizeApproval(approval);

        lock (_gate)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            _authorizations.TryGetValue(normalized.ExtensionInstanceId, out AuthorizationState? previous);
            int generation = checked((previous?.CredentialGeneration ?? 0) + 1);
            var state = new AuthorizationState
            {
                ExtensionInstanceId = normalized.ExtensionInstanceId,
                ProviderId = normalized.ProviderId,
                DisplayName = normalized.DisplayName,
                Version = normalized.Version,
                Source = normalized.Source,
                Permissions = normalized.Permissions.ToArray(),
                Capabilities = normalized.Capabilities.ToArray(),
                RoutingScope = normalized.RoutingScope,
                BoundOriginNodeIds = normalized.BoundOriginNodeIds.ToArray(),
                CredentialGeneration = generation,
                Credential = CreateCredential(),
                ApprovedAtUtc = previous is { RevokedAtUtc: null }
                    ? previous.ApprovedAtUtc
                    : now,
                UpdatedAtUtc = now,
                LastSeenUtc = previous?.LastSeenUtc,
                LastBusinessActivityUtc = previous?.LastBusinessActivityUtc,
                LastBusinessDataCount = previous?.LastBusinessDataCount ?? 0,
                RuntimeVersion = previous?.RuntimeVersion ?? ""
            };
            _authorizations[state.ExtensionInstanceId] = state;
            Save();
            return new ExtensionEnrollmentCredential(ToContext(state), state.Credential);
        }
    }

    internal ExtensionEnrollmentCredential RotateCredential(string extensionInstanceId)
    {
        string normalizedId = NormalizeIdentifier(extensionInstanceId, "扩展实例 ID");
        lock (_gate)
        {
            if (!_authorizations.TryGetValue(normalizedId, out AuthorizationState? state)
                || state.RevokedAtUtc != null)
            {
                throw new InvalidOperationException("扩展授权不存在或已撤销");
            }

            state.CredentialGeneration = checked(state.CredentialGeneration + 1);
            state.Credential = CreateCredential();
            state.UpdatedAtUtc = _timeProvider.GetUtcNow();
            Save();
            return new ExtensionEnrollmentCredential(ToContext(state), state.Credential);
        }
    }

    internal bool Revoke(string extensionInstanceId)
    {
        string normalizedId = NormalizeIdentifier(extensionInstanceId, "扩展实例 ID");
        lock (_gate)
        {
            if (!_authorizations.TryGetValue(normalizedId, out AuthorizationState? state)
                || state.RevokedAtUtc != null)
            {
                return false;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            state.Credential = "";
            state.RevokedAtUtc = now;
            state.UpdatedAtUtc = now;
            Save();
            return true;
        }
    }

    internal bool TryAuthenticate(
        string extensionInstanceId,
        string credential,
        out ExtensionAuthorizationContext? authorization)
    {
        authorization = null;
        string normalizedId;
        try { normalizedId = NormalizeIdentifier(extensionInstanceId, "扩展实例 ID"); }
        catch (InvalidDataException) { return false; }

        lock (_gate)
        {
            if (!_authorizations.TryGetValue(normalizedId, out AuthorizationState? state)
                || state.RevokedAtUtc != null
                || !FixedTimeCredentialEquals(state.Credential, credential))
            {
                return false;
            }

            authorization = ToContext(state);
            return true;
        }
    }

    internal bool TryGetActiveCredential(
        string extensionInstanceId,
        out string credential,
        out ExtensionAuthorizationContext? authorization)
    {
        credential = "";
        authorization = null;
        string normalizedId;
        try { normalizedId = NormalizeIdentifier(extensionInstanceId, "扩展实例 ID"); }
        catch (InvalidDataException) { return false; }

        lock (_gate)
        {
            if (!_authorizations.TryGetValue(normalizedId, out AuthorizationState? state)
                || state.RevokedAtUtc != null
                || !IsCredentialFormatValid(state.Credential))
            {
                return false;
            }

            credential = state.Credential;
            authorization = ToContext(state);
            return true;
        }
    }

    internal IReadOnlyList<ExtensionAuthorizationContext> GetAll() =>
        GetAll(includeRevoked: true);

    internal IReadOnlyList<ExtensionAuthorizationContext> GetAll(bool includeRevoked)
    {
        lock (_gate)
        {
            return _authorizations.Values
                .Where(state => includeRevoked || state.RevokedAtUtc == null)
                .OrderBy(state => state.DisplayName, StringComparer.Ordinal)
                .ThenBy(state => state.ExtensionInstanceId, StringComparer.Ordinal)
                .Select(ToContext)
                .ToArray();
        }
    }

    internal void RecordHeartbeat(string extensionInstanceId, string? runtimeVersion = null)
    {
        string normalizedId = NormalizeIdentifier(extensionInstanceId, "扩展实例 ID");
        lock (_gate)
        {
            if (!_authorizations.TryGetValue(normalizedId, out AuthorizationState? state)
                || state.RevokedAtUtc != null)
                return;
            state.LastSeenUtc = _timeProvider.GetUtcNow();
            string version = runtimeVersion?.Trim() ?? "";
            if (version.Length is > 0 and <= 32 && !version.Any(char.IsControl))
                state.RuntimeVersion = version;
        }
    }

    internal void RecordBusinessActivity(string extensionInstanceId, int dataCount)
    {
        if (dataCount <= 0) return;
        string normalizedId = NormalizeIdentifier(extensionInstanceId, "扩展实例 ID");
        lock (_gate)
        {
            if (!_authorizations.TryGetValue(normalizedId, out AuthorizationState? state)
                || state.RevokedAtUtc != null)
                return;
            DateTimeOffset now = _timeProvider.GetUtcNow();
            state.LastSeenUtc = now;
            state.LastBusinessActivityUtc = now;
            state.LastBusinessDataCount = dataCount;
            Save();
        }
    }

    private static ExtensionAuthorizationApproval NormalizeApproval(
        ExtensionAuthorizationApproval approval)
    {
        string extensionId = NormalizeIdentifier(approval.ExtensionInstanceId, "扩展实例 ID");
        string providerId = approval.ProviderId?.Trim().ToLowerInvariant() ?? "";
        if (!ProviderPattern.IsMatch(providerId))
            throw new InvalidDataException("来源标识格式无效");
        string displayName = NormalizeText(approval.DisplayName, 100, "扩展名称", required: true);
        string version = NormalizeText(approval.Version, 32, "扩展版本", required: true);
        string source = NormalizeText(approval.Source, 256, "扩展来源", required: true);
        string[] permissions = NormalizeKnownValues(
            approval.Permissions,
            ExtensionPermissions.Supported,
            "权限");
        string[] capabilities = NormalizeKnownValues(
            approval.Capabilities,
            ExtensionScanCapabilities.Supported,
            "能力");
        string[] boundNodes = (approval.BoundOriginNodeIds ?? [])
            .Select(node => NormalizeIdentifier(node, "录像节点 ID"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(node => node, StringComparer.Ordinal)
            .ToArray();

        if (capabilities.Length > 0
            && (!permissions.Contains(ExtensionPermissions.ScanTasksRead, StringComparer.Ordinal)
                || !permissions.Contains(ExtensionPermissions.ScanResultsWrite, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("扫码能力必须同时批准任务读取和结果写入权限");
        }
        if (permissions.Contains(ExtensionPermissions.RecordingsDownload, StringComparer.Ordinal)
            && !permissions.Contains(ExtensionPermissions.RecordingsSearch, StringComparer.Ordinal))
            throw new InvalidDataException("录像下载权限必须与录像查询权限同时批准");
        if (permissions.Contains(ExtensionPermissions.RecordingsDelivery, StringComparer.Ordinal)
            && (!permissions.Contains(ExtensionPermissions.RecordingsSearch, StringComparer.Ordinal)
                || !permissions.Contains(ExtensionPermissions.RecordingsDownload, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("录像交付副本权限必须同时批准录像查询和下载权限");
        }
        if (capabilities.Contains(ExtensionScanCapabilities.MeasurementCapture, StringComparer.Ordinal))
        {
            if (!permissions.Contains(ExtensionPermissions.RecordingFieldsWrite, StringComparer.Ordinal))
                throw new InvalidDataException("测量能力必须批准录像字段写入权限");
            if (approval.RoutingScope != ExtensionRoutingScope.SelectedRecordingNodes
                || boundNodes.Length == 0)
            {
                throw new InvalidDataException("测量扩展必须绑定至少一个录像节点");
            }
        }
        if (approval.RoutingScope == ExtensionRoutingScope.SelectedRecordingNodes
            && boundNodes.Length == 0)
        {
            throw new InvalidDataException("选择指定录像节点时必须提供绑定节点");
        }
        if (approval.RoutingScope == ExtensionRoutingScope.AllLocalRecordingNodes)
            boundNodes = [];

        return approval with
        {
            ExtensionInstanceId = extensionId,
            ProviderId = providerId,
            DisplayName = displayName,
            Version = version,
            Source = source,
            Permissions = permissions,
            Capabilities = capabilities,
            BoundOriginNodeIds = boundNodes
        };
    }

    private static string[] NormalizeKnownValues(
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

    private static string NormalizeText(string? value, int maxLength, string fieldName, bool required)
    {
        string normalized = value?.Trim() ?? "";
        if ((required && normalized.Length == 0)
            || normalized.Length > maxLength
            || normalized.Any(char.IsControl))
        {
            throw new InvalidDataException($"{fieldName}格式无效");
        }
        return normalized;
    }

    private static string NormalizeIdentifier(string? value, string fieldName)
    {
        string normalized = value?.Trim() ?? "";
        if (!IdentifierPattern.IsMatch(normalized))
            throw new InvalidDataException($"{fieldName}格式无效");
        return normalized;
    }

    private static string CreateCredential() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static bool FixedTimeCredentialEquals(string expected, string actual)
    {
        try
        {
            byte[] left = Convert.FromHexString(expected);
            byte[] right = Convert.FromHexString(actual?.Trim() ?? "");
            return left.Length == right.Length
                && CryptographicOperations.FixedTimeEquals(left, right);
        }
        catch
        {
            return false;
        }
    }

    private void Save()
    {
        var document = new StoredDocument
        {
            SchemaVersion = SchemaVersion,
            Authorizations = _authorizations.Values
                .OrderBy(state => state.ExtensionInstanceId, StringComparer.Ordinal)
                .Select(ToStoredAuthorization)
                .ToArray()
        };
        string temporary = _registryPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document), Encoding.UTF8);
        File.Move(temporary, _registryPath, overwrite: true);
    }

    private void Load()
    {
        if (!File.Exists(_registryPath))
            return;
        try
        {
            StoredDocument? document = JsonSerializer.Deserialize<StoredDocument>(
                File.ReadAllText(_registryPath, Encoding.UTF8));
            if (document == null || document.SchemaVersion != SchemaVersion)
                throw new InvalidDataException("扩展授权文件版本不受支持");

            foreach (StoredAuthorization item in document.Authorizations ?? [])
            {
                try
                {
                    AuthorizationState state = FromStoredAuthorization(item);
                    _authorizations[state.ExtensionInstanceId] = state;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn(
                        "ExtensionAuthorization",
                        $"跳过无效扩展授权 id={SafeIdentifier(item.ExtensionInstanceId)}, error={ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("ExtensionAuthorization", $"扩展授权文件无法加载，已有令牌将失效 error={ex.Message}");
        }
    }

    private StoredAuthorization ToStoredAuthorization(AuthorizationState state)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plain = Encoding.UTF8.GetBytes(state.Credential);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];
        using var aes = new AesGcm(_encryptionKey, tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag, BuildAssociatedData(state.ExtensionInstanceId, state.CredentialGeneration));
        return new StoredAuthorization
        {
            ExtensionInstanceId = state.ExtensionInstanceId,
            ProviderId = state.ProviderId,
            DisplayName = state.DisplayName,
            Version = state.Version,
            Source = state.Source,
            Permissions = state.Permissions,
            Capabilities = state.Capabilities,
            RoutingScope = state.RoutingScope,
            BoundOriginNodeIds = state.BoundOriginNodeIds,
            CredentialGeneration = state.CredentialGeneration,
            CredentialCipherText = Convert.ToBase64String(cipher),
            CredentialNonce = Convert.ToBase64String(nonce),
            CredentialTag = Convert.ToBase64String(tag),
            ApprovedAtUtc = state.ApprovedAtUtc,
            UpdatedAtUtc = state.UpdatedAtUtc,
            RevokedAtUtc = state.RevokedAtUtc,
            LastBusinessActivityUtc = state.LastBusinessActivityUtc,
            LastBusinessDataCount = state.LastBusinessDataCount
        };
    }

    private AuthorizationState FromStoredAuthorization(StoredAuthorization item)
    {
        var approval = new ExtensionAuthorizationApproval
        {
            ExtensionInstanceId = item.ExtensionInstanceId,
            ProviderId = item.ProviderId,
            DisplayName = item.DisplayName,
            Version = item.Version,
            Source = item.Source,
            Permissions = item.Permissions ?? [],
            Capabilities = item.Capabilities ?? [],
            RoutingScope = item.RoutingScope,
            BoundOriginNodeIds = item.BoundOriginNodeIds ?? []
        };
        ExtensionAuthorizationApproval normalized = NormalizeApproval(approval);
        if (item.CredentialGeneration <= 0
            || item.ApprovedAtUtc == default
            || item.UpdatedAtUtc < item.ApprovedAtUtc)
        {
            throw new InvalidDataException("扩展授权时间或凭据代次无效");
        }

        byte[] cipher = Convert.FromBase64String(item.CredentialCipherText);
        byte[] plain = new byte[cipher.Length];
        using var aes = new AesGcm(_encryptionKey, 16);
        aes.Decrypt(
            Convert.FromBase64String(item.CredentialNonce),
            cipher,
            Convert.FromBase64String(item.CredentialTag),
            plain,
            BuildAssociatedData(normalized.ExtensionInstanceId, item.CredentialGeneration));
        string credential = Encoding.UTF8.GetString(plain);
        if (item.RevokedAtUtc == null && !IsCredentialFormatValid(credential))
            throw new InvalidDataException("扩展凭据格式无效");
        if (item.RevokedAtUtc != null && credential.Length != 0)
            throw new InvalidDataException("已撤销扩展仍包含有效凭据");

        return new AuthorizationState
        {
            ExtensionInstanceId = normalized.ExtensionInstanceId,
            ProviderId = normalized.ProviderId,
            DisplayName = normalized.DisplayName,
            Version = normalized.Version,
            Source = normalized.Source,
            Permissions = normalized.Permissions.ToArray(),
            Capabilities = normalized.Capabilities.ToArray(),
            RoutingScope = normalized.RoutingScope,
            BoundOriginNodeIds = normalized.BoundOriginNodeIds.ToArray(),
            CredentialGeneration = item.CredentialGeneration,
            Credential = credential,
            ApprovedAtUtc = item.ApprovedAtUtc,
            UpdatedAtUtc = item.UpdatedAtUtc,
            RevokedAtUtc = item.RevokedAtUtc,
            LastBusinessActivityUtc = item.LastBusinessActivityUtc,
            LastBusinessDataCount = Math.Max(0, item.LastBusinessDataCount)
        };
    }

    private static bool IsCredentialFormatValid(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static byte[] BuildAssociatedData(string extensionInstanceId, int generation) =>
        Encoding.UTF8.GetBytes($"packingproof-extension-credential-v1\n{extensionInstanceId}\n{generation}");

    private static byte[] LoadOrCreateRootKey(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                byte[] existing = Convert.FromBase64String(File.ReadAllText(path, Encoding.UTF8).Trim());
                if (existing.Length == 32)
                    return existing;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("ExtensionAuthorization", $"扩展根密钥无法读取，将重新生成 error={ex.Message}");
            }
        }

        byte[] created = RandomNumberGenerator.GetBytes(32);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, Convert.ToBase64String(created), Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
        return created;
    }

    private static ExtensionAuthorizationContext ToContext(AuthorizationState state) => new()
    {
        ExtensionInstanceId = state.ExtensionInstanceId,
        ProviderId = state.ProviderId,
        DisplayName = state.DisplayName,
        Version = state.Version,
        Source = state.Source,
        Permissions = state.Permissions.ToArray(),
        Capabilities = state.Capabilities.ToArray(),
        RoutingScope = state.RoutingScope,
        BoundOriginNodeIds = state.BoundOriginNodeIds.ToArray(),
        CredentialGeneration = state.CredentialGeneration,
        ApprovedAtUtc = state.ApprovedAtUtc,
        UpdatedAtUtc = state.UpdatedAtUtc,
        RevokedAtUtc = state.RevokedAtUtc,
        LastSeenUtc = state.LastSeenUtc,
        LastBusinessActivityUtc = state.LastBusinessActivityUtc,
        LastBusinessDataCount = state.LastBusinessDataCount,
        RuntimeVersion = state.RuntimeVersion
    };

    private static string SafeIdentifier(string? value)
    {
        string normalized = value?.Trim() ?? "";
        return normalized.Length <= 12 ? normalized : normalized[..12];
    }

    private sealed class AuthorizationState
    {
        internal string ExtensionInstanceId { get; init; } = "";
        internal string ProviderId { get; init; } = "";
        internal string DisplayName { get; init; } = "";
        internal string Version { get; init; } = "";
        internal string Source { get; init; } = "";
        internal string[] Permissions { get; init; } = [];
        internal string[] Capabilities { get; init; } = [];
        internal ExtensionRoutingScope RoutingScope { get; init; }
        internal string[] BoundOriginNodeIds { get; init; } = [];
        internal int CredentialGeneration { get; set; }
        internal string Credential { get; set; } = "";
        internal DateTimeOffset ApprovedAtUtc { get; init; }
        internal DateTimeOffset UpdatedAtUtc { get; set; }
        internal DateTimeOffset? RevokedAtUtc { get; set; }
        internal DateTimeOffset? LastSeenUtc { get; set; }
        internal DateTimeOffset? LastBusinessActivityUtc { get; set; }
        internal int LastBusinessDataCount { get; set; }
        internal string RuntimeVersion { get; set; } = "";
    }

    private sealed class StoredDocument
    {
        public int SchemaVersion { get; set; }
        public StoredAuthorization[] Authorizations { get; set; } = [];
    }

    private sealed class StoredAuthorization
    {
        public string ExtensionInstanceId { get; set; } = "";
        public string ProviderId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Version { get; set; } = "";
        public string Source { get; set; } = "";
        public string[] Permissions { get; set; } = [];
        public string[] Capabilities { get; set; } = [];
        public ExtensionRoutingScope RoutingScope { get; set; }
        public string[] BoundOriginNodeIds { get; set; } = [];
        public int CredentialGeneration { get; set; }
        public string CredentialCipherText { get; set; } = "";
        public string CredentialNonce { get; set; } = "";
        public string CredentialTag { get; set; } = "";
        public DateTimeOffset ApprovedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public DateTimeOffset? RevokedAtUtc { get; set; }
        public DateTimeOffset? LastBusinessActivityUtc { get; set; }
        public int LastBusinessDataCount { get; set; }
    }
}
