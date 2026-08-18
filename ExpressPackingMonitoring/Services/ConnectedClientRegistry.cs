using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ExpressPackingMonitoring.Services;

internal sealed class ConnectedClientRegistry : IDisposable
{
    internal const int HeartbeatIntervalSeconds = 15;
    internal const int ExpirationSeconds = 45;
    internal const int MaxClients = 256;
    internal const int MaxClientsPerAddress = 16;

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        "web-desktop",
        "web-mobile",
        "userscript",
        "print-station",
        "recording-workstation",
        "mobile-app"
    };

    private static readonly Regex ClientIdPattern = new(
        "^[A-Za-z0-9._:-]{8,128}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ConcurrentDictionary<string, ConnectedClientInfo> _clients = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly ITimer? _cleanupTimer;
    private bool _disposed;

    public ConnectedClientRegistry(TimeProvider? timeProvider = null, bool startCleanupTimer = true)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (startCleanupTimer)
        {
            _cleanupTimer = _timeProvider.CreateTimer(
                _ => PruneExpired(),
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));
        }
    }

    public event Action<IReadOnlyList<ConnectedClientInfo>>? Changed;

    public void Heartbeat(ConnectedClientHeartbeat heartbeat, string remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        string clientId = NormalizeClientId(heartbeat.ClientId);
        string clientType = NormalizeClientType(heartbeat.ClientType);
        string displayName = NormalizeDisplayName(heartbeat.DisplayName);
        string address = string.IsNullOrWhiteSpace(remoteAddress) ? "unknown" : remoteAddress.Trim();
        string nodeId = NormalizeOptionalIdentifier(heartbeat.NodeId, clientId);
        string deviceType = NormalizeDeviceType(heartbeat.DeviceType);
        int orderReceiverPort = heartbeat.OrderReceiverPort is > 0 and <= 65535
            ? heartbeat.OrderReceiverPort.Value
            : MobileOrderReceiverRegistry.OrderReceiverPort;
        string[] capabilities = NormalizeCapabilities(heartbeat.Capabilities);
        string appVersion = NormalizeAppVersion(heartbeat.AppVersion);
        int appBuildNumber = heartbeat.AppBuildNumber.GetValueOrDefault();
        if (appBuildNumber < 0)
            appBuildNumber = 0;
        string key = BuildKey(clientType, clientId);

        if (heartbeat.Connected == false)
        {
            if (_clients.TryRemove(key, out _)) RaiseChanged();
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        bool changed = false;
        _clients.AddOrUpdate(
            key,
            _ =>
            {
                EnsureCapacity(address);
                changed = true;
                return new ConnectedClientInfo(
                    clientId,
                    clientType,
                    displayName,
                    address,
                    now,
                    nodeId,
                    deviceType,
                    orderReceiverPort,
                    capabilities,
                    appVersion,
                    appBuildNumber);
            },
            (_, existing) =>
            {
                if (!string.Equals(existing.RemoteAddress, address, StringComparison.OrdinalIgnoreCase))
                    EnsureAddressCapacity(address, key);
                // LastSeenUtc changes on every heartbeat; notify consumers so UI online indicators
                // do not remain stuck on the snapshot captured before the first heartbeat.
                changed = true;
                if (!string.Equals(existing.DisplayName, displayName, StringComparison.Ordinal)
                    || !string.Equals(existing.RemoteAddress, address, StringComparison.Ordinal))
                {
                    changed = true;
                }
                if (!string.Equals(existing.AppVersion, appVersion, StringComparison.Ordinal)
                    || existing.AppBuildNumber != appBuildNumber)
                    changed = true;
                return existing with
                {
                    DisplayName = displayName,
                    RemoteAddress = address,
                    LastSeenUtc = now,
                    NodeId = nodeId,
                    DeviceType = deviceType,
                    OrderReceiverPort = orderReceiverPort,
                    Capabilities = capabilities,
                    AppVersion = appVersion,
                    AppBuildNumber = appBuildNumber
                };
            });

        if (changed) RaiseChanged();
    }

    public IReadOnlyList<ConnectedClientInfo> GetSnapshot()
    {
        PruneExpired();
        return SnapshotCore();
    }

    internal static int CountDistinctAddresses(IEnumerable<ConnectedClientInfo>? clients)
    {
        return (clients ?? Array.Empty<ConnectedClientInfo>())
            .Select(client => NormalizeRemoteAddress(client.RemoteAddress))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    internal void PruneExpired()
    {
        if (_disposed) return;
        DateTimeOffset cutoff = _timeProvider.GetUtcNow().AddSeconds(-ExpirationSeconds);
        bool changed = false;
        foreach ((string key, ConnectedClientInfo client) in _clients)
        {
            if (client.LastSeenUtc >= cutoff) continue;
            changed |= _clients.TryRemove(key, out _);
        }
        if (changed) RaiseChanged();
    }

    private void EnsureCapacity(string address)
    {
        PruneExpired();
        if (_clients.Count >= MaxClients)
            throw new ConnectedClientValidationException("connection_registry_full", "在线设备数量已达到上限");
        EnsureAddressCapacity(address, ignoreKey: null);
    }

    private void EnsureAddressCapacity(string address, string? ignoreKey)
    {
        int addressCount = _clients.Values.Count(client =>
            string.Equals(client.RemoteAddress, address, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(BuildKey(client.ClientType, client.ClientId), ignoreKey, StringComparison.Ordinal));
        if (addressCount >= MaxClientsPerAddress)
            throw new ConnectedClientValidationException("too_many_clients", "当前设备注册的连接端过多");
    }

    private void RaiseChanged()
    {
        IReadOnlyList<ConnectedClientInfo> snapshot = SnapshotCore();
        try { Changed?.Invoke(snapshot); } catch { }
    }

    private IReadOnlyList<ConnectedClientInfo> SnapshotCore() => _clients.Values
        .OrderBy(client => client.ClientType, StringComparer.Ordinal)
        .ThenBy(client => client.DisplayName, StringComparer.Ordinal)
        .ThenBy(client => client.ClientId, StringComparer.Ordinal)
        .ToArray();

    private static string NormalizeClientId(string? value)
    {
        string result = value?.Trim() ?? "";
        if (!ClientIdPattern.IsMatch(result))
            throw new ConnectedClientValidationException("invalid_client_id", "clientId 必须为 8 到 128 位字母、数字或 . _ : -");
        return result;
    }

    private static string NormalizeClientType(string? value)
    {
        string result = value?.Trim().ToLowerInvariant() ?? "";
        if (!AllowedTypes.Contains(result))
            throw new ConnectedClientValidationException("invalid_client_type", "不支持的 clientType");
        return result;
    }

    private static string NormalizeDisplayName(string? value)
    {
        string result = value?.Trim() ?? "";
        if (result.Length == 0 || result.Length > 64 || result.Any(char.IsControl))
            throw new ConnectedClientValidationException("invalid_display_name", "displayName 不能为空且最多 64 个字符");
        return result;
    }

    private static string NormalizeOptionalIdentifier(string? value, string fallback)
    {
        string result = value?.Trim() ?? "";
        return result.Length is >= 8 and <= 128
            && result.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or ':' or '-')
                ? result
                : fallback;
    }

    private static string NormalizeAppVersion(string? value)
    {
        string result = value?.Trim() ?? "";
        return result.Length <= 32 ? result : "";
    }

    private static string NormalizeDeviceType(string? value)
    {
        string result = value?.Trim().ToLowerInvariant() ?? "";
        return result is "mobile" or "pc" ? result : "";
    }

    private static string[] NormalizeCapabilities(IEnumerable<string>? values) =>
        (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value.Length <= 64)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();

    private static string BuildKey(string clientType, string clientId) => $"{clientType}:{clientId}";

    private static string NormalizeRemoteAddress(string? value)
    {
        string address = value?.Trim() ?? "unknown";
        if (System.Net.IPAddress.TryParse(address, out System.Net.IPAddress? parsed)
            && parsed.IsIPv4MappedToIPv6)
            return parsed.MapToIPv4().ToString();
        return address;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer?.Dispose();
        _clients.Clear();
    }
}

internal sealed class ConnectedClientHeartbeat
{
    public string ClientId { get; set; } = "";
    public string ClientType { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool NicknameCustomized { get; set; }
    public bool? Connected { get; set; }
    public string NodeId { get; set; } = "";
    public string DeviceType { get; set; } = "";
    public int? OrderReceiverPort { get; set; }
    public string[] Capabilities { get; set; } = [];
    public string AppVersion { get; set; } = "";
    public int? AppBuildNumber { get; set; }
}

internal sealed record ConnectedClientInfo(
    string ClientId,
    string ClientType,
    string DisplayName,
    string RemoteAddress,
    DateTimeOffset LastSeenUtc,
    string NodeId,
    string DeviceType,
    int OrderReceiverPort,
    IReadOnlyList<string> Capabilities,
    string AppVersion = "",
    int AppBuildNumber = 0);

internal sealed class ConnectedClientValidationException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
