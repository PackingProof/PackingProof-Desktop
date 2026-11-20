using ExpressPackingMonitoring.Logging;
using System.IO;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

internal sealed record BackupDeviceMaintenanceCheckpoint(
    DateTimeOffset LastSuccessfulCheckUtc,
    int NameSeedVersion,
    int LegacyRemovalImportVersion = 0);

/// <summary>
/// 历史设备后台对账的持久化检查时间。只记录一次全量对账成功时间，
/// 不为每台设备另建状态表：设备是否仍需要保留由录像库和设备登记表实时推导。
/// </summary>
internal sealed class BackupDeviceMaintenanceStateStore
{
    private const int CurrentVersion = 3;
    private readonly string _path;
    private readonly object _sync = new();

    internal BackupDeviceMaintenanceStateStore(string path)
    {
        _path = path;
    }

    internal BackupDeviceMaintenanceCheckpoint? LoadCheckpoint()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_path))
                    return null;
                State? state = JsonSerializer.Deserialize<State>(
                    File.ReadAllText(_path));
                return state is { Version: >= 2 and <= CurrentVersion }
                    && state.LastSuccessfulCheckUtc != default
                    ? new BackupDeviceMaintenanceCheckpoint(
                        state.LastSuccessfulCheckUtc,
                        Math.Max(0, state.NameSeedVersion),
                        state.Version >= 3
                            ? Math.Max(0, state.LegacyRemovalImportVersion)
                            : 0)
                    : null;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn(
                    "MobileBackup",
                    $"历史设备检查时间读取失败，将按已到期处理：{ex.Message}");
                return null;
            }
        }
    }

    internal void SaveCheckpoint(BackupDeviceMaintenanceCheckpoint checkpoint)
    {
        lock (_sync)
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
                        LastSuccessfulCheckUtc =
                            checkpoint.LastSuccessfulCheckUtc,
                        NameSeedVersion = checkpoint.NameSeedVersion,
                        LegacyRemovalImportVersion =
                            checkpoint.LegacyRemovalImportVersion
                    }));
                File.Move(temporaryPath, _path, true);
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
                    $"历史设备检查时间写入失败：{ex.Message}");
            }
        }
    }

    private sealed class State
    {
        public int Version { get; set; }
        public DateTimeOffset LastSuccessfulCheckUtc { get; set; }
        public int NameSeedVersion { get; set; }
        public int LegacyRemovalImportVersion { get; set; }
    }
}
