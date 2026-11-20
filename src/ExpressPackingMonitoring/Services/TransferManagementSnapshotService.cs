using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Localization;

namespace ExpressPackingMonitoring.Services;

internal enum TransferManagementMode
{
    HostDevices,
    RecordingTransfer
}

internal enum HostConnectionState
{
    Unknown,
    Online,
    Offline,
    AwaitingApproval,
    PausedByUser,
    StorageBlocked,
    WriteBusy
}

internal enum DeviceAttentionKind
{
    None,
    Offline
}

internal sealed record TransferTaskRow(
    long TaskId,
    string Identifier,
    long FileSizeBytes,
    long SentBytes,
    string Reason);

internal sealed record HostDeviceRow(
    string DeviceId,
    string DisplayName,
    bool IsOnline,
    int TodayReceivedCount,
    DateTimeOffset? LastActivityAt,
    DeviceAttentionKind Attention);

internal sealed record MobileBackupDeviceDirectoryEntry(
    string DeviceId,
    string DisplayName,
    bool IsOnline,
    DateTimeOffset? LastSeenAt);

internal sealed record TransferManagementSnapshot(
    HostConnectionState HostState,
    int TodayReceivedCount,
    int PendingCount,
    int NeedsActionCount,
    TransferTaskRow? CurrentUpload,
    TransferTaskRow? PausedUpload,
    TransferTaskRow? UpcomingUpload,
    DateTimeOffset? NextUploadAt,
    IReadOnlyList<TransferTaskRow> FailurePreview,
    int FailureOverflowCount,
    DateTimeOffset? NextRetryAt,
    IReadOnlyList<HostDeviceRow> HostDevices)
{
    public static TransferManagementSnapshot Empty() =>
        new(
            HostConnectionState.Unknown,
            0,
            0,
            0,
            null,
            null,
            null,
            null,
            [],
            0,
            null,
            []);
}

/// <summary>
/// UI 只读快照适配器。所有数据库、设备登记表和存储探测都在后台线程完成；
/// 窗口不直接持有 SQLite 连接，也不根据错误文本猜测上传状态。
/// </summary>
internal sealed class TransferManagementSnapshotService
{
    private readonly AppConfig _config;
    private readonly VideoDatabase _database;
    private readonly WebServer? _webServer;
    private readonly RecordingTransferQueueStore? _queueStore;
    private readonly RecordingTransferService? _transferService;
    private readonly Func<RecordingTransferHostHealth?> _hostHealth;

    public TransferManagementSnapshotService(
        TransferManagementMode mode,
        AppConfig config,
        VideoDatabase database,
        WebServer? webServer,
        RecordingTransferQueueStore? queueStore,
        RecordingTransferService? transferService,
        Func<RecordingTransferHostHealth?>? hostHealth = null)
    {
        Mode = mode;
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _webServer = webServer;
        _queueStore = queueStore;
        _transferService = transferService;
        _hostHealth = hostHealth ?? (() => null);
    }

    public TransferManagementMode Mode { get; }

