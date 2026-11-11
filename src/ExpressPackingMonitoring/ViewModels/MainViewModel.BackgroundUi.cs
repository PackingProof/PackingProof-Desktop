#nullable disable
#nullable disable
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace ExpressPackingMonitoring.ViewModels;

public partial class MainViewModel
{
    private readonly CoalescingBackgroundRefresh _todayStatsRefresh = new();
    private readonly CoalescingBackgroundRefresh _archiveBackupSummaryRefresh = new();
    private readonly CoalescingBackgroundRefresh _mobileBackupStatusRefresh = new();
    private readonly CoalescingBackgroundRefresh _userscriptStatusRefresh = new();
    private readonly CoalescingBackgroundRefresh _recentScanRecordsRefresh = new();
    private static readonly TimeSpan UiHeartbeatStallThreshold = TimeSpan.FromSeconds(2);
    private DateTime _lastUiHeartbeatStallLogAt = DateTime.MinValue;
    private int _recentScanRecordMutationVersion;

    private void RefreshTodayStats()
    {
        if (_isDisposed || _db == null)
            return;
        _todayStatsRefresh.Request("今日统计", RefreshTodayStatsAsync);
    }

    private async Task RefreshTodayStatsAsync()
    {
        VideoDatabase database = _db;
        if (database == null || _isDisposed)
            return;

        int pieces;
        TimeSpan duration;
        try
        {
            var todayList = database.GetAggregatedStats(
                DateTime.Today,
                DateTime.Today,
                "day",
                "pc");
            pieces = todayList is { Count: > 0 } ? todayList[0].TotalPieces : 0;
            duration = todayList is { Count: > 0 }
                ? TimeSpan.FromSeconds(todayList[0].TotalDurationSec)
                : TimeSpan.Zero;
        }
        catch
        {
            return;
        }

        await ApplyOnUiThreadAsync(() =>
        {
            if (_isDisposed || !ReferenceEquals(_db, database))
                return;
            TotalPieces = pieces;
            _totalPackTime = duration;
            OnPropertyChanged(nameof(TotalPackTimeDisplay));
            OnPropertyChanged(nameof(AveragePackTimeDisplay));
        });
    }

    private void RefreshArchiveBackupSummary()
    {
        if (_isDisposed)
            return;
        _archiveBackupSummaryRefresh.Request("网络备份状态", RefreshArchiveBackupSummaryAsync);
    }

    private async Task RefreshArchiveBackupSummaryAsync()
    {
        AppConfig config = Config;
        VideoDatabase database = _db;
        ArchiveService archiveService = _archiveService;
        if (_isDisposed || config == null)
            return;

        bool visible = ArchiveBackupCardModel.ShouldShowArchiveBackupCard(
            config,
            IsRecordingWorkstation);
        ArchiveQueueSummary summary = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
        if (visible && database != null)
        {
            try
            {
                summary = database.GetArchiveQueueSummary();
            }
            catch
            {
                return;
            }
        }

        bool targetUnavailable = _archiveTargetUnavailable;
        string unavailableRoot = _archiveUnavailableRoot;
        ArchiveWorkerSnapshot workerSnapshot = default;
        try
        {
            workerSnapshot = archiveService?.CurrentWorkerSnapshot ?? default;
        }
        catch
        {
        }

        ArchiveBackupCardState state = visible
            ? ArchiveBackupCardModel.BuildArchiveBackupCardState(
                summary,
                ArchiveBackupCardModel.ResolveCurrentArchiveTarget(config),
                targetUnavailable,
                unavailableRoot,
                workerSnapshot)
            : default;

        await ApplyOnUiThreadAsync(() =>
        {
            if (_isDisposed || !ReferenceEquals(Config, config))
                return;
            IsArchiveBackupCardVisible = visible;
            if (!visible)
                return;
            RemainingArchiveBackupCount = summary.RemainingCount;
            ArchiveBackupShortStatusText = state.ShortStatusText;
            ArchiveBackupStatusText = state.DetailText;
        });
    }

    private void RefreshMobileBackupStatuses()
    {
        if (_isDisposed)
            return;
        _mobileBackupStatusRefresh.Request("设备备份状态", RefreshMobileBackupStatusesAsync);
    }

