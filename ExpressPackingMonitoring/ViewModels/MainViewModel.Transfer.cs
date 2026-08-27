using System.IO;
using System.Windows;
using System.Windows.Input;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using CommunityToolkit.Mvvm.Input;

namespace ExpressPackingMonitoring.ViewModels;

internal readonly record struct RecordingTransferCardState(
    string ShortStatusText,
    string DetailText);

public partial class MainViewModel
{
    private RecordingTransferService? _recordingTransferService;
    private RecordingTransferQueueStore? _recordingTransferStore;
    private string _recordingTransferShortStatusText = "检查中";
    private string _recordingTransferStatusText = "正在检查待上传录像";
    private int _pendingRecordingTransferCount;
    private double _recordingTransferProgress;
    private string _lastRecordingTransferError = "";
    private string _boundHostOnlineStatusText = "检查中";
    private string _recordingCacheUsageText = "正在检查本地缓存";
    private string _recordingCacheStatusText = "本地缓存会在接近上限时自动清理";
    private double _recordingCacheUsagePercent;
    private bool _isRecordingCacheWarning;
    private bool _recordingCacheBlockedDialogShown;
    private int _recordingCacheEmergencyStopRequested;
    private DateTime _lastRecordingWorkstationHeartbeatAt = DateTime.MinValue;
    private DateTime _nextRecordingWorkstationDiscoveryAt = DateTime.MinValue;
    private int _recordingWorkstationDiscoveryFailureCount;
    private int _recordingWorkstationHeartbeatInProgress;
    private readonly object _recordingCacheMaintenanceLock = new();
    private string _recordingCacheInventoryRoot = "";
    private bool _recordingCacheInventoryInitialized;
    private DateTime _recordingCacheSnapshotAt = DateTime.MinValue;
    private bool _recordingCacheCanFit;
    private bool _recordingCacheSnapshotAvailable;

    public ICommand OpenBoundHostCommand { get; private set; } = null!;
    public ICommand RetryRecordingTransfersCommand { get; private set; } = null!;
    public ICommand ChangeBoundHostCommand { get; private set; } = null!;

    public bool IsRecordingWorkstation =>
        string.Equals(
            Config?.DeploymentPreset,
            DeploymentPresets.RecordingWorkstation,
            StringComparison.OrdinalIgnoreCase);

    public bool IsMainConnectionVisible => ShouldShowMainConnection(Config);
    public string MainConnectionButtonText => GetMainConnectionButtonText(Config);
    public string MainConnectionButtonToolTip => ShouldManageBoundHostFromMainConnection(Config)
        ? "连接、绑定或管理保存电脑"
        : WorkstationStatusToolTip;

    public string RecordingTransferStatusText
    {
        get => _recordingTransferStatusText;
        private set { _recordingTransferStatusText = value; OnPropertyChanged(); }
    }

    public int PendingRecordingTransferCount
    {
        get => _pendingRecordingTransferCount;
        private set { _pendingRecordingTransferCount = value; OnPropertyChanged(); }
    }

    public double RecordingTransferProgress
    {
        get => _recordingTransferProgress;
        private set { _recordingTransferProgress = value; OnPropertyChanged(); }
    }

    public string LastRecordingTransferError
    {
        get => _lastRecordingTransferError;
        private set { _lastRecordingTransferError = value; OnPropertyChanged(); }
    }

    public string BoundHostAddress => Config?.LastKnownHostAddress ?? "";
    public string BoundHostNameDisplay
    {
        get
        {
            string name = Config?.LastKnownHostNodeName?.Trim() ?? "";
            if (name.Length > 0)
                return name;
            return string.IsNullOrWhiteSpace(BoundHostAddress)
                ? "尚未绑定保存主机"
                : "已绑定主机";
        }
    }

    public string RecordingTransferShortStatusText
    {
        get => _recordingTransferShortStatusText;
        private set { _recordingTransferShortStatusText = value; OnPropertyChanged(); }
    }

    public string BoundHostDisplay
    {
        get
        {
            string name = Config?.LastKnownHostNodeName?.Trim() ?? "";
            string address = BoundHostAddress;
            return name.Length == 0 ? address : $"{name} · {address}";
        }
    }

    public string BoundHostOnlineStatusText
    {
        get => _boundHostOnlineStatusText;
        private set { _boundHostOnlineStatusText = value; OnPropertyChanged(); }
    }