    public Task<TransferManagementSnapshot> LoadAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Mode == TransferManagementMode.HostDevices
                ? LoadHostSnapshot()
                : LoadTransferSnapshot();
        }, cancellationToken);

    public Task RetryTaskAsync(long taskId, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_queueStore == null)
                return;
            _queueStore.RetryTaskNow(taskId, DateTime.UtcNow);
            _transferService?.Wake();
        }, cancellationToken);

    public Task RetryAllAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_queueStore == null)
                return;
            _queueStore.RetryFailedNow(DateTime.UtcNow);
            _transferService?.EnqueueCompletedRecordings();
            _transferService?.RetryNow();
        }, cancellationToken);

    public Task<BackupDeviceRemovalResult> ForgetHostDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Mode != TransferManagementMode.HostDevices || _webServer == null)
            {
                return new BackupDeviceRemovalResult(
                    BackupDeviceRemovalStatus.NotFound);
            }
            return _webServer.TryForgetKnownBackupDevice(deviceId);
        }, cancellationToken);

    private TransferManagementSnapshot LoadTransferSnapshot()
    {
        RecordingTransferQueueStore queueStore = _queueStore
            ?? throw new InvalidOperationException("上传队列尚未初始化");
        DateTime localStart = DateTime.Today;
        DateTime localEnd = localStart.AddDays(1);
        TimeZoneInfo zone = TimeZoneInfo.Local;
        RecordingTransferManagementData data = queueStore.GetManagementData(
            new DateTimeOffset(localStart, zone.GetUtcOffset(localStart)),
            new DateTimeOffset(localEnd, zone.GetUtcOffset(localEnd)));

        RecordingTransferHostHealth? health = _hostHealth();
        HostConnectionState hostState = health?.State
            ?? (data.DominantFailureKind switch
            {
                RecordingTransferFailureKinds.HostStorageUnavailable =>
                    HostConnectionState.StorageBlocked,
                RecordingTransferFailureKinds.HostWriteBusy =>
                    HostConnectionState.WriteBusy,
                RecordingTransferFailureKinds.HostApprovalRequired =>
                    HostConnectionState.AwaitingApproval,
                RecordingTransferFailureKinds.PausedByUser =>
                    HostConnectionState.PausedByUser,
                _ => HostConnectionState.Unknown
            });

        return new TransferManagementSnapshot(
            hostState,
            data.TodayUploadedCount,
            data.PendingCount + data.RetryWaitCount,
            data.NeedsActionCount,
            MapTask(data.CurrentUpload),
            MapTask(data.PausedUpload),
            MapTask(data.NextPendingUpload),
            _transferService?.GetNextProcessingAt(),
            data.FailurePreview.Select(MapTask).Where(row => row != null).Cast<TransferTaskRow>().ToArray(),
            data.FailureOverflowCount,
            data.NextRetryAt,
            []);
    }

    private TransferManagementSnapshot LoadHostSnapshot()
    {
        WebServer webServer = _webServer
            ?? throw new InvalidOperationException("主机服务尚未初始化");
        DateTime today = DateTime.Today;
        IReadOnlyList<MobileBackupDeviceSummary> summaries =
            _database.GetMobileBackupDeviceSummaries(today);
        IReadOnlySet<string> removedDeviceIds =
            webServer.GetRemovedSourceDeviceIds();
        IReadOnlyDictionary<string, string> currentNames =
            webServer.GetCurrentSourceDeviceNames();
        IReadOnlyList<MobileBackupDeviceDirectoryEntry> directory =
            webServer.GetMobileBackupDeviceDirectory();
        HostUploadStorageAvailability storageState =
            webServer.GetMobileBackupStorageAvailability();

        var devices = new Dictionary<string, MutableHostDevice>(
            StringComparer.OrdinalIgnoreCase);
        foreach (MobileBackupDeviceSummary summary in summaries)
        {
            if (!BackupDeviceIdentity.IsRemote(summary.DeviceId, _config.NodeId)
                || removedDeviceIds.Contains(summary.DeviceId))
                continue;
            devices[summary.DeviceId] = new MutableHostDevice
            {
                DeviceId = summary.DeviceId,
                DisplayName = ResolveName(summary.DeviceId, summary.DeviceName, currentNames),
                TodayReceivedCount = summary.TodayCount,
                LastActivityAt = ToOffset(summary.LastActivityAt)
            };
        }

        foreach (MobileBackupDeviceDirectoryEntry device in directory)
        {
            if (!BackupDeviceIdentity.IsRemote(device.DeviceId, _config.NodeId))
                continue;
            devices.TryGetValue(device.DeviceId, out MutableHostDevice? item);
            item ??= new MutableHostDevice
            {
                DeviceId = device.DeviceId
            };
            item.DisplayName = ResolveName(
                device.DeviceId,
                item.DisplayName.Length > 0 ? item.DisplayName : device.DisplayName,
                currentNames);
            item.IsOnline = device.IsOnline;
            if (device.LastSeenAt is DateTimeOffset seen
                && (item.LastActivityAt == null || seen > item.LastActivityAt))
            {
                item.LastActivityAt = seen;
            }
            devices[device.DeviceId] = item;
        }

        HostDeviceRow[] rows = devices.Values
            .Select(item =>
            {
                DeviceAttentionKind attention = !item.IsOnline
                    ? DeviceAttentionKind.Offline
                    : DeviceAttentionKind.None;
                return new HostDeviceRow(
                    item.DeviceId,
                    string.IsNullOrWhiteSpace(item.DisplayName)
                        ? AppLanguage.Get("设备")
                        : item.DisplayName,
                    item.IsOnline,
                    item.TodayReceivedCount,
                    item.LastActivityAt,
                    attention);
            })
            .OrderBy(row => row.Attention == DeviceAttentionKind.None ? 1 : 0)
            .ThenByDescending(row => row.LastActivityAt)
            .ThenBy(row => row.DisplayName, StringComparer.CurrentCulture)
            .ToArray();

        HostConnectionState hostState = storageState.IsAvailable
            ? HostConnectionState.Online
            : HostConnectionState.StorageBlocked;
        return new TransferManagementSnapshot(
            hostState,
            rows.Sum(row => row.TodayReceivedCount),
            0,
            rows.Count(row => row.Attention != DeviceAttentionKind.None),
            null,
            null,
            null,
            null,
            [],
            0,
            null,
            rows);
    }

    private static TransferTaskRow? MapTask(RecordingTransferManagementTaskRow? task) =>
        task == null
            ? null
            : new TransferTaskRow(
                task.TaskId,
                task.Identifier,
                task.FileSizeBytes,
                task.SentBytes,
                task.Reason);

    private static string ResolveName(
        string deviceId,
        string fallback,
        IReadOnlyDictionary<string, string> names)
    {
        if (names.TryGetValue(deviceId, out string? current)
            && !string.IsNullOrWhiteSpace(current))
        {
            return current.Trim();
        }
        return fallback?.Trim() ?? "";
    }

    private static DateTimeOffset? ToOffset(DateTime? value)
    {
        if (value is not DateTime dateTime)
            return null;
        return dateTime.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(dateTime),
            DateTimeKind.Local => new DateTimeOffset(dateTime),
            _ => new DateTimeOffset(
                DateTime.SpecifyKind(dateTime, DateTimeKind.Local))
        };
    }

    private sealed class MutableHostDevice
    {
        public string DeviceId { get; init; } = "";
        public string DisplayName { get; set; } = "";
        public bool IsOnline { get; set; }
        public int TodayReceivedCount { get; set; }
        public DateTimeOffset? LastActivityAt { get; set; }
    }
}