    private async Task RefreshMobileBackupStatusesAsync()
    {
        AppConfig config = Config;
        VideoDatabase database = _db;
        WebServer webServer = _webServer;
        IReadOnlyList<ConnectedClientInfo> connectedClients = _connectedClientSnapshot;
        if (_isDisposed || config == null)
            return;

        DateTime statusDate = DateTime.Today;
        IReadOnlyList<MobileBackupDailyCount> counts = [];
        try
        {
            counts = database?.GetMobileBackupDailyCounts(statusDate) ?? [];
        }
        catch
        {
            return;
        }

        IReadOnlyDictionary<string, string> currentSourceNames =
            webServer?.GetCurrentSourceDeviceNames()
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var statusByDevice = new Dictionary<string, MobileBackupStatusRow>(
            StringComparer.OrdinalIgnoreCase);
        foreach (MobileBackupDailyCount item in counts
                     .Where(item => BackupDeviceIdentity.IsRemote(item.DeviceId, config.NodeId)))
        {
            string currentName = RecordingSourceNameLookup.Resolve(
                currentSourceNames,
                item.DeviceId,
                item.DeviceName);
            statusByDevice[item.DeviceId] = new MobileBackupStatusRow(
                item.DeviceId,
                currentName.Length > 0
                    ? currentName
                    : GetFallbackDeviceName(item.DeviceId, item.DeviceKind),
                item.DeviceKind,
                item.VideoCount,
                false);
        }

        foreach (ConnectedClientInfo client in connectedClients
                     .Where(client => ShouldIncludeBackupDeviceClient(client, config.NodeId)))
        {
            string deviceId = string.IsNullOrWhiteSpace(client.NodeId)
                ? client.ClientId
                : client.NodeId;
            if (!BackupDeviceIdentity.IsRemote(deviceId, config.NodeId))
                continue;

            statusByDevice.TryGetValue(deviceId, out MobileBackupStatusRow existing);
            bool isComputer = string.Equals(
                client.ClientType,
                "recording-workstation",
                StringComparison.OrdinalIgnoreCase);
            statusByDevice[deviceId] = new MobileBackupStatusRow(
                deviceId,
                string.IsNullOrWhiteSpace(client.DisplayName)
                    ? existing.Name ?? (isComputer ? "电脑设备" : "手机设备")
                    : client.DisplayName,
                isComputer ? "pc" : "mobile",
                existing.Count,
                true);
        }

        MobileBackupStatusRow[] ordered = statusByDevice.Values
            .OrderBy(item => item.Name, StringComparer.CurrentCulture)
            .ThenBy(item => item.DeviceId, StringComparer.Ordinal)
            .ToArray();

        await ApplyOnUiThreadAsync(() =>
        {
            if (_isDisposed || !ReferenceEquals(Config, config))
                return;
            _mobileBackupStatusDate = statusDate;
            MobileBackupDeviceStatuses.Clear();
            foreach (MobileBackupStatusRow item in ordered)
            {
                MobileBackupDeviceStatuses.Add(new MobileBackupDeviceStatus
                {
                    DeviceId = item.DeviceId,
                    DisplayText = $"{item.Name} · 今日备份 {item.Count} 个",
                    IsOnline = item.Online
                });
            }
            if (MobileBackupDeviceStatuses.Count == 0)
            {
                MobileBackupDeviceStatuses.Add(new MobileBackupDeviceStatus
                {
                    DisplayText = AppLanguage.Get("暂无手机/电脑设备"),
                    IsOnline = false
                });
            }
            RefreshUserscriptStatus();
        });
    }

    private void RefreshUserscriptStatus()
    {
        if (_isDisposed)
            return;
        _userscriptStatusRefresh.Request("油猴状态", RefreshUserscriptStatusAsync);
    }

    private async Task RefreshUserscriptStatusAsync()
    {
        AppConfig config = Config;
        WebServer webServer = _webServer;
        string monitorAddress = MonitorAccessAddress;
        if (_isDisposed || config == null)
            return;

        UserscriptTargetStatus status;
        try
        {
            IReadOnlyList<RecordingDeviceInfo> devices =
                webServer?.GetRecordingDevices(
                    monitorAddress,
                    includeKnown: true) ?? [];
            status = UserscriptTargetState.GetStatus(config, devices);
        }
        catch
        {
            return;
        }
        (string shortStatus, string detailText) =
            UserscriptStatusCardModel.GetCardTexts(status);

        await ApplyOnUiThreadAsync(() =>
        {
            if (_isDisposed || !ReferenceEquals(Config, config))
                return;
            _lastUserscriptStatusRefreshAt = DateTime.Now;
            UserscriptSetupStatusText = AppLanguage.Get(detailText);
            UserscriptSetupShortStatusText = AppLanguage.Get(shortStatus);
            UserscriptButtonText = AppLanguage.Get(status.ButtonText);
        });
    }