    private void InitializeRecordingTransfers()
    {
        OpenBoundHostCommand = new RelayCommand(OpenBoundHost);
        RetryRecordingTransfersCommand = new RelayCommand(RetryRecordingTransfers);
        ChangeBoundHostCommand = new RelayCommand(() => ChangeBoundHost());
        if (!IsRecordingWorkstation || _db == null)
            return;

        try
        {
            _recordingTransferStore = new RecordingTransferQueueStore(_dbFilePath);
            _recordingTransferService = new RecordingTransferService(
                _recordingTransferStore,
                _db,
                () => Config,
                hostAddressChanged: PersistResolvedRecordingHost);
            _recordingTransferService.ProgressChanged += OnRecordingTransferProgressChanged;
            _recordingTransferService.Start();
            RefreshRecordingTransferSummary();
            RunRecordingCacheCleanup();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("RecordingTransfer", "Transfer service startup failed", ex);
            RecordingTransferShortStatusText = "上传失败";
            RecordingTransferStatusText = "上传启动失败，录像保留在本机";
            LastRecordingTransferError = ex.Message;
        }
    }

    private void OpenBoundHost()
    {
        if (!IsRecordingWorkstation || string.IsNullOrWhiteSpace(Config.LastKnownHostAddress))
        {
            ShowToast("尚未绑定保存主机", ToastSeverity.Warning);
            return;
        }
        string url = MobileConnectionService.BuildAccessUrl(
            Config.LastKnownHostAddress,
            requireAccessKey: true,
            Config.LastKnownHostAccessKey);
        if (!WorkstationNetwork.TryOpenUrl(url, out string error))
            AppDialog.Error(null, error, "打开主机录像失败");
    }

    private void RetryRecordingTransfers()
    {
        _recordingTransferService?.EnqueueCompletedRecordings();
        _recordingTransferService?.RetryNow();
        RecordingTransferShortStatusText = "检查中";
        RecordingTransferStatusText = "正在重新连接";
    }

    public string RecordingCacheUsageText
    {
        get => _recordingCacheUsageText;
        private set { _recordingCacheUsageText = value; OnPropertyChanged(); }
    }

    public string RecordingCacheStatusText
    {
        get => _recordingCacheStatusText;
        private set { _recordingCacheStatusText = value; OnPropertyChanged(); }
    }

    public double RecordingCacheUsagePercent
    {
        get => _recordingCacheUsagePercent;
        private set { _recordingCacheUsagePercent = value; OnPropertyChanged(); }
    }

    public bool IsRecordingCacheWarning
    {
        get => _isRecordingCacheWarning;
        private set { _isRecordingCacheWarning = value; OnPropertyChanged(); }
    }

    private void QueueRecordingWorkstationHeartbeat(bool force = false)
    {
        if (!IsRecordingWorkstation
            || string.IsNullOrWhiteSpace(Config.LastKnownHostAddress)
            || string.IsNullOrWhiteSpace(Config.NodeId))
            return;
        DateTime now = DateTime.UtcNow;
        if (!force
            && now - _lastRecordingWorkstationHeartbeatAt < TimeSpan.FromSeconds(15))
            return;
        if (Interlocked.Exchange(ref _recordingWorkstationHeartbeatInProgress, 1) != 0)
            return;

        if (force)
            _nextRecordingWorkstationDiscoveryAt = DateTime.MinValue;
        _lastRecordingWorkstationHeartbeatAt = now;
        _ = SendRecordingWorkstationHeartbeatAsync();
    }

