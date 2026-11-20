using ExpressPackingMonitoring.Logging;
using System.IO;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 用户主动解绑的设备号。该状态独立于设备登记表保存，避免登记被历史整理或旧版本清理后，
/// 同一个设备号又从历史录像来源中重新出现在设备列表和筛选里。
/// </summary>
internal sealed class BackupDeviceRemovalStore
{
    private const int CurrentVersion = 1;

    private readonly string _path;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _devices;

    internal BackupDeviceRemovalStore(
        string path,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _devices = Load(_path);
        MobileOrderReceiverRegistry.CleanupAbandonedTemporaryFiles(_path);
    }

    internal IReadOnlySet<string> GetRemovedDeviceIds()
    {
        lock (_sync)
        {
            return _devices.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal bool IsRemoved(string? deviceId)
    {
        string normalizedDeviceId = NormalizeDeviceId(deviceId);
        if (normalizedDeviceId.Length == 0)
            return false;

        lock (_sync)
        {
            return _devices.ContainsKey(normalizedDeviceId);
        }
    }

    internal bool MarkRemoved(string? deviceId)
    {
        string normalizedDeviceId = NormalizeDeviceId(deviceId);
        if (normalizedDeviceId.Length == 0)
            return false;

        lock (_sync)
        {
            if (_devices.ContainsKey(normalizedDeviceId))
                return true;

            _devices[normalizedDeviceId] = _utcNow();
            if (Save())
                return true;

            _devices.Remove(normalizedDeviceId);
            return false;
        }
    }

    internal bool MarkRemovedRange(IEnumerable<string?>? deviceIds)
    {
        string[] normalizedDeviceIds = (deviceIds ?? [])
            .Select(NormalizeDeviceId)
            .Where(deviceId => deviceId.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedDeviceIds.Length == 0)
            return true;

        lock (_sync)
        {
            string[] addedDeviceIds = normalizedDeviceIds
                .Where(deviceId => !_devices.ContainsKey(deviceId))
                .ToArray();
            if (addedDeviceIds.Length == 0)
                return true;

            DateTimeOffset removedAt = _utcNow();
            foreach (string deviceId in addedDeviceIds)
                _devices[deviceId] = removedAt;
            if (Save())
                return true;

            foreach (string deviceId in addedDeviceIds)
                _devices.Remove(deviceId);
            return false;
        }
    }

    internal bool Restore(string? deviceId)
    {
        string normalizedDeviceId = NormalizeDeviceId(deviceId);
        if (normalizedDeviceId.Length == 0)
            return false;

        lock (_sync)
        {
            if (!_devices.Remove(normalizedDeviceId, out DateTimeOffset removedAt))
                return false;

            if (Save())
                return true;

            _devices[normalizedDeviceId] = removedAt;
            return false;
        }
    }

    private bool Save()
    {
        string? directory = Path.GetDirectoryName(_path);
        string temporaryPath =
            $"{_path}.{Environment.ProcessId:X}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(new State
                {
                    Version = CurrentVersion,
                    Devices = _devices
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            StringComparer.Ordinal)
                }));
            File.Move(temporaryPath, _path, true);
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
            RuntimeLog.Warn(
                "MobileBackup",
                $"设备解绑状态写入失败：{ex.Message}");
            return false;
        }
    }

    private static Dictionary<string, DateTimeOffset> Load(string path)
    {
        var devices = new Dictionary<string, DateTimeOffset>(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path))
                return devices;
            State? state = JsonSerializer.Deserialize<State>(
                File.ReadAllText(path));
            if (state is not { Version: CurrentVersion, Devices: not null })
                return devices;

            foreach ((string deviceId, DateTimeOffset removedAt) in state.Devices)
            {
                string normalizedDeviceId = NormalizeDeviceId(deviceId);
                if (normalizedDeviceId.Length > 0)
                    devices[normalizedDeviceId] = removedAt;
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "MobileBackup",
                $"设备解绑状态读取失败，将按空表继续：{ex.Message}");
        }
        return devices;
    }

    private static string NormalizeDeviceId(string? deviceId) =>
        deviceId?.Trim() ?? "";

    private sealed class State
    {
        public int Version { get; set; }
        public Dictionary<string, DateTimeOffset> Devices { get; set; } = [];
    }
}
