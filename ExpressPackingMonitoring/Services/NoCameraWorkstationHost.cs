using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Logging;
using System.IO;

namespace ExpressPackingMonitoring.Services;

internal sealed class NoCameraWorkstationHost : IDisposable
{
    private AppConfig _config;
    private readonly string _databasePath;
    private readonly string _stateDirectory;
    private readonly Action<int> _repairLanAccess;
    private VideoDatabase? _database;
    private WebServer? _server;
    private ArchiveService? _archiveService;
    private bool _disposed;
    private bool _archiveTargetUnavailable;
    private string _archiveUnavailableRoot = "";

    public NoCameraWorkstationHost(
        AppConfig config,
        string? databasePath = null,
        string? stateDirectory = null,
        Action<int>? repairLanAccess = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _databasePath = databasePath ?? AppPaths.VideoDatabasePath;
        _stateDirectory = stateDirectory ?? AppPaths.MobileBackupStateDir;
        _repairLanAccess = repairLanAccess ?? WebServer.RepairLanAccess;
    }

    public bool IsRunning => _server != null;
    public bool HasDatabase => _database != null;
    public bool HasActiveMobileBackups => _server?.HasActiveMobileBackups == true;
    public bool IsLanAvailable { get; private set; }
    public string StoragePath { get; private set; } = "";
    public string LocalPlaybackUrl { get; private set; } = "";

    /// <summary>备份主机窗口的“录像备份”卡片状态：是否可见 + 短状态/详情。</summary>
    internal (bool IsVisible, ArchiveBackupCardState State) GetArchiveBackupCardState()
    {
        bool visible = ArchiveBackupCardModel.ShouldShowArchiveBackupCard(
            _config,
            isRecordingWorkstation: false);
        if (!visible || _database == null)
            return (false, new ArchiveBackupCardState("", ""));

        ArchiveQueueSummary summary = _database.GetArchiveQueueSummary();
        ArchiveBackupCardState state = ArchiveBackupCardModel.BuildArchiveBackupCardState(
            summary,
            ArchiveBackupCardModel.ResolveCurrentArchiveTarget(_config),
            _archiveTargetUnavailable,
            _archiveUnavailableRoot,
            _archiveService?.CurrentWorkerSnapshot ?? default);
        return (true, state);
    }
    public string LanAccessUrl { get; private set; } = "";
    public string ErrorMessage { get; private set; } = "";
    public VideoDatabase Database =>
        _database ?? throw new InvalidOperationException("录像数据库尚未打开");
    public event Action<MobileAppUpdateAvailableInfo>? MobileAppUpdateAvailable;
    public event Action? MobileBackupStatusChanged;
    public event Action? ArchiveBackupStatusChanged;
    public event Func<BackupDeviceEnrollmentRequest, BackupDeviceEnrollmentApprovalDecision>? BackupDeviceEnrollmentRequested;
    public event Action<bool>? MobileBackupActivityChanged;

    public Task WaitForMobileBackupsAsync(CancellationToken cancellationToken = default) =>
        _server?.WaitForMobileBackupsAsync(cancellationToken) ?? Task.CompletedTask;

    public IReadOnlyList<RecordingDeviceInfo> GetRecordingDevices(bool includeKnown = false)
    {
        if (_server == null)
            return Array.Empty<RecordingDeviceInfo>();
        string authority = "";
        string accessUrl = IsLanAvailable ? LanAccessUrl : LocalPlaybackUrl;
        if (Uri.TryCreate(accessUrl, UriKind.Absolute, out Uri? uri))
            authority = uri.Authority;
        return _server.GetRecordingDevices(authority, includeKnown);
    }

