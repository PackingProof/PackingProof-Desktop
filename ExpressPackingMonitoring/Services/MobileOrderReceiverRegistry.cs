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
    /// <summary>用户自定义昵称的保留条数上限：这类名字不随活跃时间清理，但也不能无限增长。</summary>
    private const int MaximumCustomizedNames = 128;
    private readonly string _path;
    private readonly Func<DateTime> _utcNow;
    private readonly object _sync = new();
    private List<Entry> _entries;

    internal MobileOrderReceiverRegistry(string? path = null, Func<DateTime>? utcNow = null)
    {
        _path = path ?? GetDefaultPath();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _entries = Load(_path);
        if (RepairDuplicateNames())
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
        string? platform = null,
        bool customized = false)
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
                // 用户改过名的设备不按活跃时间清理：否则 30 天后自定义昵称会跟着丢，
                // 筛选与列表又会退回记录里的老快照名。
                || (!item.Customized && now - item.LastSeenUtc > Retention));
            TrimCustomizedOverflow();

            string normalizedNodeId = requestedNodeId;
            if (normalizedNodeId.Length == 0)
                normalizedNodeId = existing?.NodeId ?? CreateFallbackNodeId(address);
            string normalizedNodeName = nodeName?.Trim() ?? "";
            string normalizedDeviceKind = deviceKind?.Trim() ?? "";
            string normalizedPlatform = platform?.Trim().ToLowerInvariant() ?? "";
            // 备份上传、能力查询这些路径不带平台与设备类型，之前每次注册都按"从机"重新命名，
            // 同一台安卓手机在不同路径下就会拿到不同前缀，界面里看起来像被改过名。
            // 这里沿用首次识别到的类型与平台，保证昵称前缀稳定。
            if (existing != null)
            {
                if (normalizedDeviceKind.Length == 0) normalizedDeviceKind = existing.DeviceKind;
                if (normalizedPlatform.Length == 0) normalizedPlatform = existing.Platform;
            }

            // 用户手动改过的昵称不许被自动命名覆盖。手机每 15 秒心跳都会把本地缓存的
            // 旧自动名报回来，没有这一位的话改好的名字下一次心跳就被改回去了；
            // 只有显式改名（customized=true）才能改动它。
            bool existingCustomized = existing?.Customized == true && !customized;
            string namePrefix = GetNamePrefix(normalizedDeviceKind, normalizedPlatform);
            if (existingCustomized)
            {
                normalizedNodeName = existing!.NodeName;
            }
            else if (IsAutomaticName(normalizedNodeName) && !customized)
            {
                bool sameStableDevice = existing != null
                    && (requestedNodeId.Length == 0
                        || string.Equals(existing.NodeId, requestedNodeId, StringComparison.OrdinalIgnoreCase));
                bool existingAutomatic = existing != null && IsAutomaticName(existing.NodeName);
                bool existingUsesPrefix = existingAutomatic && existing!.NodeName.StartsWith(namePrefix, StringComparison.Ordinal)
                    && IsAssignedDeviceName(existing.NodeName);
                normalizedNodeName = sameStableDevice && existingUsesPrefix
                    ? existing!.NodeName
                    : CreateNextMobileName(existing?.NodeName, namePrefix);
            }
            else if (IsNameTakenByAnotherDevice(normalizedNodeName, normalizedNodeId))
            {
                // 主机不允许两台设备同名：设备自己报来的名字如果撞名，
                // 就退回按平台自动编号，保证一台设备一个名字。
                normalizedNodeName = CreateNextMobileName(existing?.NodeName, namePrefix);
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
                DeviceKind = normalizedDeviceKind,
                Platform = normalizedPlatform,
                Port = normalizedPort,
                Capabilities = normalizedCapabilities,
                // 显式改名或此前已改名：保持"用户自定义"，自动命名不再覆盖。
                Customized = customized || existing?.Customized == true
            };
            _entries.Insert(0, entry);
            // 每次注册都顺手修一次重名：老版本曾经给多台设备发过同一个名字，
            // 那些设备只要上线就该被分开命名，不能等到重启才修。
            if (RepairDuplicateNames())
            {
                Entry? refreshed = _entries.FirstOrDefault(item =>
                    string.Equals(item.NodeId, normalizedNodeId, StringComparison.OrdinalIgnoreCase));
                if (refreshed != null)
                    entry = refreshed;
            }
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
                // 用户改过名的设备即使长期不在线也要保留在列表里：昵称不能因为
                // 设备离线超过保留期就退回记录里的老快照名。
                .Where(item => item.Customized || now - item.LastSeenUtc <= Retention)
                .OrderByDescending(item => item.LastSeenUtc)
                .Select(item => ToInfo(item, now - item.LastSeenUtc <= ActiveRetention))
                .ToArray();
        }
    }

    /// <summary>
    /// 设置用户自定义昵称。主机不允许两台设备同名：名字已被别的设备占用时直接拒绝，
    /// 由用户换一个名字，而不是悄悄改成"名字 2"。
    /// 改名后自动命名（含手机每 15 秒回灌的旧自动名）不再覆盖它，
    /// 设备掉出活跃期也不会连带丢掉这个名字。
    /// </summary>
    internal bool TrySetCustomName(string? nodeId, string? name, out string error)
    {
        error = "";
        string normalizedNodeId = nodeId?.Trim() ?? "";
        if (normalizedNodeId.Length == 0)
        {
            error = "找不到要改名的设备";
            return false;
        }

        string requested = name?.Trim() ?? "";
        if (requested.Length is < 1 or > 20 || requested.Any(char.IsControl))
        {
            error = "昵称需要 1 到 20 个字符，且不能包含换行或其他控制字符";
            return false;
        }

        lock (_sync)
        {
            Entry? entry = _entries.FirstOrDefault(item =>
                string.Equals(item.NodeId, normalizedNodeId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                error = "设备不存在，或还没有连接过这台主机";
                return false;
            }

            if (IsNameTakenByAnotherDevice(requested, normalizedNodeId))
            {
                error = $"已经有设备叫“{requested}”，请换一个名字";
                return false;
            }

            entry.NodeName = requested;
            entry.Customized = true;
            try { Save(); } catch { }
            return true;
        }
    }

    /// <summary>名字是否已被别的设备占用。主机不允许同名设备，改名与自动编号都要先问这一句。</summary>
    private bool IsNameTakenByAnotherDevice(string name, string nodeId)
    {
        string value = name?.Trim() ?? "";
        if (value.Length == 0)
            return false;

        return _entries.Any(item =>
            !string.Equals(item.NodeId, nodeId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.NodeName?.Trim(), value, StringComparison.OrdinalIgnoreCase));
    }

    private void TrimCustomizedOverflow()
    {
        if (_entries.Count(item => item.Customized) <= MaximumCustomizedNames)
            return;

        foreach (Entry entry in _entries
            .Where(item => item.Customized)
            .OrderByDescending(item => item.LastSeenUtc)
            .Skip(MaximumCustomizedNames)
            .ToArray())
        {
            _entries.Remove(entry);
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

        // 主机不允许同名设备：编号还要避开用户手改出来的名字（例如有人把设备命名为"安卓3"）。
        string candidate = $"{prefix}{nextNumber}";
        while (IsNameTakenByAnotherDevice(candidate, ""))
        {
            nextNumber++;
            candidate = $"{prefix}{nextNumber}";
        }

        return candidate;
    }

    /// <summary>
    /// 保证一台设备一个名字。老版本曾经给多台设备发过同一个名字，
    /// 用户手改的名字也可能与别的设备撞车；这里保留"用户改过的那台"和最近上线的设备，
    /// 其余重名的设备退回按平台自动编号。
    /// </summary>
    private bool RepairDuplicateNames()
    {
        bool changed = false;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Entry entry in _entries
            .OrderByDescending(item => item.Customized)
            .ThenByDescending(item => item.LastSeenUtc)
            .ToArray())
        {
            string name = entry.NodeName?.Trim() ?? "";
            if (name.Length == 0)
                continue;
            if (used.Add(name))
                continue;

            // 名字已被别的设备占用，昵称不能重复，只能重新编号。
            string prefix = GetNamePrefix(entry.DeviceKind, entry.Platform);
            string replacement = CreateNextMobileName(null, prefix);
            while (!used.Add(replacement))
                replacement = CreateNextMobileName(replacement, prefix);

            entry.NodeName = replacement;
            entry.Customized = false;
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
        Online: online,
        Customized: item.Customized);

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

        /// <summary>用户手动设置过昵称：自动命名与重名修复都不再改动它。</summary>
        public bool Customized { get; set; }
    }
}

internal sealed record MobileOrderReceiverInfo(
    string NodeId,
    string NodeName,
    string Address,
    int Port,
    IReadOnlyList<string> Capabilities,
    bool Online,
    bool Customized = false);
