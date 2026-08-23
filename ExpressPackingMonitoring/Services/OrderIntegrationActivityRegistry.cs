using System.IO;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

internal sealed record OrderIntegrationActivity(string NodeId, DateTimeOffset LastActivityUtc, int ReceivedCount);

internal sealed class OrderIntegrationActivityRegistry
{
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private List<Entry> _entries;

    internal OrderIntegrationActivityRegistry(string path, TimeProvider? timeProvider = null)
    {
        _path = path;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _entries = Load(path);
        lock (_sync) PruneExpiredCore();
    }

    internal void RecordReceived(string? nodeId, int receivedCount)
    {
        string normalizedNodeId = nodeId?.Trim() ?? "";
        if (normalizedNodeId.Length == 0 || receivedCount <= 0) return;

        lock (_sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            _entries.RemoveAll(entry =>
                string.Equals(entry.NodeId, normalizedNodeId, StringComparison.OrdinalIgnoreCase)
                || now - entry.LastActivityUtc > Retention);
            _entries.Add(new Entry
            {
                NodeId = normalizedNodeId,
                LastActivityUtc = now,
                ReceivedCount = receivedCount
            });
            Save();
        }
    }

    internal IReadOnlyDictionary<string, OrderIntegrationActivity> GetSnapshot()
    {
        lock (_sync)
        {
            PruneExpiredCore();
            return _entries.ToDictionary(
                entry => entry.NodeId,
                entry => new OrderIntegrationActivity(entry.NodeId, entry.LastActivityUtc, entry.ReceivedCount),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private void PruneExpiredCore()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (_entries.RemoveAll(entry => now - entry.LastActivityUtc > Retention) > 0)
            Save();
    }

    private static List<Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [];
        }
        catch { return []; }
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
        public DateTimeOffset LastActivityUtc { get; set; }
        public int ReceivedCount { get; set; }
    }
}
