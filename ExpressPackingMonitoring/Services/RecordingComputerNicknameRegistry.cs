using System.IO;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

internal sealed class RecordingComputerNicknameRegistry
{
    private const int MaxNameLength = 20;
    internal const int MaxKnownComputers = 128;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private readonly string _path;
    private readonly object _sync = new();
    private List<Entry> _entries;

    /// <summary>
    /// 另一张昵称表（手机登记表）的快照：设备号 → 昵称。
    ///
    /// 昵称在用户眼里只有一份，不能一台手机和一台电脑同名，所以两张表必须互相查。
    /// 取快照而不是回调对方查询是为了避开 AB-BA 死锁：两张表各有自己的锁，
    /// 在锁内互相调用时，一边在改名、另一边在处理心跳就会卡住。
    /// </summary>
    private readonly Func<IReadOnlyDictionary<string, string>> _externalNicknames;

    private IReadOnlyDictionary<string, string> _nicknameSnapshot =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>本表的昵称快照（设备号 → 昵称），供另一张表查重名，读取不取锁。</summary>
    internal IReadOnlyDictionary<string, string> NicknameSnapshot =>
        Volatile.Read(ref _nicknameSnapshot);

    internal RecordingComputerNicknameRegistry(
        string path,
        Func<IReadOnlyDictionary<string, string>>? externalNicknames = null)
    {
        _path = path;
        _externalNicknames = externalNicknames
            ?? (() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        _entries = Load(path);
        RefreshNicknameSnapshot();
    }

    internal string Assign(string? nodeId, string? requestedName, bool customized)
    {
        string normalizedNodeId = nodeId?.Trim() ?? "";
        if (normalizedNodeId.Length == 0)
            return NormalizeName(requestedName) ?? "电脑1";

        lock (_sync)
        {
            DateTime cutoff = DateTime.UtcNow - Retention;
            _entries.RemoveAll(item => item.UpdatedAtUtc < cutoff);
            Entry? existing = _entries.FirstOrDefault(item =>
                string.Equals(item.NodeId, normalizedNodeId, StringComparison.OrdinalIgnoreCase));
            string assignedName;
            if (customized && NormalizeName(requestedName) is string customName)
            {
                assignedName = MakeUnique(customName, normalizedNodeId);
            }
            else if (existing != null)
            {
                assignedName = existing.DisplayName;
                customized = existing.Customized;
            }
            else
            {
                assignedName = CreateNextAutomaticName();
                customized = false;
            }

            _entries.RemoveAll(item =>
                string.Equals(item.NodeId, normalizedNodeId, StringComparison.OrdinalIgnoreCase));
            _entries.Add(new Entry
            {
                NodeId = normalizedNodeId,
                DisplayName = assignedName,
                Customized = customized,
                UpdatedAtUtc = DateTime.UtcNow
            });
            _entries = _entries
                .OrderByDescending(item => item.UpdatedAtUtc)
                .Take(MaxKnownComputers)
                .ToList();
            RefreshNicknameSnapshot();
            try { Save(); } catch { }
            return assignedName;
        }
    }

    internal void RegisterHost(string? nodeId, string? displayName, bool customized)
    {
        Assign(nodeId, displayName, customized);
    }

    internal IReadOnlyList<RecordingComputerNicknameInfo> GetKnown()
    {
        lock (_sync)
        {
            DateTime cutoff = DateTime.UtcNow - Retention;
            if (_entries.RemoveAll(item => item.UpdatedAtUtc < cutoff) > 0)
            {
                try { Save(); } catch { }
            }
            return _entries
                .OrderByDescending(item => item.UpdatedAtUtc)
                .Select(item => new RecordingComputerNicknameInfo(
                    item.NodeId,
                    item.DisplayName,
                    item.UpdatedAtUtc))
                .ToArray();
        }
    }

    private string CreateNextAutomaticName()
    {
        // 自动编号也要避开手机登记表里的名字：用户可能把一台手机手工命名成"电脑3"，
        // 那么下一台工位就不能再拿到"电脑3"。
        var usedNumbers = _entries
            .Select(item => item.DisplayName?.Trim() ?? "")
            .Concat(ReadExternalNicknames().Values.Select(name => name?.Trim() ?? ""))
            .Where(Config.AppConfig.IsAutomaticComputerName)
            .Select(name => int.TryParse(name["电脑".Length..], out int number) ? number : 0)
            .Where(number => number > 0)
            .ToHashSet();
        int number = 1;
        while (usedNumbers.Contains(number)) number++;
        return $"电脑{number}";
    }

    private bool IsNameTakenInExternalTable(string name, string nodeId)
    {
        string value = name?.Trim() ?? "";
        if (value.Length == 0)
            return false;

        foreach ((string externalNodeId, string externalName) in ReadExternalNicknames())
        {
            if (string.Equals(externalNodeId, nodeId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(externalName?.Trim(), value, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private IReadOnlyDictionary<string, string> ReadExternalNicknames()
    {
        try
        {
            return _externalNicknames();
        }
        catch
        {
            // 另一张表暂时读不到时不阻塞命名：本表内的唯一性仍然保证。
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 刷新对外的昵称快照。每次改动 <see cref="_entries"/> 之后都要调用，
    /// 否则另一张表查到的是旧名字。必须在持有 <see cref="_sync"/> 时调用。
    /// </summary>
    private void RefreshNicknameSnapshot()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Entry entry in _entries)
        {
            string entryNodeId = entry.NodeId?.Trim() ?? "";
            if (entryNodeId.Length > 0)
                snapshot[entryNodeId] = entry.DisplayName?.Trim() ?? "";
        }

        Volatile.Write(ref _nicknameSnapshot, snapshot);
    }

    private string MakeUnique(string requestedName, string nodeId)
    {
        // 本表 + 手机登记表一起查：手机和电脑不能同名，而只发心跳的工位不在手机登记表里，
        // 反过来没上传过的手机也可能不在本表里，两边都查才是全局唯一。
        bool IsUsed(string candidate) => _entries.Any(item =>
            !string.Equals(item.NodeId, nodeId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.DisplayName, candidate, StringComparison.OrdinalIgnoreCase))
            || IsNameTakenInExternalTable(candidate, nodeId);
        if (!IsUsed(requestedName)) return requestedName;

        for (int number = 2; ; number++)
        {
            string suffix = $" {number}";
            int prefixLength = Math.Max(1, MaxNameLength - suffix.Length);
            string candidate = requestedName[..Math.Min(requestedName.Length, prefixLength)] + suffix;
            if (!IsUsed(candidate)) return candidate;
        }
    }

    private static string? NormalizeName(string? value)
    {
        string name = value?.Trim() ?? "";
        return name.Length is > 0 and <= MaxNameLength && !name.Any(char.IsControl)
            ? name
            : null;
    }

    private static List<Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [];
        }
        catch
        {
            return [];
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
        public string NodeId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool Customized { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}

internal sealed record RecordingComputerNicknameInfo(
    string NodeId,
    string DisplayName,
    DateTime UpdatedAtUtc);