    private async Task RefreshOrderStartContextAsync(
        string orderNumber,
        long recordId,
        string recordingSessionId,
        AppConfig config,
        VideoDatabase database,
        WebServer webServer)
    {
        if (_isDisposed || config == null)
            return;

        bool logOrderInfo = config.EnableOrderInfoLog;
        if (logOrderInfo)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[OrderInfo] 扫码查询: {orderNumber}, "
                + $"EnableAnnounce={config.EnableOrderInfoAnnounce}, "
                + $"WebServer={(webServer != null ? "已启动" : "未启动")}");
        }

        bool isDuplicate;
        OrderInfo orderInfo;
        try
        {
            (isDuplicate, orderInfo) = await Task.Run(() =>
                (
                    database != null
                        && database.OrderIdExistsRecent(
                            orderNumber,
                            excludeRecordId: recordId),
                    webServer?.GetOrderInfo(orderNumber)
                ));
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(
                "Scan",
                $"录像启动后的订单信息查询失败：{ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (logOrderInfo)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[OrderInfo] 查询结果: {(orderInfo != null
                    ? $"命中 买家=[{orderInfo.BuyerMessage}] "
                      + $"卖家=[{orderInfo.SellerMemo}] "
                      + $"商品=[{orderInfo.ProductInfo}]"
                    : "未命中")}");
        }

        await ApplyOnUiThreadAsync(() =>
        {
            if (_isDisposed
                || !IsRecording
                || _currentRecordId != recordId
                || !string.Equals(
                    _recordingOrderId,
                    orderNumber,
                    StringComparison.Ordinal)
                || !string.Equals(
                    _recordingSessionId,
                    recordingSessionId,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (isDuplicate)
            {
                PublishScannerAlert(
                    $"duplicate-order-number:{orderNumber}",
                    "警告：重复单号，请确认",
                    DefaultSpeechCatalog.DuplicateOrderNumber,
                    repeatCount: 2);
            }

            if (orderInfo == null)
                return;
            SetPreviewOrderNotice(orderInfo);
            if (!config.EnableOrderInfoAnnounce)
                return;
            foreach (AlertSpeechFollowup announcement in BuildOrderInfoSpeechFollowups(
                         orderInfo,
                         config.EnableOrderInfoAnnounce,
                         config.AnnounceBuyerMessage,
                         config.AnnounceSellerMemo,
                         config.AnnounceProductInfo,
                         config.AnnounceTotalItemCount))
            {
                PublishVoice(
                    announcement.Text,
                    announcement.VoiceStyle,
                    announcement.Sound,
                    repeatCount: 1,
                    interruptCurrent: false);
            }
        });
    }

    private void RestoreRecentScanRecords()
    {
        if (_isDisposed || _db == null)
            return;
        int version = Volatile.Read(ref _recentScanRecordMutationVersion);
        _recentScanRecordsRefresh.Request(
            "最近扫码记录",
            () => RestoreRecentScanRecordsAsync(version));
    }

    private async Task RestoreRecentScanRecordsAsync(int mutationVersion)
    {
        VideoDatabase database = _db;
        if (database == null || _isDisposed)
            return;

        IReadOnlyList<ScanRecord> records;
        try
        {
            records = database.GetRecentCompletedVideos(DateTime.Today, 20, "pc")
                .Select(record => new ScanRecord(
                    record.OrderId,
                    "已保存",
                    record.StartTime.ToString("HH:mm:ss"),
                    record.Mode))
                .ToArray();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(
                "ScanHistory",
                "Failed to restore recent scan records",
                ex);
            return;
        }

        await ApplyOnUiThreadAsync(() =>
        {
            if (_isDisposed
                || !ReferenceEquals(_db, database)
                || mutationVersion != Volatile.Read(ref _recentScanRecordMutationVersion))
                return;
            _allLogs.Clear();
            foreach (ScanRecord record in records)
                _allLogs.Add(record);
            FilterLogs();
        });
    }

    private void DisposeBackgroundUiRefreshes()
    {
        _todayStatsRefresh.Dispose();
        _archiveBackupSummaryRefresh.Dispose();
        _mobileBackupStatusRefresh.Dispose();
        _userscriptStatusRefresh.Dispose();
        _recentScanRecordsRefresh.Dispose();
    }

    private static async Task ApplyOnUiThreadAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        await dispatcher.InvokeAsync(action);
    }

    private readonly record struct MobileBackupStatusRow(
        string DeviceId,
        string Name,
        string Kind,
        int Count,
        bool Online);
}