    private async Task SendRecordingWorkstationHeartbeatAsync()
    {
        try
        {
            RecordingWorkstationHeartbeatResult heartbeat =
                await WorkstationNetwork.SendRecordingWorkstationHeartbeatAsync(
                Config.LastKnownHostAddress,
                Config.NodeId,
                Config.NodeName,
                Config.WebServerPort,
                connected: true,
                nicknameCustomized: Config.NodeNameCustomized);
            if (heartbeat.Online)
            {
                _recordingWorkstationDiscoveryFailureCount = 0;
                _nextRecordingWorkstationDiscoveryAt = DateTime.MinValue;
            }
            else if (DateTime.UtcNow >= _nextRecordingWorkstationDiscoveryAt)
            {
                PackingProofNodeInfo? resolvedHost = await WorkstationNetwork.FindHostByNodeIdAsync(
                    Config.LastKnownHostNodeId,
                    Config.LastKnownHostAddress,
                    Config.WebServerPort);
                if (resolvedHost != null
                    && !string.Equals(
                        WorkstationNetwork.NormalizeAddress(resolvedHost.Address),
                        WorkstationNetwork.NormalizeAddress(Config.LastKnownHostAddress),
                        StringComparison.OrdinalIgnoreCase))
                {
                    PersistResolvedRecordingHost(resolvedHost);
                    heartbeat = await WorkstationNetwork.SendRecordingWorkstationHeartbeatAsync(
                        resolvedHost.Address,
                        Config.NodeId,
                        Config.NodeName,
                        Config.WebServerPort,
                        connected: true,
                        nicknameCustomized: Config.NodeNameCustomized);
                }

                if (heartbeat.Online)
                {
                    _recordingWorkstationDiscoveryFailureCount = 0;
                    _nextRecordingWorkstationDiscoveryAt = DateTime.MinValue;
                }
                else
                {
                    _recordingWorkstationDiscoveryFailureCount = Math.Min(
                        _recordingWorkstationDiscoveryFailureCount + 1,
                        5);
                    int backoffSeconds = 15 * (1 << Math.Min(
                        _recordingWorkstationDiscoveryFailureCount - 1,
                        4));
                    _nextRecordingWorkstationDiscoveryAt = DateTime.UtcNow.AddSeconds(backoffSeconds);
                }
            }
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_isDisposed) return;
                if (heartbeat.Online)
                    ApplyAssignedComputerNickname(heartbeat.AssignedDisplayName);
                BoundHostOnlineStatusText = heartbeat.Online ? "在线" : "离线";
                RefreshRecordingTransferSummary();
            });
        }
        finally
        {
            Interlocked.Exchange(ref _recordingWorkstationHeartbeatInProgress, 0);
        }
    }

    private void ChangeBoundHost(Window? owner = null)
    {
        if (!IsRecordingWorkstation) return;

        var window = new ViewerClientWindow(
            Config,
            DeploymentPresets.RecordingWorkstation);
        Window? dialogOwner = owner ?? Application.Current?.MainWindow;
        if (dialogOwner != null)
            window.Owner = dialogOwner;
        if (window.ShowDialog() == true)
        {
            OnPropertyChanged(nameof(BoundHostAddress));
            OnPropertyChanged(nameof(BoundHostNameDisplay));
            OnPropertyChanged(nameof(BoundHostDisplay));
            RetryRecordingTransfers();
        }
    }

    private void ApplyAssignedComputerNickname(string? assignedDisplayName)
    {
        string name = assignedDisplayName?.Trim() ?? "";
        if (name.Length is < 1 or > 20
            || name.Any(char.IsControl)
            || string.Equals(name, Config.NodeName, StringComparison.Ordinal))
        {
            return;
        }

        if (!WorkstationConfigStore.TryUpdate(
                config =>
                {
                    if (string.Equals(config.NodeId, Config.NodeId, StringComparison.OrdinalIgnoreCase))
                        config.NodeName = name;
                },
                out AppConfig saved,
                out _)
            || !string.Equals(saved.NodeId, Config.NodeId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Config.NodeName = saved.NodeName;
        Config.NodeNameCustomized = saved.NodeNameCustomized;
        OnPropertyChanged(nameof(ComputerDisplayName));
    }

    internal static bool ShouldPromptRecordingWorkstationHostBinding(AppConfig? config) =>
        string.Equals(
            DeploymentPresets.Normalize(config?.DeploymentPreset),
            DeploymentPresets.RecordingWorkstation,
            StringComparison.Ordinal)
        && (string.IsNullOrWhiteSpace(config?.LastKnownHostNodeId)
            || string.IsNullOrWhiteSpace(config.LastKnownHostAddress)
            || string.IsNullOrWhiteSpace(config.LastKnownHostAccessKey));

    private async Task RunRecordingWorkstationHostBindingPromptIfNeededAsync(Window owner)
    {
        if (_isDisposed || !ShouldPromptRecordingWorkstationHostBinding(Config))
            return;

        Task<bool>? startupTask = _webServerStartupTask;
        if (startupTask != null)
        {
            bool lanReady;
            try
            {
                lanReady = await startupTask;
            }
            catch
            {
                return;
            }

            if (!lanReady)
                return;
        }

        if (_isDisposed || !ShouldPromptRecordingWorkstationHostBinding(Config))
            return;

        ChangeBoundHost(owner);
    }

    internal static bool ShouldShowMainConnection(AppConfig? config)
    {
        string preset = DeploymentPresets.Normalize(config?.DeploymentPreset);
        return preset == DeploymentPresets.RecordingHost
            || preset == DeploymentPresets.RecordingWorkstation;
    }

    internal static bool ShouldManageBoundHostFromMainConnection(AppConfig? config) =>
        string.Equals(
            DeploymentPresets.Normalize(config?.DeploymentPreset),
            DeploymentPresets.RecordingWorkstation,
            StringComparison.Ordinal);

    internal static string GetMainConnectionButtonText(AppConfig? config) =>
        DeploymentPresets.Normalize(config?.DeploymentPreset) switch
        {
            DeploymentPresets.RecordingWorkstation => "管理保存主机",
            DeploymentPresets.RecordingHost or DeploymentPresets.MobileBackupHost => "连接手机/电脑",
            _ => "连接保存主机"
        };

    public void ShowMainConnection(Window? owner = null)
    {
        if (ShouldManageBoundHostFromMainConnection(Config))
        {
            ChangeBoundHost(owner);
            return;
        }

        if (ShouldShowMainConnection(Config))
            ShowMobileConnection(owner);
    }

    private void OnRecordingTransferProgressChanged(RecordingTransferProgress progress)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_isDisposed) return;
            RecordingTransferProgress = progress.TotalBytes <= 0
                ? 0
                : Math.Clamp(progress.SentBytes * 100d / progress.TotalBytes, 0, 100);
            if (progress.State == RecordingTransferStates.Uploaded)
            {
                RefreshRecordingTransferSummary(recentlyUploaded: true);
                RunRecordingCacheCleanup();
            }
            else
            {
                if (progress.State == RecordingTransferStates.Failed)
                    LastRecordingTransferError = progress.Error;
                RefreshRecordingTransferSummary();
            }
        });
    }

    private void RefreshRecordingTransferSummary(bool recentlyUploaded = false)
    {
        RecordingTransferSummary? summary = _recordingTransferStore?.GetSummary();
        if (summary == null) return;
        PendingRecordingTransferCount =
            summary.PendingCount + summary.UploadingCount + summary.FailedCount;
        LastRecordingTransferError = summary.LastError;
        bool requiresReconnect = IsReconnectRequiredError(summary.LastError);
        RecordingTransferCardState cardState = BuildRecordingTransferCardState(
            summary.PendingCount,
            summary.UploadingCount,
            summary.FailedCount,
            RecordingTransferProgress,
            string.Equals(BoundHostOnlineStatusText, "离线", StringComparison.Ordinal),
            recentlyUploaded,
            requiresReconnect);
        RecordingTransferShortStatusText = cardState.ShortStatusText;
        RecordingTransferStatusText = cardState.DetailText;
    }

    private void PersistResolvedRecordingHost(PackingProofNodeInfo node)
    {
        if (!string.Equals(
                node.NodeId,
                Config.LastKnownHostNodeId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!WorkstationConfigStore.TryUpdate(
                config =>
                {
                    if (string.Equals(
                            config.LastKnownHostNodeId,
                            node.NodeId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        config.LastKnownHostAddress = node.Address;
                        config.LastKnownHostNodeName = node.NodeName;
                    }
                },
                out AppConfig saved,
                out _)
            || !string.Equals(
                saved.LastKnownHostNodeId,
                node.NodeId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_isDisposed
                || !string.Equals(
                    Config.LastKnownHostNodeId,
                    node.NodeId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Config.LastKnownHostAddress = saved.LastKnownHostAddress;
            Config.LastKnownHostNodeName = saved.LastKnownHostNodeName;
            OnPropertyChanged(nameof(BoundHostAddress));
            OnPropertyChanged(nameof(BoundHostNameDisplay));
            OnPropertyChanged(nameof(BoundHostDisplay));
            QueueRecordingWorkstationHeartbeat(force: true);
        });
    }

    internal static bool IsReconnectRequiredError(string? error) =>
        !string.IsNullOrWhiteSpace(error)
        && (error.Contains("设备令牌无效", StringComparison.Ordinal)
            || error.Contains("重新连接保存主机", StringComparison.Ordinal)
            || error.Contains("未允许本机连接", StringComparison.Ordinal));

    internal static RecordingTransferCardState BuildRecordingTransferCardState(
        int pendingCount,
        int uploadingCount,
        int failedCount,
        double progress,
        bool hostOffline,
        bool recentlyUploaded,
        bool requiresReconnect = false)
    {
        int totalCount = Math.Max(0, pendingCount)
            + Math.Max(0, uploadingCount)
            + Math.Max(0, failedCount);
        if (uploadingCount > 0)
        {
            return new RecordingTransferCardState(
                "上传中",
                $"正在上传 {Math.Clamp(progress, 0, 100):F0}% · 共 {totalCount} 个待上传");
        }

        if (failedCount > 0)
        {
            if (requiresReconnect)
            {
                return new RecordingTransferCardState(
                    "需要重新连接",
                    "需要重新连接保存主机，录像仍保存在本机");
            }
            return new RecordingTransferCardState(
                "上传失败",
                $"{totalCount} 个录像等待重试，录像仍保存在本机");
        }

        if (totalCount > 0)
        {
            return new RecordingTransferCardState(
                "待上传",
                hostOffline
                    ? $"{totalCount} 个录像已保存在本机，联网后自动上传"
                    : $"{totalCount} 个录像等待上传");
        }

        return new RecordingTransferCardState(
            "已完成",
            recentlyUploaded ? "最近录像已上传" : "暂无待上传录像");
    }

    private void RunRecordingCacheCleanup()
    {
        if (!IsRecordingWorkstation)
            return;
        _ = Task.Run(() => RunRecordingCacheMaintenance(
            RecordingWorkstationCachePolicy.RecordingAndPackagingHeadroomBytes));
    }

    private RecordingCacheMaintenanceResult RunRecordingCacheMaintenance(
        long requiredHeadroomBytes,
        bool forceReconcile = false)
    {
        if (!IsRecordingWorkstation || _recordingTransferStore == null || _db == null)
            return RecordingCacheMaintenanceResult.Unavailable("本地缓存服务尚未准备好");

        lock (_recordingCacheMaintenanceLock)
        {
            try
            {
                StorageLocation location =
                    RecordingWorkstationCachePolicy.GetConfiguredLocation(Config)
                    ?? throw new IOException("尚未设置本地缓存位置");
                string cachePath = Path.GetFullPath(location.Path);
                if (!Directory.Exists(cachePath))
                    throw new DirectoryNotFoundException("本地缓存位置不存在，请重新选择");
                string? root = Path.GetPathRoot(cachePath);
                if (string.IsNullOrWhiteSpace(root))
                    throw new IOException("无法确定本地缓存所在磁盘");
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                    throw new IOException("本地缓存所在磁盘未就绪");

                if (forceReconcile
                    || !_recordingCacheInventoryInitialized
                    || !string.Equals(
                        _recordingCacheInventoryRoot,
                        cachePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    ReconcileRecordingCacheInventory(cachePath);
                }

                long cacheBytes = GetRecordingCacheInventoryBytes(cachePath);
                long configuredLimitBytes =
                    Math.Max(1L, Config.RecordingCacheMaxGB)
                    * StorageSpacePolicy.BytesPerGiB;
                long reserveBytes =
                    StorageSpacePolicy.GetEffectiveReserveBytes(location, drive);
                RecordingCacheSpaceSnapshot snapshot =
                    RecordingWorkstationCachePolicy.CalculateSpace(
                        cacheBytes,
                        configuredLimitBytes,
                        drive.AvailableFreeSpace,
                        reserveBytes);

                IReadOnlyList<RecordingTransferTask> uploaded =
                    _recordingTransferStore.GetUploadedWithLocalCache();
                var verifiedTasksByPath =
                    new Dictionary<string, List<RecordingTransferTask>>(
                        StringComparer.OrdinalIgnoreCase);
                var cleanupGroupsById =
                    new Dictionary<long, IReadOnlyList<RecordingTransferTask>>();
                var verifiedItems = new List<RecordingCacheCleanupItem>();
                foreach (RecordingTransferTask task in uploaded)
                {
                    if (task.RemoteVideoRecordId is not long remoteRecordId
                        || remoteRecordId <= 0
                        || task.VerificationVersion < BackupRequestAuthentication.CurrentVersion
                        || string.IsNullOrWhiteSpace(task.VerificationReceipt))
                    {
                        continue;
                    }
                    string path = ResolveManagedRecordingPath(task);
                    if (path.Length == 0 || !File.Exists(path))
                        continue;
                    if (!_db.IsLocalVideoFileFullyVerifiedForCacheDeletion(path))
                        continue;
                    if (!verifiedTasksByPath.TryGetValue(
                            path,
                            out List<RecordingTransferTask>? tasks))
                    {
                        tasks = [];
                        verifiedTasksByPath[path] = tasks;
                    }
                    tasks.Add(task);
                }

                foreach ((string path, List<RecordingTransferTask> tasks) in verifiedTasksByPath)
                {
                    long size;
                    try { size = new FileInfo(path).Length; }
                    catch { continue; }
                    RecordingTransferTask representative = tasks
                        .OrderBy(task => task.CreatedAt)
                        .ThenBy(task => task.Id)
                        .First();
                    cleanupGroupsById[representative.Id] = tasks;
                    verifiedItems.Add(new RecordingCacheCleanupItem(
                        representative.Id,
                        representative.CreatedAt,
                        size));
                }

                RecordingCacheCleanupPlan plan =
                    RecordingWorkstationCachePolicy.CreateCleanupPlan(
                        snapshot,
                        requiredHeadroomBytes,
                        verifiedItems);
                int cleanedCount = 0;
                long cleanedBytes = 0;
                foreach (long taskId in plan.ItemIds)
                {
                    if (!cleanupGroupsById.TryGetValue(
                            taskId,
                            out IReadOnlyList<RecordingTransferTask>? tasks)
                        || tasks.Count == 0)
                    {
                        continue;
                    }
                    string path = ResolveManagedRecordingPath(tasks[0]);
                    if (path.Length == 0)
                        continue;
                    long size = 0;
                    using (VideoLifecycleCoordinator.EnterAsync(
                               tasks[0].LocalVideoRecordId,
                               CancellationToken.None).GetAwaiter().GetResult())
                    {
                        // 候选来自锁外快照。进入录像生命周期锁后重新读取并确认，
                        // 防止归档、替换或恢复任务在远端校验期间改变文件所有权。
                        VideoRecord? current = _db.GetVideoById(tasks[0].LocalVideoRecordId);
                        if (current == null
                            || current.IsDeleted
                            || !string.Equals(
                                Path.GetFullPath(current.FilePath ?? ""),
                                path,
                                StringComparison.OrdinalIgnoreCase)
                            || !_db.IsLocalVideoFileFullyVerifiedForCacheDeletion(path))
                        {
                            continue;
                        }

                        if (File.Exists(path))
                        {
                            size = new FileInfo(path).Length;
                            RecordingTransferService? transferService = _recordingTransferService;
                            bool remotelyVerified = transferService != null;
                            foreach (RecordingTransferTask task in tasks)
                            {
                                if (!remotelyVerified)
                                    break;
                                remotelyVerified = transferService!
                                    .VerifyRemoteRecordForCleanupAsync(task, size)
                                    .GetAwaiter()
                                    .GetResult();
                            }
                            if (!remotelyVerified)
                            {
                                RuntimeLog.Warn(
                                    "RecordingTransfer",
                                    $"Cache retained because fresh host verification was unavailable records={tasks.Count}");
                                continue;
                            }
                            File.Delete(path);
                        }
                    }
                    foreach (RecordingTransferTask task in tasks)
                    {
                        if (task.RemoteVideoRecordId is not long remoteRecordId
                            || remoteRecordId <= 0)
                        {
                            continue;
                        }
                        _db.MarkVideoCacheDeleted(task.LocalVideoRecordId, remoteRecordId);
                        _recordingTransferStore.MarkCacheDeleted(task.Id, DateTime.UtcNow);
                    }
                    cleanedCount++;
                    cleanedBytes += size;
                    RuntimeLog.Info(
                        "RecordingTransfer",
                        $"Verified cache removed records={tasks.Count}, bytes={size}");
                }

                if (cleanedCount > 0)
                {
                    drive = new DriveInfo(root);
                    cacheBytes = GetRecordingCacheInventoryBytes(cachePath);
                    reserveBytes =
                        StorageSpacePolicy.GetEffectiveReserveBytes(location, drive);
                    snapshot = RecordingWorkstationCachePolicy.CalculateSpace(
                        cacheBytes,
                        configuredLimitBytes,
                        drive.AvailableFreeSpace,
                        reserveBytes);
                    RuntimeLog.Info(
                        "RecordingTransfer",
                        $"Cache cleanup completed count={cleanedCount}, bytes={cleanedBytes}, remaining={snapshot.CacheBytes}");
                }

                bool canFit = snapshot.RemainingBytes >= requiredHeadroomBytes;
                bool warning = !canFit
                    || snapshot.UsagePercent
                    >= RecordingWorkstationCachePolicy.WarningWatermark * 100
                    || snapshot.RemainingBytes
                    < RecordingWorkstationCachePolicy.RecordingAndPackagingHeadroomBytes;
                PublishRecordingCacheStatus(snapshot, canFit, warning);
                _recordingCacheCanFit = canFit;
                _recordingCacheSnapshotAvailable = true;
                _recordingCacheSnapshotAt = DateTime.Now;
                return new RecordingCacheMaintenanceResult(
                    true,
                    canFit,
                    warning,
                    cleanedCount,
                    snapshot,
                    "");
            }
            catch (Exception ex)
            {
                _recordingCacheCanFit = false;
                _recordingCacheSnapshotAvailable = false;
                _recordingCacheSnapshotAt = DateTime.Now;
                RuntimeLog.Warn(
                    "RecordingTransfer",
                    $"Cache maintenance failed: {ex.Message}");
                PublishRecordingCacheUnavailable(ex.Message);
                return RecordingCacheMaintenanceResult.Unavailable(ex.Message);
            }
        }
    }

    private void ReconcileRecordingCacheInventory(string cachePath)
    {
        var files = new List<StorageVideoFile>();
        foreach (FileInfo file in EnumerateVideoFiles(cachePath))
        {
            try
            {
                files.Add(new StorageVideoFile
                {
                    FilePath = file.FullName,
                    FileSizeBytes = file.Length,
                    StartTime = file.LastWriteTimeUtc
                });
            }
            catch
            {
                // 文件可能正在转换或被已验证缓存清理移除，下一次校准会重新确认。
            }
        }

        _db!.ReplaceLocalVideoFileInventory(cachePath, files);
        _recordingCacheInventoryRoot = cachePath;
        _recordingCacheInventoryInitialized = true;
        RuntimeLog.Info(
            "RecordingTransfer",
            $"Cache inventory reconciled files={files.Count}, root={cachePath}");
    }

    private long GetRecordingCacheInventoryBytes(string cachePath)
    {
        long totalBytes = _db!.GetLocalVideoFileInventory()
            .Where(file => IsPathInside(file.FilePath, cachePath))
            .Sum(file => Math.Max(0, file.FileSizeBytes));

        string? currentVideoPath = _currentVideoFilePath;
        if (!string.IsNullOrWhiteSpace(currentVideoPath)
            && IsPathInside(Path.GetFullPath(currentVideoPath), cachePath))
        {
            totalBytes += GetExistingFileSize(currentVideoPath);
        }

        string? currentAudioPath = _currentAudioFilePath;
        if (!string.IsNullOrWhiteSpace(currentAudioPath)
            && IsPathInside(Path.GetFullPath(currentAudioPath), cachePath))
        {
            totalBytes += GetExistingFileSize(currentAudioPath);
        }

        return Math.Max(0, totalBytes);
    }

    private void PublishRecordingCacheStatus(
        RecordingCacheSpaceSnapshot snapshot,
        bool canFit,
        bool warning)
    {
        void Update()
        {
            if (_isDisposed) return;
            double cachedGb =
                snapshot.CacheBytes / (double)StorageSpacePolicy.BytesPerGiB;
            RecordingCacheUsageText =
                $"已缓存 {cachedGb:F1} GB / 上限 {Config.RecordingCacheMaxGB} GB";
            RecordingCacheUsagePercent = snapshot.UsagePercent;
            IsRecordingCacheWarning = warning;
            RecordingCacheStatusText = canFit
                ? warning
                    ? "本地缓存接近安全上限，已上传录像会自动清理"
                    : "本地缓存空间充足"
                : "可清理的已上传录像不足，下一段录像将暂停";
            DiskUsagePercent = snapshot.UsagePercent;
            DiskUsageText = RecordingCacheUsageText;
            if (canFit)
            {
                _recordingCacheBlockedDialogShown = false;
                Interlocked.Exchange(ref _recordingCacheEmergencyStopRequested, 0);
            }
        }

        if (Application.Current?.Dispatcher is { } dispatcher
            && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(Update);
        }
        else
        {
            Update();
        }
    }

    private void PublishRecordingCacheUnavailable(string error)
    {
        void Update()
        {
            if (_isDisposed) return;
            IsRecordingCacheWarning = true;
            RecordingCacheStatusText = $"本地缓存不可用：{error}";
        }

        if (Application.Current?.Dispatcher is { } dispatcher
            && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(Update);
        }
        else
        {
            Update();
        }
    }

    internal static IReadOnlyList<RecordingTransferTask> SelectRecordingCacheCleanupCandidates(
        IReadOnlyList<RecordingTransferTask> uploaded,
        string policy,
        int keepDays,
        int maxGb,
        DateTime now,
        Func<RecordingTransferTask, string> pathResolver,
        Func<string, long>? fileLengthProvider = null)
    {
        if (string.Equals(policy, "DeleteImmediately", StringComparison.Ordinal))
            return uploaded.ToArray();
        if (!string.Equals(policy, "KeepWithinSize", StringComparison.Ordinal))
        {
            DateTime cutoff = now.AddDays(-Math.Max(1, keepDays));
            return uploaded
                .Where(task => task.CreatedAt.ToLocalTime() < cutoff)
                .ToArray();
        }

        long maxBytes = Math.Max(1L, maxGb) * 1024 * 1024 * 1024;
        var existing = uploaded
            .Select(task => new
            {
                Task = task,
                Path = pathResolver(task)
            })
            .Where(item => item.Path.Length > 0
                && (fileLengthProvider != null || File.Exists(item.Path)))
            .Select(item => new
            {
                item.Task,
                Size = fileLengthProvider?.Invoke(item.Path) ?? new FileInfo(item.Path).Length
            })
            .OrderBy(item => item.Task.CreatedAt)
            .ToList();
        long total = existing.Sum(item => item.Size);
        var candidates = new List<RecordingTransferTask>();
        foreach (var item in existing)
        {
            if (total <= maxBytes) break;
            total -= item.Size;
            candidates.Add(item.Task);
        }
        return candidates;
    }

    private string ResolveManagedRecordingPath(RecordingTransferTask task)
    {
        string[] candidates =
        [
            _db?.GetVideoById(task.LocalVideoRecordId)?.FilePath ?? "",
            task.LocalFilePath
        ];
        foreach (string candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(candidate); }
            catch { continue; }
            IEnumerable<StorageLocation> managedLocations = IsRecordingWorkstation
                ? RecordingWorkstationCachePolicy.GetConfiguredLocation(Config) is { } cacheLocation
                    ? [cacheLocation]
                    : []
                : Config.StorageLocations;
            if (managedLocations.Any(location => IsPathInside(fullPath, location.Path)))
                return fullPath;
        }
        return "";
    }

    private static bool IsPathInside(string filePath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return false;
        string root;
        try { root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; }
        catch { return false; }
        return filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private void DisposeRecordingTransfers()
    {
        if (IsRecordingWorkstation
            && !string.IsNullOrWhiteSpace(Config.LastKnownHostAddress)
            && !string.IsNullOrWhiteSpace(Config.NodeId))
        {
            _ = WorkstationNetwork.SendRecordingWorkstationHeartbeatAsync(
                Config.LastKnownHostAddress,
                Config.NodeId,
                Config.NodeName,
                Config.WebServerPort,
                connected: false,
                nicknameCustomized: Config.NodeNameCustomized);
        }
        if (_recordingTransferService != null)
            _recordingTransferService.ProgressChanged -= OnRecordingTransferProgressChanged;
        try { _recordingTransferService?.Dispose(); } catch { }
        _recordingTransferService = null;
        _recordingTransferStore = null;
    }
}

internal readonly record struct RecordingCacheMaintenanceResult(
    bool IsAvailable,
    bool CanFitRequiredHeadroom,
    bool IsWarning,
    int CleanedCount,
    RecordingCacheSpaceSnapshot Snapshot,
    string Error)
{
    public static RecordingCacheMaintenanceResult Unavailable(string error) =>
        new(
            false,
            false,
            true,
            0,
            default,
            error);
}
