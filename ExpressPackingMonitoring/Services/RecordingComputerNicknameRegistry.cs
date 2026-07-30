using System.IO;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

internal sealed class RecordingComputerNicknameRegistry
{
    private const int MaxNameLength = 20;
    private readonly string _path;
    private readonly object _sync = new();
    private List<Entry> _entries;

    internal RecordingComputerNicknameRegistry(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    internal string Assign(string? nodeId, string? requestedName, bool customized)
    {
        string normalizedNodeId = nodeId?.Trim() ?? "";
        if (normalizedNodeId.Length == 0)
            return NormalizeName(requestedName) ?? "电脑1";

        lock (_sync)
        {
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
            try { Save(); } catch { }
            return assignedName;
        }
    }

    internal void RegisterHost(string? nodeId, string? displayName, bool customized)
    {
        Assign(nodeId, displayName, customized);
    }

    private string CreateNextAutomaticName()
    {
        var usedNumbers = _entries
            .Select(item => item.DisplayName?.Trim() ?? "")
            .Where(Config.AppConfig.IsAutomaticComputerName)
            .Select(name => int.TryParse(name["电脑".Length..], out int number) ? number : 0)
            .Where(number => number > 0)
            .ToHashSet();
        int number = 1;
        while (usedNumbers.Contains(number)) number++;
        return $"电脑{number}";
    }

    private string MakeUnique(string requestedName, string nodeId)
    {
        bool IsUsed(string candidate) => _entries.Any(item =>
            !string.Equals(item.NodeId, nodeId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.DisplayName, candidate, StringComparison.OrdinalIgnoreCase));
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
