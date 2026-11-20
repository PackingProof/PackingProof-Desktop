using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

internal enum BackupDeviceRemovalStatus
{
    Removed,
    Failed,
    NotFound
}

internal sealed record BackupDeviceRemovalResult(
    BackupDeviceRemovalStatus Status,
    int VideoCount = 0);

public sealed partial class WebServer
{
    private static readonly TimeSpan BackupDeviceMaintenanceInterval =
        TimeSpan.FromHours(12);
    private static readonly TimeSpan BackupDeviceMaintenanceRetryInterval =
        TimeSpan.FromMinutes(30);

    private Task? _backupDeviceMaintenanceTask;
    private BackupDeviceMaintenanceStateStore? _backupDeviceMaintenanceState;

    private void StartBackupDeviceMaintenance()
    {
        _backupDeviceMaintenanceState = new BackupDeviceMaintenanceStateStore(
            Path.Combine(
                _extensionStateDirectory,
                "device-maintenance",
                "state.json"));
        _backupDeviceMaintenanceTask = Task.Run(
            () => RunBackupDeviceMaintenanceAsync(_cts.Token),
            _cts.Token);
    }

    private async Task RunBackupDeviceMaintenanceAsync(
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BackupDeviceMaintenanceCheckpoint? checkpoint =
            _backupDeviceMaintenanceState?.LoadCheckpoint();
        int nameSeedVersion = checkpoint?.NameSeedVersion ?? 0;
        int legacyRemovalImportVersion =
            checkpoint?.LegacyRemovalImportVersion ?? 0;
        bool migrationPending =
            nameSeedVersion < 1 || legacyRemovalImportVersion < 1;
        DateTimeOffset nextCheckAt = !migrationPending
            && checkpoint is { } loaded
            && loaded.LastSuccessfulCheckUtc.Add(
                BackupDeviceMaintenanceInterval) > now
                ? loaded.LastSuccessfulCheckUtc.Add(
                    BackupDeviceMaintenanceInterval)
                : now.AddSeconds(3);

        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan delay = nextCheckAt - DateTimeOffset.UtcNow;
            if (delay < TimeSpan.FromSeconds(3))
                delay = TimeSpan.FromSeconds(3);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            try
            {
                IReadOnlyList<VideoSourceInfo> sources =
                    migrationPending
                        ? _db.GetVideoSources()
                        : [];
                cancellationToken.ThrowIfCancellationRequested();
                if (nameSeedVersion < 1)
                {
                    _mobileOrderReceivers.SeedRecordedDevices(sources);
                    nameSeedVersion = 1;
                }
                int importedRemovals = 0;
                if (legacyRemovalImportVersion < 1)
                {
                    importedRemovals = ImportLegacyDeviceRemovals(sources);
                    legacyRemovalImportVersion = 1;
                }

                IReadOnlyList<string> knownDeviceIds =
                    _mobileOrderReceivers
                        .GetKnownRecordingDevices()
                        .Select(device => device.NodeId)
                        .ToArray();
                IReadOnlyList<string> activeDeviceIds =
                    _db.GetActiveExternalDeviceIds(knownDeviceIds);
                cancellationToken.ThrowIfCancellationRequested();
                int removed =
                    _mobileOrderReceivers.ReconcileActiveDevices(activeDeviceIds);
                cancellationToken.ThrowIfCancellationRequested();
                RefreshRecordingDeviceIndex();

                DateTimeOffset completedAt = DateTimeOffset.UtcNow;
                _backupDeviceMaintenanceState?.SaveCheckpoint(
                    new BackupDeviceMaintenanceCheckpoint(
                        completedAt,
                        nameSeedVersion,
                        legacyRemovalImportVersion));
                nextCheckAt = completedAt.Add(BackupDeviceMaintenanceInterval);
                if (removed > 0)
                {
                    RuntimeLog.Info(
                        "MobileBackup",
                        $"后台整理历史设备完成，清理 {removed} 台无录像设备");
                }
                if (importedRemovals > 0)
                {
                    RuntimeLog.Info(
                        "MobileBackup",
                        $"旧版解绑状态迁移完成，隐藏 {importedRemovals} 个历史设备号");
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                nextCheckAt = DateTimeOffset.UtcNow
                    .Add(BackupDeviceMaintenanceRetryInterval);
                RuntimeLog.Warn(
                    "MobileBackup",
                    $"后台整理历史设备失败，稍后重试：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 旧版本的解绑先删了登记条目，没有留下可查询的墓碑。升级后把“仍有有效录像、
    /// 但已不在当前设备登记中”的历史设备号补记为隐藏：当前列表和筛选恢复干净，
    /// 录像、标签与按单号搜索保持不变。
    /// </summary>
    internal int ImportLegacyDeviceRemovals(
        IReadOnlyList<VideoSourceInfo>? sources)
    {
        IReadOnlySet<string> currentDeviceIds = _mobileOrderReceivers
            .GetKnownRecordingDevices()
            .Select(device => device.NodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (currentDeviceIds.Count == 0)
            return 0;

        IReadOnlySet<string> knownDeviceIds = _mobileOrderReceivers
            .GetKnownRecordingDevices(includeRemoved: true)
            .Select(device => device.NodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> alreadyRemoved =
            _backupDeviceRemovals.GetRemovedDeviceIds();
        string[] legacyDeviceIds = (sources ?? [])
            .Where(source => string.Equals(
                source.SourceType,
                "external",
                StringComparison.OrdinalIgnoreCase))
            .Select(source => source.DeviceId?.Trim() ?? "")
            .Where(deviceId => deviceId.Length > 0)
            .Where(deviceId => BackupDeviceIdentity.IsRemote(deviceId, _nodeId))
            .Where(deviceId => !knownDeviceIds.Contains(deviceId))
            .Where(deviceId => !alreadyRemoved.Contains(deviceId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!_backupDeviceRemovals.MarkRemovedRange(legacyDeviceIds))
        {
            throw new IOException("旧版设备解绑状态写入失败");
        }
        return legacyDeviceIds.Length;
    }

    /// <summary>
    /// 解除一台远程从机的配对和上传权限。设备号与昵称作为历史墓碑保留，
    /// 因此旧录像仍能正常显示和搜索；当前设备列表、筛选和上传目标会立即排除它。
    /// 普通心跳不会恢复，只有重新扫码配对才会清除墓碑状态。
    /// </summary>
    internal BackupDeviceRemovalResult TryForgetKnownBackupDevice(string? deviceId)
    {
        string normalizedDeviceId = deviceId?.Trim() ?? "";
        if (normalizedDeviceId.Length == 0
            || !BackupDeviceIdentity.IsRemote(normalizedDeviceId, _nodeId))
        {
            return new BackupDeviceRemovalResult(BackupDeviceRemovalStatus.NotFound);
        }

        bool removedFromMobileRegistry =
            _mobileOrderReceivers.TryForgetDevice(normalizedDeviceId);
        bool removedFromComputerRegistry =
            _recordingComputerNicknames.TryForgetDevice(normalizedDeviceId);
        bool revokedCredential =
            _backupPairingTokens.ForgetDevice(normalizedDeviceId);
        bool alreadyRemoved =
            _backupDeviceRemovals.IsRemoved(normalizedDeviceId);
        bool hasHistoricalVideos =
            _db.CountActiveExternalVideosByDevice(normalizedDeviceId) > 0;
        if (!removedFromMobileRegistry
            && !removedFromComputerRegistry
            && !revokedCredential
            && !alreadyRemoved
            && !hasHistoricalVideos)
            return new BackupDeviceRemovalResult(BackupDeviceRemovalStatus.NotFound);

        // 旧版本的解绑可能已经从登记表里删掉了条目，只剩录像里的设备号。
        // 独立状态必须覆盖这种情况，否则列表从历史录像补齐设备时又会把它带回来。
        if (!_backupDeviceRemovals.MarkRemoved(normalizedDeviceId))
            return new BackupDeviceRemovalResult(BackupDeviceRemovalStatus.Failed);
        RefreshRecordingDeviceIndex();
        return new BackupDeviceRemovalResult(BackupDeviceRemovalStatus.Removed);
    }
}
