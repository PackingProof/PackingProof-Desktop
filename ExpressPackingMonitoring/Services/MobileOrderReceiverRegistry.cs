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
    /// <summary>昵称台账上限。设备本身按保存期清理，但名字要留得更久，见 <see cref="_rememberedNames"/>。</summary>
    private const int MaximumRememberedNames = 512;
    private readonly string _path;
    private readonly string _namesPath;
    private readonly Func<DateTime> _utcNow;
    private readonly object _sync = new();
    private List<Entry> _entries;
    /// <summary>
    /// 设备号 -> 最近一次分配到的昵称。昵称是可变属性（用户随时会改），
    /// 录像记录里的 SourceDeviceName 只是写入当时的快照；设备掉出 30 天保留期后，
    /// 界面仍要靠这份台账把老记录认成同一台设备，否则同一台设备改名后会在列表里
    /// 变成两个名字。台账只用于显示名解析，不参与编号与重名判定。
    /// </summary>
    private Dictionary<string, RememberedName> _rememberedNames;

    internal MobileOrderReceiverRegistry(string? path = null, Func<DateTime>? utcNow = null)
    {
        _path = path ?? GetDefaultPath();
        _namesPath = Path.Combine(
            Path.GetDirectoryName(_path) ?? "",
            Path.GetFileNameWithoutExtension(_path) + "-names.json");
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _entries = Load(_path);
        _rememberedNames = LoadRememberedNames(_namesPath);
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
        bool customized = false,
        bool trustProvidedName = false)
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
            else if (!trustProvidedName && IsAutomaticName(normalizedNodeName) && !customized)
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
            RememberName(entry.NodeId, entry.NodeName, now);
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
            RememberName(entry.NodeId, entry.NodeName, _utcNow());
            try { Save(); } catch { }
            return true;
        }
    }

    /// <summary>
    /// 台账里记住的设备昵称，按最近一次出现排序。给显示名解析当兜底：设备掉出保留期后，
    /// 老记录仍按这份名字显示，不会因为记录里的历史快照又冒出一个旧昵称。
    /// </summary>
    internal IReadOnlyList<RememberedDeviceName> GetRememberedNames()
    {
        lock (_sync)
        {
            return _rememberedNames
                .Where(pair => pair.Key.Length > 0 && !string.IsNullOrWhiteSpace(pair.Value.Name))
                .OrderByDescending(pair => pair.Value.SeenUtc)
                .Select(pair => new RememberedDeviceName(pair.Key, pair.Value.Name.Trim()))
                .ToArray();
        }
    }

    private void RememberName(string? nodeId, string? name, DateTime seenUtc)
    {
        string id = nodeId?.Trim() ?? "";
        string value = name?.Trim() ?? "";
        if (id.Length == 0 || value.Length == 0)
            return;

        _rememberedNames[id] = new RememberedName { Name = value, SeenUtc = seenUtc };
        if (_rememberedNames.Count > MaximumRememberedNames)
        {
            foreach (string expired in _rememberedNames
                .OrderByDescending(pair => pair.Value.SeenUtc)
                .Skip(MaximumRememberedNames)
                .Select(pair => pair.Key)
                .ToArray())
            {
                _rememberedNames.Remove(expired);
            }
        }

        try { SaveRememberedNames(); } catch { }
    }

    private void SaveRememberedNames()
    {
        string? directory = Path.GetDirectoryName(_namesPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporaryPath = _namesPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_rememberedNames));
        File.Move(temporaryPath, _namesPath, true);
    }

    private static Dictionary<string, RememberedName> LoadRememberedNames(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Dictionary<string, RememberedName>(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, RememberedName>>(File.ReadAllText(path))
                ?? new Dictionary<string, RememberedName>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, RememberedName>(StringComparer.OrdinalIgnoreCase);
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
            RememberName(entry.NodeId, replacement, entry.LastSeenUtc);
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
        Customized: item.Customized,
        LastSeenUtc: item.LastSeenUtc);

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

    /// <summary>台账条目：这台设备上一次叫什么名字、什么时候。</summary>
    private sealed class RememberedName
    {
        public string Name { get; set; } = "";
        public DateTime SeenUtc { get; set; }
    }
}

internal sealed record MobileOrderReceiverInfo(
    string NodeId,
    string NodeName,
    string Address,
    int Port,
    IReadOnlyList<string> Capabilities,
    bool Online,
    bool Customized = false,
    DateTime LastSeenUtc = default);

/// <summary>台账里记住的一台设备的昵称（设备已经掉出保留期时用来兜底显示名）。</summary>
internal sealed record RememberedDeviceName(string NodeId, string Name);