    public int GetConnectedMobileCount()
    {
        if (_server == null)
            return 0;
        return _server.GetConnectedClients()
            .Where(client => string.Equals(client.ClientType, "mobile-app", StringComparison.Ordinal)
                || string.Equals(client.DeviceType, "mobile", StringComparison.Ordinal))
            .Select(client => string.IsNullOrWhiteSpace(client.NodeId)
                ? client.RemoteAddress
                : client.NodeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    public void UpdateConfig(AppConfig config)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task StartAsync(bool requestLanAccess = true, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopServer();
        ErrorMessage = "";
        IsLanAvailable = false;
        LanAccessUrl = "";

        try
        {
            StoragePath = StorageLocationResolver.Resolve(_config, allowDefaultFallback: false);
            _database ??= new VideoDatabase(_databasePath);
            DisposeArchiveService();
            _archiveTargetUnavailable = false;
            _archiveUnavailableRoot = "";
            _archiveService = new ArchiveService(
                _database,
                new NasArchiveProvider(),
                archiveTargetResolver: () =>
                    StorageLocationResolver.GetOrderedBackupLocations(_config));
            _archiveService.BackupTargetAvailabilityChanged +=
                OnArchiveTargetAvailabilityChanged;
            _archiveService.WorkerStateChanged += OnArchiveWorkerStateChanged;
            _archiveService.ArchiveQueueChanged += OnArchiveQueueChanged;
            LocalPlaybackUrl = MobileConnectionService.BuildAccessUrl(
                $"127.0.0.1:{_config.WebServerPort}",
                _config.RequireWebAccessKey,
                _config.WebAccessKey);

            WebServer lanServer = CreateServer("+");
            try
            {
                lanServer.Start(allowAccessSetup: requestLanAccess);
                _server = lanServer;
                string address = await WorkstationNetwork.GetVerifiedLocalAccessAddressAsync(
                    _config.WebServerPort,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                bool isLoopback = address.StartsWith("127.", StringComparison.Ordinal);
                bool addressResponds = !isLoopback && await WorkstationNetwork.CanConnectAsync(address);
                IsLanAvailable = addressResponds && WebServer.HasExpectedFirewallRule(_config.WebServerPort);
                if (IsLanAvailable)
                {
                    LanAccessUrl = MobileConnectionService.BuildAccessUrl(
                        address,
                        _config.RequireWebAccessKey,
                        _config.WebAccessKey);
                }
                else
                {
                    ErrorMessage = "局域网访问尚未配置，当前仅本机可用";
                }
            }
            catch (Exception lanException)
            {
                lanServer.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (WebServer.IsListenerConflict(lanException))
                {
                    throw new InvalidOperationException(
                        $"Web 服务端口 {_config.WebServerPort} 已被其他程序或尚未退出的旧版本占用，请关闭旧程序后重试",
                        lanException);
                }

                RuntimeLog.Warn("NoCamera", $"LAN listener unavailable, fallback loopback: {lanException.Message}");
                WebServer localServer = CreateServer("127.0.0.1");
                try
                {
                    localServer.Start();
                    _server = localServer;
                    ErrorMessage = WebServer.GetLanAccessFailureUserMessage(
                        repairAttempted: false,
                        repairButtonAvailable: true);
                }
                catch (Exception localException)
                {
                    localServer.Dispose();
                    throw new InvalidOperationException(
                        $"Web 服务启动失败。局域网监听错误：{lanException.Message} 本机监听错误：{localException.Message}",
                        localException);
                }
            }
        }
        catch (Exception ex)
        {
            StopServer();
            DisposeArchiveService();
            ErrorMessage = GetFriendlyError(ex);
            RuntimeLog.Error("NoCamera", "No-camera workstation startup failed", ex);
            throw new InvalidOperationException(ErrorMessage, ex);
        }
    }

    public async Task<bool> RepairLanAccessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            RuntimeLog.Info("NoCamera", $"Repairing LAN access port={_config.WebServerPort}");
            await Task.Run(
                () => _repairLanAccess(_config.WebServerPort),
                cancellationToken);
            RuntimeLog.Info("NoCamera", $"LAN access permissions repaired port={_config.WebServerPort}");
        }
        catch (Exception ex)
        {
            ErrorMessage = WebServer.GetLanAccessFailureUserMessage(repairAttempted: true);
            RuntimeLog.Error("NoCamera", "LAN access permission repair failed", ex);
            return false;
        }

        try
        {
            // 权限已单独修复；后续启动不应再被存储空间等无关前置条件阻止权限修复。
            await StartAsync(requestLanAccess: false, cancellationToken);
            return IsLanAvailable;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"局域网权限已修复，但设备备份服务无法启动：{ErrorMessage}";
            RuntimeLog.Warn("NoCamera", $"LAN permissions repaired but service restart failed: {ex.Message}");
            return false;
        }
    }

    private WebServer CreateServer(string listenerHost)
    {
        var server = new WebServer(
            _database!,
            _config.WebServerPort,
            _config.TranscodeCacheMaxMB,
            requireAccessKey: _config.RequireWebAccessKey,
            accessKey: _config.WebAccessKey,
            listenerHost: listenerHost,
            mobileConnectionUrlProvider: () => LanAccessUrl,
            mobileBackupComputerId: _config.MobileBackupComputerId,
            mobileBackupComputerName: Environment.MachineName,
            mobileBackupStateDirectory: _stateDirectory,
            mobileBackupRecordingRootResolver: () => StorageLocationResolver.Resolve(_config, allowDefaultFallback: false),
            mobileBackupArchiveTargetResolver: () =>
            {
                RecordingStoragePlan plan = StorageLocationResolver.ResolveRecordingPlan(
                    _config,
                    allowDefaultFallback: false);
                return plan.RequiresNetworkArchive ? plan.ArchiveTarget : null;
            },
            mobileBackupArchivePendingCallback: () => _archiveService?.Wake(),
            nodeId: _config.NodeId,
            nodeName: _config.NodeName,
            deploymentPreset: DeploymentPresets.MobileBackupHost,
            backupDeviceEnrollmentApprover: request =>
                BackupDeviceEnrollmentRequested?.Invoke(request)
                ?? BackupDeviceEnrollmentApprovalDecision.Unavailable)
        {
            EnableOrderInfoLog = _config.EnableOrderInfoLog
        };
        server.MobileAppUpdateAvailable += update =>
        {
            try { MobileAppUpdateAvailable?.Invoke(update); } catch { }
        };
        server.ConnectedClientsChanged += _ =>
        {
            try { MobileBackupStatusChanged?.Invoke(); } catch { }
        };
        server.MobileBackupCompleted += (_, _) =>
        {
            try { MobileBackupStatusChanged?.Invoke(); } catch { }
        };
        server.MobileBackupActivityChanged += hasActive =>
        {
            try { MobileBackupActivityChanged?.Invoke(hasActive); } catch { }
        };
        return server;
    }

    private void StopServer()
    {
        try { _server?.Dispose(); } catch { }
        _server = null;
        IsLanAvailable = false;
    }

    private void OnArchiveTargetAvailabilityChanged(bool available, string root)
    {
        _archiveTargetUnavailable = !available;
        _archiveUnavailableRoot = available ? "" : root;
        NotifyArchiveBackupStatusChanged();
    }

    private void OnArchiveWorkerStateChanged(ArchiveWorkerSnapshot _) =>
        NotifyArchiveBackupStatusChanged();

    private void OnArchiveQueueChanged() => NotifyArchiveBackupStatusChanged();

    private void NotifyArchiveBackupStatusChanged()
    {
        try { ArchiveBackupStatusChanged?.Invoke(); } catch { }
    }

    private void DisposeArchiveService()
    {
        if (_archiveService == null)
            return;
        _archiveService.BackupTargetAvailabilityChanged -=
            OnArchiveTargetAvailabilityChanged;
        _archiveService.WorkerStateChanged -= OnArchiveWorkerStateChanged;
        _archiveService.ArchiveQueueChanged -= OnArchiveQueueChanged;
        try { _archiveService.Dispose(); } catch { }
        _archiveService = null;
    }

    private static string GetFriendlyError(Exception exception)
    {
        Exception root = exception;
        while (root.InnerException != null)
            root = root.InnerException;

        if (root is Microsoft.Data.Sqlite.SqliteException)
            return $"录像数据库无法打开：{root.Message}";
        if (root is IOException or UnauthorizedAccessException)
            return $"录像存储不可用：{root.Message}";
        return exception.Message;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopServer();
        DisposeArchiveService();
        try { _database?.Dispose(); } catch { }
        _database = null;
    }
}
