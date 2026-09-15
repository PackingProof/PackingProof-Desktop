using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
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
    /// <summary>设备条数硬上限。有录像的设备最后才动，见 <see cref="TrimOverflow"/>。</summary>
    private const int MaximumKnownDevices = 512;
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
        bool customized = false,
        bool trustProvidedName = false,
        bool hasRecordings = false)
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
                // 用户改过名、或者在这台主机上留下过录像的设备都不按活跃时间清理：
                // 昵称映射是录像显示名的唯一来源，设备一旦被清掉，它的老录像就只能退回
                // 记录里的历史快照（同一台设备又会变成两个名字）。
                || (!item.Customized && !item.HasRecordings && now - item.LastSeenUtc > Retention));
            TrimCustomizedOverflow();
            TrimOverflow();

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
                Customized = customized || existing?.Customized == true,
                // 有录像的设备不清：这台主机还要靠它显示老录像的来源名。
                HasRecordings = hasRecordings || existing?.HasRecordings == true
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
                // 用户改过名、或者有录像的设备即使长期不在线也要留在列表里：
                // 昵称不能因为设备离线超过保留期就退回记录里的老快照名。
                .Where(item => item.Customized || item.HasRecordings || now - item.LastSeenUtc <= Retention)
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

    /// <summary>
    /// 用库里已有的录像来源补齐"设备号 -> 昵称"映射。
    ///
    /// 昵称只存在这张表里（记录不再逐条写昵称），升级前的昵称只存在于记录快照中，
    /// 而设备可能早就掉出保留期了，所以启动时按库里的来源补一次：只补没有登记过的设备，
    /// 名字取这台设备最近一条记录里的名字；同名时按最近还有录像的那台优先，
    /// 另一台留空（界面按设备号生成兜底名），保证一个名字只属于一台设备。
    /// </summary>
    internal void SeedRecordedDevices(IEnumerable<VideoSourceInfo>? sources)
    {
        if (sources == null)
            return;

        lock (_sync)
        {
            var usedNames = new HashSet<string>(
                _entries.Select(item => item.NodeName?.Trim() ?? "").Where(name => name.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            foreach (VideoSourceInfo source in sources
                .Where(item => string.Equals(item.SourceType, "external", StringComparison.OrdinalIgnoreCase))
                .Where(item => !string.IsNullOrWhiteSpace(item.DeviceId))
                .OrderByDescending(item => item.LastRecordUtc))
            {
                string nodeId = source.DeviceId.Trim();
                if (_entries.Any(item => string.Equals(item.NodeId, nodeId, StringComparison.OrdinalIgnoreCase)))
                    continue;

                string name = source.DeviceName?.Trim() ?? "";
                if (name.Length > 0 && !usedNames.Add(name))
                    name = "";
                _entries.Add(new Entry
                {
                    NodeId = nodeId,
                    NodeName = name,
                    LastSeenUtc = source.LastRecordUtc,
                    Capabilities = [PackingProofCapabilities.Recording, PackingProofCapabilities.OrderReceiver],
                    HasRecordings = true
                });
                changed = true;
            }

            if (!changed)
                return;

            TrimOverflow();
            try { Save(); } catch { }
        }
    }

    /// <summary>
    /// 条数上限保护。先清"没有录像、也不是用户改名"的最老设备，
    /// 只有在剩下的全都有录像时才动到有录像的设备。
    /// </summary>
    private void TrimOverflow()
    {
        if (_entries.Count <= MaximumKnownDevices)
            return;

        foreach (Entry entry in _entries
            .OrderBy(item => item.HasRecordings)
            .ThenBy(item => item.Customized)
            .ThenBy(item => item.LastSeenUtc)
            .Take(_entries.Count - MaximumKnownDevices)
            .ToArray())
        {
            _entries.Remove(entry);
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

        /// <summary>
        /// 这台设备在主机上留下过录像。有录像的设备连同昵称一起长期保留：
        /// 昵称映射是录像显示名的唯一来源，清掉它老录像就只剩设备号了。
        /// </summary>
        public bool HasRecordings { get; set; }
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
