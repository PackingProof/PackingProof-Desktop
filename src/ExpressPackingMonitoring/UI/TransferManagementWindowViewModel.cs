using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI;

internal sealed class TransferManagementWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly TransferManagementSnapshotService _service;
    private readonly CancellationTokenSource _cts = new();
    private int _refreshing;
    private string _summaryText = "";
    private bool _isAttentionSummary;
    private string _emptyText = "";
    private string _footerText = "";
    private string _normalDevicesHeader = "";
    private string _failureOverflowText = "";
    private string _remainingUploadText = "";
    private string _todayReceivedCountText = "0";
    private bool _hasDetailSummary;
    private GridLength _deviceNameColumnWidth = new(112);
    private GridLength _todayColumnWidth = new(90);
    private GridLength _activityColumnWidth = new(78);
    private GridLength _attentionColumnWidth = new(56);
    private UploadSlotItem? _uploadSlot;
    private bool _hasUploadSlot;
    private bool _hasHostDevices;
    private bool _hasFailureRows;
    private bool _hasFailureOverflow;
    private bool _hasRemainingUpload;
    private bool _hasNormalDevices;
    private bool _isNormalDevicesExpanded = true;
    private bool _canRetryAll;
    private string _stopReasonText = "";
    private readonly Func<bool>? _readUploadPaused;
    private readonly Func<Task<bool>>? _toggleUploadPause;
    private bool _isUploadPauseVisible;
    private bool _isUploadPaused;
    private readonly bool _canCheckForUpdates;
    private bool _isCurrentPcUpdateRequired;

    public TransferManagementWindowViewModel(
        TransferManagementSnapshotService service,
        Func<bool>? isUploadPaused = null,
        Func<Task<bool>>? toggleUploadPause = null,
        bool canCheckForUpdates = false)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _readUploadPaused = isUploadPaused;
        _toggleUploadPause = toggleUploadPause;
        _canCheckForUpdates = canCheckForUpdates;
        _isUploadPauseVisible = !IsHostMode && toggleUploadPause != null;
        RetryAllCommand = new AsyncRelayCommand(RetryAllAsync, () => CanRetryAll);
        RetryTaskCommand = new AsyncRelayCommand<TransferFailureItem>(
            RetryTaskAsync,
            item => item != null);
        ToggleNormalDevicesCommand = new RelayCommand(ToggleNormalDevices);
        ToggleUploadPauseCommand = new AsyncRelayCommand(
            ToggleUploadPauseAsync,
            () => IsUploadPauseVisible);
        ApplySnapshot(TransferManagementSnapshot.Empty());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsHostMode => _service.Mode == TransferManagementMode.HostDevices;

    public ObservableCollection<HostDeviceItem> AttentionDevices { get; } = [];
    public ObservableCollection<HostDeviceItem> NormalDevices { get; } = [];
    public ObservableCollection<TransferFailureItem> FailureItems { get; } = [];

    public string SummaryText
    {
        get => _summaryText;
        private set => SetField(ref _summaryText, value);
    }

    public bool IsAttentionSummary
    {
        get => _isAttentionSummary;
        private set => SetField(ref _isAttentionSummary, value);
    }

    public string EmptyText
    {
        get => _emptyText;
        private set => SetField(ref _emptyText, value);
    }

    public string FooterText
    {
        get => _footerText;
        private set => SetField(ref _footerText, value);
    }

    public string NormalDevicesHeader
    {
        get => _normalDevicesHeader;
        private set => SetField(ref _normalDevicesHeader, value);
    }

    public string FailureOverflowText
    {
        get => _failureOverflowText;
        private set => SetField(ref _failureOverflowText, value);
    }

    public string RemainingUploadText
    {
        get => _remainingUploadText;
        private set => SetField(ref _remainingUploadText, value);
    }

    public string TodayReceivedCountText
    {
        get => _todayReceivedCountText;
        private set => SetField(ref _todayReceivedCountText, value);
    }

    public string TodaySummaryLabel =>
        AppLanguage.Get(IsHostMode ? "今日已接收" : "今日已上传");

    public GridLength DeviceNameColumnWidth
    {
        get => _deviceNameColumnWidth;
        private set => SetField(ref _deviceNameColumnWidth, value);
    }

    public GridLength TodayColumnWidth
    {
        get => _todayColumnWidth;
        private set => SetField(ref _todayColumnWidth, value);
    }

    public GridLength ActivityColumnWidth
    {
        get => _activityColumnWidth;
        private set => SetField(ref _activityColumnWidth, value);
    }

    public GridLength AttentionColumnWidth
    {
        get => _attentionColumnWidth;
        private set => SetField(ref _attentionColumnWidth, value);
    }

    public UploadSlotItem? UploadSlot
    {
        get => _uploadSlot;
        private set => SetField(ref _uploadSlot, value);
    }

    public bool HasUploadSlot
    {
        get => _hasUploadSlot;
        private set => SetField(ref _hasUploadSlot, value);
    }

    public bool HasHostDevices
    {
        get => _hasHostDevices;
        private set => SetField(ref _hasHostDevices, value);
    }

    public bool HasDetailSummary
    {
        get => _hasDetailSummary;
        private set => SetField(ref _hasDetailSummary, value);
    }

    public bool HasFailureRows
    {
        get => _hasFailureRows;
        private set => SetField(ref _hasFailureRows, value);
    }

    public bool HasFailureOverflow
    {
        get => _hasFailureOverflow;
        private set => SetField(ref _hasFailureOverflow, value);
    }

    public bool HasRemainingUpload
    {
        get => _hasRemainingUpload;
        private set => SetField(ref _hasRemainingUpload, value);
    }

    public bool HasNormalDevices
    {
        get => _hasNormalDevices;
        private set => SetField(ref _hasNormalDevices, value);
    }

    public bool IsNormalDevicesExpanded
    {
        get => _isNormalDevicesExpanded;
        private set
        {
            if (SetField(ref _isNormalDevicesExpanded, value))
                OnPropertyChanged(nameof(NormalDevicesToggleText));
        }
    }

    public string NormalDevicesToggleText => AppLanguage.Get(
        IsNormalDevicesExpanded ? "收起" : "展开");

    public bool CanRetryAll
    {
        get => _canRetryAll;
        private set
        {
            if (SetField(ref _canRetryAll, value))
                RetryAllCommand.NotifyCanExecuteChanged();
        }
    }

    public string StopReasonText
    {
        get => _stopReasonText;
        private set => SetField(ref _stopReasonText, value);
    }

    public bool IsUploadPauseVisible
    {
        get => _isUploadPauseVisible;
        private set
        {
            if (SetField(ref _isUploadPauseVisible, value))
                ToggleUploadPauseCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsUploadPaused
    {
        get => _isUploadPaused;
        private set
        {
            if (SetField(ref _isUploadPaused, value))
                OnPropertyChanged(nameof(UploadPauseButtonText));
        }
    }

    public bool IsCurrentPcUpdateRequired
    {
        get => _isCurrentPcUpdateRequired;
        private set
        {
            if (SetField(ref _isCurrentPcUpdateRequired, value))
                OnPropertyChanged(nameof(ShowCheckUpdateButton));
        }
    }

    public bool ShowCheckUpdateButton =>
        _canCheckForUpdates
        && !IsHostMode
        && IsCurrentPcUpdateRequired;

    public string UploadPauseButtonText =>
        AppLanguage.Get(IsUploadPaused ? "继续上传" : "暂停上传");

    public IAsyncRelayCommand RetryAllCommand { get; }
    public IAsyncRelayCommand<TransferFailureItem> RetryTaskCommand { get; }
    public IRelayCommand ToggleNormalDevicesCommand { get; }
    public IAsyncRelayCommand ToggleUploadPauseCommand { get; }

    public async Task RefreshAsync()
    {
        if (_cts.IsCancellationRequested
            || Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
        {
            return;
        }

        try
        {
            TransferManagementSnapshot snapshot =
                await _service.LoadAsync(_cts.Token);
            if (!_cts.IsCancellationRequested)
                ApplySnapshot(snapshot);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    public async Task<BackupDeviceRemovalResult> ForgetDeviceAsync(
        HostDeviceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        BackupDeviceRemovalResult result = await _service.ForgetHostDeviceAsync(
            item.DeviceId,
            _cts.Token);
        if (result.Status == BackupDeviceRemovalStatus.Removed)
            await RefreshAsync();
        return result;
    }

    internal void ApplySnapshotForPreview(TransferManagementSnapshot snapshot) =>
        ApplySnapshot(snapshot);

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task RetryTaskAsync(TransferFailureItem? item)
    {
        if (item == null)
            return;
        await _service.RetryTaskAsync(item.TaskId, _cts.Token);
        await RefreshAsync();
    }

    private async Task RetryAllAsync()
    {
        if (!CanRetryAll)
            return;
        await _service.RetryAllAsync(_cts.Token);
        await RefreshAsync();
    }

    private async Task ToggleUploadPauseAsync()
    {
        if (_toggleUploadPause == null || !IsUploadPauseVisible)
            return;
        if (!await _toggleUploadPause())
            return;
        IsUploadPaused = _readUploadPaused?.Invoke() ?? false;
        await RefreshAsync();
    }

    private void ToggleNormalDevices() =>
        IsNormalDevicesExpanded = !IsNormalDevicesExpanded;

    private void ApplySnapshot(TransferManagementSnapshot snapshot)
    {
        IsUploadPaused = snapshot.HostState == HostConnectionState.PausedByUser;
        ApplySummary(snapshot);
        ApplyHostDevices(snapshot.HostDevices);
        ApplyTransferRows(snapshot);
        FooterText = AppLanguage.Get(
            IsHostMode
                ? "设备详情中可继续管理连接方式、定时开关机和更新"
                : "失败录像会保留在本机，修好后可重新上传");
        OnPropertyChanged(nameof(IsHostMode));
    }

    private void ApplySummary(TransferManagementSnapshot snapshot)
    {
        if (IsHostMode)
        {
            IsCurrentPcUpdateRequired = false;
            IsAttentionSummary = snapshot.NeedsActionCount > 0;
            SummaryText = IsAttentionSummary
                ? AppLanguage.Format("{0} 台需要处理", snapshot.NeedsActionCount)
                : "";
            TodayReceivedCountText = snapshot.TodayReceivedCount.ToString(
                CultureInfo.CurrentCulture);
            StopReasonText = snapshot.HostState == HostConnectionState.StorageBlocked
                ? AppLanguage.Get("主机存储空间不足")
                : "";
            HasDetailSummary = StopReasonText.Length > 0;
            return;
        }

        bool pausedByUser = IsUploadPaused;
        IsCurrentPcUpdateRequired =
            snapshot.HostState == HostConnectionState.ClientUpdateRequired
            && !pausedByUser
            && snapshot.NeedsActionCount == 0;
        IsAttentionSummary = snapshot.NeedsActionCount > 0;
        SummaryText = snapshot.NeedsActionCount > 0
            ? AppLanguage.Format("{0} 个异常", snapshot.NeedsActionCount)
            : "";
        StopReasonText = pausedByUser || snapshot.NeedsActionCount > 0
            ? ""
            : snapshot.HostState switch
        {
            HostConnectionState.Offline =>
                AppLanguage.Get("备份主机离线，恢复后自动继续"),
            HostConnectionState.AwaitingApproval =>
                AppLanguage.Get("等待主机允许连接"),
            HostConnectionState.ClientUpdateRequired =>
                AppLanguage.Get("当前电脑版本过低，请更新后再备份"),
            HostConnectionState.HostUpdateRequired =>
                AppLanguage.Get("备份主机版本过低，请先在主机上更新后继续备份"),
            HostConnectionState.StorageBlocked =>
                AppLanguage.Get("备份主机空间不足，上传已暂停"),
            HostConnectionState.WriteBusy =>
                AppLanguage.Get("主机写入繁忙，稍后自动继续"),
            HostConnectionState.Unknown when snapshot.PendingCount > 0
                                             && snapshot.UpcomingUpload != null =>
                "",
            HostConnectionState.Unknown when snapshot.PendingCount > 0 =>
                AppLanguage.Get("正在检查备份主机"),
            _ when snapshot.NextRetryAt is DateTimeOffset retryAt &&
                   snapshot.PendingCount > 0 =>
                AppLanguage.Format(
                    "{0} 自动重试",
                    retryAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)),
            _ => ""
        };
        TodayReceivedCountText = snapshot.TodayReceivedCount.ToString(
            CultureInfo.CurrentCulture);
        HasDetailSummary = StopReasonText.Length > 0;
    }

    private void ApplyHostDevices(IReadOnlyList<HostDeviceRow> rows)
    {
        HostDeviceItem[] items = rows
            .Select(CreateHostDeviceItem)
            .ToArray();
        DeviceNameColumnWidth = new GridLength(
            CalculateDeviceNameColumnWidth(
                items.Select(row => row.DisplayName)));
        TodayColumnWidth = new GridLength(
            CalculateTextColumnWidth(
                items.Select(row =>
                    $"{AppLanguage.Get("今日")} {row.TodayCountText}"),
                fontSize: 12,
                minimum: 96,
                maximum: 124,
                padding: 14));
        ActivityColumnWidth = new GridLength(
            CalculateTextColumnWidth(
                items.Select(row => row.ActivityText),
                fontSize: 11,
                minimum: 68,
                maximum: 92,
                padding: 10));
        AttentionColumnWidth = new GridLength(
            CalculateTextColumnWidth(
                items.Select(row => row.AttentionText),
                fontSize: 12,
                minimum: 88,
                maximum: 108,
                padding: 14));
        var attention = items
            .Where(item => item.HasAttentionText)
            .ToArray();
        var normal = items
            .Where(item => !item.HasAttentionText)
            .ToArray();

        SyncCollection(AttentionDevices, attention);
        SyncCollection(NormalDevices, normal);
        HasHostDevices = rows.Count > 0;
        HasNormalDevices = normal.Length > 0;
        NormalDevicesHeader = AppLanguage.Format("其余 {0} 台正常", normal.Length);
        EmptyText = AppLanguage.Get("暂无手机/电脑设备");
    }

    private void ApplyTransferRows(TransferManagementSnapshot snapshot)
    {
        TransferFailureItem[] failures = snapshot.FailurePreview
            .Select(CreateFailureItem)
            .ToArray();

        UploadSlotItem? uploadSlot = null;
        if (snapshot.CurrentUpload is TransferTaskRow current)
        {
            uploadSlot = CreateUploadSlotItem(
                current,
                AppLanguage.Get("正在上传"),
                isPaused: false);
        }
        else if (IsUploadPaused && snapshot.PausedUpload is TransferTaskRow paused)
        {
            uploadSlot = CreateUploadSlotItem(
                paused,
                AppLanguage.Get("已暂停"),
                isPaused: true);
        }
        else if (snapshot.UpcomingUpload is TransferTaskRow upcoming
                 && (snapshot.HostState == HostConnectionState.Online
                     || snapshot.HostState == HostConnectionState.Unknown
                     || snapshot.HostState == HostConnectionState.ClientUpdateRequired
                     || snapshot.HostState == HostConnectionState.HostUpdateRequired
                     || IsUploadPaused))
        {
            uploadSlot = CreateUploadSlotItem(
                upcoming,
                IsUploadPaused
                    ? AppLanguage.Get("已暂停")
                    : snapshot.HostState is HostConnectionState.ClientUpdateRequired
                        or HostConnectionState.HostUpdateRequired
                        ? AppLanguage.Get("等待更新")
                    : snapshot.HostState == HostConnectionState.Unknown
                        ? AppLanguage.Get("正在检查主机")
                        : FormatUploadWait(snapshot.NextUploadAt),
                IsUploadPaused);
        }

        UploadSlot = uploadSlot;
        HasUploadSlot = uploadSlot != null;
        SyncCollection(FailureItems, failures);
        HasFailureRows = failures.Length > 0;
        HasFailureOverflow = snapshot.FailureOverflowCount > 0;
        FailureOverflowText = snapshot.FailureOverflowCount > 0
            ? AppLanguage.Format("还有 {0} 个失败录像", snapshot.FailureOverflowCount)
            : "";
        HasRemainingUpload = snapshot.PendingCount > 0;
        RemainingUploadText = HasRemainingUpload
            ? AppLanguage.Format("还有 {0} 个待上传", snapshot.PendingCount)
            : "";
        CanRetryAll = snapshot.NeedsActionCount > 0;
    }

    private UploadSlotItem CreateUploadSlotItem(
        TransferTaskRow row,
        string statusText,
        bool isPaused)
    {
        TransferFailureItem item = CreateFailureItem(row);
        return new UploadSlotItem(
            statusText,
            item.SummaryText,
            item.Progress,
            item.ProgressText,
            isPaused);
    }

    internal static string FormatUploadWait(DateTimeOffset? nextUploadAt)
    {
        if (nextUploadAt is not DateTimeOffset next)
            return AppLanguage.Get("冷却中");

        TimeSpan wait = next - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero)
            return AppLanguage.Get("即将上传");
        if (wait <= TimeSpan.FromMinutes(1))
            return AppLanguage.Format(
                "{0} 秒后上传",
                Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)));
        return AppLanguage.Format(
            "{0} 分钟后上传",
            Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes)));
    }

    private HostDeviceItem CreateHostDeviceItem(HostDeviceRow row)
    {
        string attention = row.Attention switch
        {
            DeviceAttentionKind.Offline => AppLanguage.Get("已离线"),
            _ => ""
        };
        return new HostDeviceItem(
            row.DeviceId,
            row.DisplayName,
            row.TodayReceivedCount,
            FormatActivity(row.LastActivityAt, row.IsOnline),
            attention,
            row.IsOnline);
    }

    private TransferFailureItem CreateFailureItem(TransferTaskRow row)
    {
        string size = FormatBytes(row.FileSizeBytes);
        double progress = row.FileSizeBytes <= 0
            ? 0
            : Math.Clamp(row.SentBytes * 100d / row.FileSizeBytes, 0, 100);
        return new TransferFailureItem(
            row.TaskId,
            row.Identifier,
            size,
            AppLanguage.TranslateRuntime(row.Reason),
            progress,
            AppLanguage.Format("{0:F0}%", progress));
    }

    internal static string FormatActivity(DateTimeOffset? value, bool isOnline)
    {
        if (value is not DateTimeOffset last)
            return isOnline ? AppLanguage.Get("刚刚") : "";
        TimeSpan age = DateTimeOffset.Now - last.ToLocalTime();
        if (age < TimeSpan.FromMinutes(1))
            return AppLanguage.Get("刚刚");
        if (age < TimeSpan.FromHours(1))
            return AppLanguage.Format("{0} 分钟前", Math.Max(1, (int)age.TotalMinutes));
        if (last.ToLocalTime().Date == DateTime.Today)
            return last.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
        return last.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            bytes = 0;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        string format = value >= 100 || unit == 0 ? "F0" : value >= 10 ? "F1" : "F2";
        return $"{value.ToString(format, CultureInfo.CurrentCulture)} {units[unit]}";
    }

    internal static double CalculateDeviceNameColumnWidth(
        IEnumerable<string> displayNames)
    {
        return CalculateTextColumnWidth(
            displayNames,
            fontSize: 12,
            minimum: 80,
            maximum: 120,
            padding: 13);
    }

    private static double CalculateTextColumnWidth(
        IEnumerable<string> values,
        double fontSize,
        double minimum,
        double maximum,
        double padding)
    {
        double maximumWidth = 0;
        foreach (string value in values)
        {
            double width = 0;
            foreach (Rune rune in (value ?? "").EnumerateRunes())
            {
                width += rune.Value switch
                {
                    >= 0x2E80 => 12,
                    ' ' => 4,
                    _ when Rune.IsUpper(rune) => 7.5,
                    _ when Rune.IsDigit(rune) => 7,
                    _ => 6.5
                };
            }
            maximumWidth = Math.Max(maximumWidth, width);
        }

        return Math.Clamp(
            Math.Ceiling(maximumWidth * fontSize / 12) + padding,
            minimum,
            maximum);
    }

    private static void SyncCollection<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> values)
    {
        if (target.SequenceEqual(values))
            return;
        target.Clear();
        foreach (T value in values)
            target.Add(value);
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed record HostDeviceItem(
    string DeviceId,
    string DisplayName,
    int TodayReceivedCount,
    string ActivityText,
    string AttentionText,
    bool IsOnline)
{
    public string TodayCountText =>
        TodayReceivedCount.ToString(CultureInfo.CurrentCulture)
        + AppLanguage.Get("今日数量单位");
    public bool HasAttentionText => AttentionText.Length > 0;
}

internal sealed record UploadSlotItem(
    string StatusText,
    string SummaryText,
    double Progress,
    string ProgressText,
    bool IsPaused);

internal sealed record TransferFailureItem(
    long TaskId,
    string Identifier,
    string FileSizeText,
    string Reason,
    double Progress,
    string ProgressText)
{
    public string SummaryText => $"{Identifier} · {FileSizeText}";
    public bool HasReason => Reason.Length > 0;
}
