using ExpressPackingMonitoring.Config;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

internal sealed class MobileOrderReceiverRegistry
{
    internal const int OrderReceiverPort = 5280;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly TimeSpan ActiveRetention = TimeSpan.FromSeconds(45);
    private readonly string _path;
    private readonly Func<DateTime> _utcNow;
    private readonly object _sync = new();
    private List<Entry> _entries;

    internal MobileOrderReceiverRegistry(string? path = null, Func<DateTime>? utcNow = null)
    {
        _path = path ?? GetDefaultPath();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _entries = Load(_path);
        if (RepairDuplicateAutomaticNames())
        {
            try { Save(); } catch { }
        }
    }

    internal MobileOrderReceiverInfo? Register(
        IPAddress? remoteAddress,
        string? nodeId = null,
        string? nodeName = null,
        int? orderReceiverPort = null,
        IEnumerable<string>? capabilities = null,
        string? deviceKind = null,
        string? platform = null)
    {
        string? address = NormalizePrivateIpv4(remoteAddress);
        if (address == null) return null;

        lock (_sync)
        {
            DateTime now = _utcNow();
            string requestedNodeId = nodeId?.Trim() ?? "";
            Entry? existing = requestedNodeId.Length > 0
                ? _entries.FirstOrDefault(item =>
                    string.Equals(item.NodeId, requestedNodeId, StringComparison.OrdinalIgnoreCase))
                : _entries.FirstOrDefault(item =>
                    string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase));
            _entries.RemoveAll(item =>
                (requestedNodeId.Length > 0
                    ? string.Equals(item.NodeId, requestedNodeId, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase))
                || now - item.LastSeenUtc > Retention);

            string normalizedNodeId = requestedNodeId;
            if (normalizedNodeId.Length == 0)
                normalizedNodeId = existing?.NodeId ?? CreateFallbackNodeId(address);
            string normalizedNodeName = nodeName?.Trim() ?? "";
            if (IsAutomaticName(normalizedNodeName))
            {
                bool sameStableDevice = existing != null
                    && (requestedNodeId.Length == 0
                        || string.Equals(existing.NodeId, requestedNodeId, StringComparison.OrdinalIgnoreCase));
                string prefix = GetNamePrefix(deviceKind, platform);
                bool existingAutomatic = existing != null && IsAutomaticName(existing.NodeName);
                bool existingUsesPrefix = existingAutomatic && existing!.NodeName.StartsWith(prefix, StringComparison.Ordinal)
                    && IsAssignedDeviceName(existing.NodeName);
                normalizedNodeName = sameStableDevice && existingUsesPrefix
                    ? existing!.NodeName
                    : CreateNextMobileName(existing?.NodeName, prefix);
            }
            int normalizedPort = orderReceiverPort is > 0 and <= 65535
                ? orderReceiverPort.Value
                : existing?.Port is > 0 and <= 65535
                    ? existing.Port
                    : OrderReceiverPort;
            string[] normalizedCapabilities = (capabilities ?? existing?.Capabilities ??
                [PackingProofCapabilities.Recording, PackingProofCapabilities.OrderReceiver])
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var entry = new Entry
            {
                Address = address,
                LastSeenUtc = now,
                NodeId = normalizedNodeId,
                NodeName = normalizedNodeName,
                DeviceKind = deviceKind?.Trim() ?? "",
                Platform = platform?.Trim().ToLowerInvariant() ?? "",
                Port = normalizedPort,
                Capabilities = normalizedCapabilities
            };
            _entries.Insert(0, entry);
            try { Save(); } catch { }
            return ToInfo(entry, online: true);
        }
    }

    internal IReadOnlyList<string> GetAuthorities()
    {
        lock (_sync)
        {
            DateTime now = _utcNow();
            return _entries
                .Where(item => now - item.LastSeenUtc <= Retention)
                .OrderByDescending(item => item.LastSeenUtc)
                .Select(item => $"{item.Address}:{NormalizePort(item.Port)}")
                .ToArray();
        }
    }

    internal static IReadOnlyList<string> GetDefaultAuthorities() =>
        new MobileOrderReceiverRegistry().GetAuthorities();

    internal IReadOnlyList<MobileOrderReceiverInfo> GetRecordingDevices()
    {
        lock (_sync)
        {
            DateTime now = _utcNow();
            return _entries
                .Where(item => now - item.LastSeenUtc <= ActiveRetention)
                .OrderByDescending(item => item.LastSeenUtc)
                .Select(item => ToInfo(item, online: true))
                .ToArray();
        }
    }

    internal IReadOnlyList<MobileOrderReceiverInfo> GetKnownRecordingDevices()
    {
        lock (_sync)
        {
            DateTime now = _utcNow();
            return _entries
                .Where(item => now - item.LastSeenUtc <= Retention)
                .OrderByDescending(item => item.LastSeenUtc)
                .Select(item => ToInfo(item, now - item.LastSeenUtc <= ActiveRetention))
                .ToArray();
        }
    }

    private string CreateNextMobileName(string? reservedName, string prefix)
    {
        IEnumerable<string> names = _entries
            .Select(item => item.NodeName?.Trim() ?? "")
            .Append(reservedName?.Trim() ?? "")
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal));
        int nextNumber = names
            .Select(name => int.TryParse(name[prefix.Length..], out int number) ? number : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        return $"{prefix}{nextNumber}";
    }

    private bool RepairDuplicateAutomaticNames()
    {
        bool changed = false;
        HashSet<string> used = new(
            _entries.Select(item => item.NodeName?.Trim() ?? "")
                .Where(name => name.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        foreach (Entry entry in _entries.OrderByDescending(item => item.LastSeenUtc))
        {
            string name = entry.NodeName?.Trim() ?? "";
            if (!IsAutomaticName(name) || !IsAssignedDeviceName(name))
                continue;
            if (used.Remove(name))
                continue;

            string replacement = CreateNextMobileName(null, "从机");
            while (!used.Add(replacement))
                replacement = CreateNextMobileName(replacement, "从机");
            entry.NodeName = replacement;
            changed = true;
        }
        return changed;
    }

    private static string GetNamePrefix(string? deviceKind, string? platform)
    {
        string normalizedPlatform = platform?.Trim().ToLowerInvariant() ?? "";
        if (normalizedPlatform is "android") return "安卓";
        if (normalizedPlatform is "ios" or "iphone" or "ipad") return "苹果";
        if (string.Equals(deviceKind?.Trim(), "pc", StringComparison.OrdinalIgnoreCase)
            || normalizedPlatform is "windows" or "macos" or "mac")
            return "电脑";
        return "从机";
    }

    private static bool IsAutomaticName(string? name)
    {
        string value = name?.Trim() ?? "";
        return value.Length == 0
            || value.Equals("本机", StringComparison.Ordinal)
            || value.Equals("设备", StringComparison.Ordinal)
            || value.StartsWith("设备 ", StringComparison.Ordinal)
            || value.StartsWith("手机录像设备 ", StringComparison.Ordinal)
            || IsAssignedDeviceName(value);
    }

    private static bool IsAssignedDeviceName(string? name)
    {
        string value = name?.Trim() ?? "";
        foreach (string prefix in new[] { "手机", "安卓", "苹果", "电脑", "从机" })
            if (value.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(value[prefix.Length..], out int number)
                && number > 0)
                return true;
        return false;
    }

    private static MobileOrderReceiverInfo ToInfo(Entry item, bool online) => new(
        item.NodeId,
        item.NodeName,
        item.Address,
        NormalizePort(item.Port),
        item.Capabilities?.Length > 0
            ? item.Capabilities
            : [PackingProofCapabilities.Recording, PackingProofCapabilities.OrderReceiver],
        Online: online);

    private static int NormalizePort(int port) =>
        port is > 0 and <= 65535 ? port : OrderReceiverPort;

    internal static string GetDefaultPath() =>
        Path.Combine(AppPaths.MobileBackupStateDir, "order-receivers.json");

    private static string? NormalizePrivateIpv4(IPAddress? address)
    {
        if (address == null) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return null;

        byte[] bytes = address.GetAddressBytes();
        bool isPrivate = bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
        return isPrivate ? address.ToString() : null;
    }

    private static string CreateFallbackNodeId(string address)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"packingproof-mobile:{address}"));
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private static List<Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<Entry>();
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new List<Entry>();
        }
        catch
        {
            return new List<Entry>();
        }
    }

    private void Save()
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_entries));
        File.Move(temporaryPath, _path, true);
    }

    private sealed class Entry
    {
        public string Address { get; set; } = "";
        public DateTime LastSeenUtc { get; set; }
        public string NodeId { get; set; } = "";
        public string NodeName { get; set; } = "";
        public string DeviceKind { get; set; } = "";
        public string Platform { get; set; } = "";
        public int Port { get; set; } = OrderReceiverPort;
        public string[] Capabilities { get; set; } = [];
    }
}

internal sealed record MobileOrderReceiverInfo(
    string NodeId,
    string NodeName,
    string Address,
    int Port,
    IReadOnlyList<string> Capabilities,
    bool Online);
