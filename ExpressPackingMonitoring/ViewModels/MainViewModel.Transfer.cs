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

public partial class MainViewModel
{
    private RecordingTransferService? _recordingTransferService;
    private RecordingTransferQueueStore? _recordingTransferStore;
    private string _recordingTransferStatusText = "";
    private int _pendingRecordingTransferCount;
    private double _recordingTransferProgress;
    private string _lastRecordingTransferError = "";
    private string _boundHostOnlineStatusText = "正在检查主机状态";
    private DateTime _lastRecordingWorkstationHeartbeatAt = DateTime.MinValue;
    private int _recordingWorkstationHeartbeatInProgress;

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
                ? "尚未绑定"
                : "已绑定主机";
        }
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
                () => Config);
            _recordingTransferService.ProgressChanged += OnRecordingTransferProgressChanged;
            _recordingTransferService.Start();
            RefreshRecordingTransferSummary();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("RecordingTransfer", "Transfer service startup failed", ex);
            RecordingTransferStatusText = "上传服务启动失败，录像仍保留在本机";
            LastRecordingTransferError = ex.Message;
        }
    }

    private void OpenBoundHost()
    {
        if (!IsRecordingWorkstation || string.IsNullOrWhiteSpace(Config.LastKnownHostAddress))
        {
            ShowToast("尚未绑定保存主机");
            return;
        }
        string url = MobileConnectionService.BuildAccessUrl(
            Config.LastKnownHostAddress,
            requireAccessKey: true,
            Config.LastKnownHostAccessKey);
        if (!WorkstationNetwork.TryOpenUrl(url, out string error))
            AppDialog.ShowMessage(null, error, "打开主机录像失败", AppDialogSeverity.Error);
    }

    private void RetryRecordingTransfers()
    {
        _recordingTransferService?.EnqueueCompletedRecordings();
        _recordingTransferService?.RetryNow();
        RecordingTransferStatusText = "正在重新连接保存主机";
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

        _lastRecordingWorkstationHeartbeatAt = now;
        _ = SendRecordingWorkstationHeartbeatAsync();
    }

    private async Task SendRecordingWorkstationHeartbeatAsync()
    {
        try
        {
            bool online = await WorkstationNetwork.SendRecordingWorkstationHeartbeatAsync(
                Config.LastKnownHostAddress,
                Config.NodeId,
                Config.NodeName,
                Config.WebServerPort,
                connected: true);
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_isDisposed) return;
                BoundHostOnlineStatusText = online ? "主机在线" : "主机离线";
                if (!online && PendingRecordingTransferCount > 0)
                    RecordingTransferStatusText = "主机离线，录像已保存在本机，联网后自动上传";
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
        RecordingTransferSummary? summary = _recordingTransferStore?.GetSummary();
        if (!ShouldPromptRecordingWorkstationHostBinding(Config)
            && summary != null
            && summary.PendingCount + summary.UploadingCount + summary.FailedCount > 0)
        {
            AppDialog.ShowMessage(
                null,
                "仍有录像等待上传。为避免已有任务被静默改到另一台主机，请等待队列完成后再更换主机",
                "暂时无法更换主机",
                AppDialogSeverity.Warning);
            return;
        }

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
        ShouldManageBoundHostFromMainConnection(config)
            ? "管理保存主机"
            : "连接手机";

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
                RecordingTransferStatusText = "最近一段录像已保存到主机";
                RunRecordingCacheCleanup();
            }
            else if (progress.State == RecordingTransferStates.Failed)
            {
                RecordingTransferStatusText = "主机离线或上传失败，录像已保存在本机，联网后自动上传";
                LastRecordingTransferError = progress.Error;
            }
            else
            {
                RecordingTransferStatusText = $"正在上传 {RecordingTransferProgress:F0}%";
            }
            RefreshRecordingTransferSummary();
        });
    }

    private void RefreshRecordingTransferSummary()
    {
        RecordingTransferSummary? summary = _recordingTransferStore?.GetSummary();
        if (summary == null) return;
        PendingRecordingTransferCount =
            summary.PendingCount + summary.UploadingCount + summary.FailedCount;
        LastRecordingTransferError = summary.LastError;
        if (summary.UploadingCount == 0 && PendingRecordingTransferCount > 0)
            RecordingTransferStatusText = "录像等待上传，主机恢复后将自动继续";
        else if (PendingRecordingTransferCount == 0
                 && string.IsNullOrWhiteSpace(RecordingTransferStatusText))
            RecordingTransferStatusText = "录像上传队列为空";
    }

    private void RunRecordingCacheCleanup()
    {
        if (!IsRecordingWorkstation || _recordingTransferStore == null || _db == null)
            return;

        try
        {
            IReadOnlyList<RecordingTransferTask> uploaded =
                _recordingTransferStore.GetUploadedWithLocalCache();
            IReadOnlyList<RecordingTransferTask> candidates =
                SelectRecordingCacheCleanupCandidates(
                    uploaded,
                    Config.RecordingCachePolicy,
                    Config.RecordingCacheKeepDays,
                    Config.RecordingCacheMaxGB,
                    DateTime.Now,
                    ResolveManagedRecordingPath);

            foreach (RecordingTransferTask task in candidates.ToArray())
            {
                if (task.RemoteVideoRecordId is not long remoteRecordId || remoteRecordId <= 0)
                    continue;
                string path = ResolveManagedRecordingPath(task);
                if (string.IsNullOrEmpty(path))
                {
                    RuntimeLog.Warn(
                        "RecordingTransfer",
                        $"Cache cleanup skipped unmanaged path record={task.LocalVideoRecordId}");
                    continue;
                }

                if (File.Exists(path))
                    File.Delete(path);
                _db.MarkVideoCacheDeleted(task.LocalVideoRecordId, remoteRecordId);
                _recordingTransferStore.MarkCacheDeleted(task.Id, DateTime.UtcNow);
                RuntimeLog.Info(
                    "RecordingTransfer",
                    $"Uploaded cache removed localRecord={task.LocalVideoRecordId}");
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("RecordingTransfer", $"Cache cleanup failed: {ex.Message}");
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
            if (Config.StorageLocations.Any(location => IsPathInside(fullPath, location.Path)))
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
                connected: false);
        }
        if (_recordingTransferService != null)
            _recordingTransferService.ProgressChanged -= OnRecordingTransferProgressChanged;
        try { _recordingTransferService?.Dispose(); } catch { }
        _recordingTransferService = null;
        _recordingTransferStore = null;
    }
}
