#nullable disable
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Config;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ExpressPackingMonitoring.ViewModels;
using Microsoft.Win32;

namespace ExpressPackingMonitoring.Services
{
    internal sealed record OrderIntegrationDeviceStatus(
        string NodeId,
        string DisplayName,
        string DeviceType,
        bool Online,
        DateTimeOffset? LastActivityUtc,
        int ReceivedCount);

    /// <summary>
    /// 内嵌轻量 HTTP 服务器，供局域网客户端搜索、播放和下载视频。
    /// 基于 HttpListener，无需额外 NuGet 依赖。
    /// </summary>
    /// <summary>订单附加信息（从快递助手页面推送）</summary>
    public class OrderInfo
    {
        public string TrackingNumber { get; set; } = "";
        public string OrderId { get; set; } = "";
        public string BuyerMessage { get; set; } = "";
        public string SellerMemo { get; set; } = "";
        public string ProductInfo { get; set; } = "";
        /// <summary>该订单中所有商品的总件数。</summary>
        public int TotalItemCount { get; set; }
        /// <summary>同一快递单号聚合后的订单数量。</summary>
        public int MergedOrderCount { get; set; }
        /// <summary>第三方来源标识，仅用于审计和兼容处理。</summary>
        public string ProviderId { get; set; } = "";
        public bool HasRefund { get; set; }
        public bool IsPrintedRefund { get; set; }
        public string RefundStatus { get; set; } = "";
        public string RefundProductInfo { get; set; } = "";
        public DateTime PushTime { get; set; } = DateTime.Now;
        public bool IsTest { get; set; }
    }

    internal sealed record MobileAppDownloadInfo(
        string Version,
        string DownloadUrl,
        string QrCode,
        string IosDownloadUrl,
        string IosQrCode);

    public sealed class BackupDeviceEnrollmentRequest
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string DeviceKind { get; set; } = "mobile";
        // 仅供批准弹窗展示（macos/windows），不参与任何校验或服务端分支。
        public string Platform { get; set; } = "";
        public string RemoteAddress { get; set; } = "";
        public string ClientVersion { get; set; } = "";
        public int ClientBuildNumber { get; set; }
        public string BackupProtocol { get; set; } = "";
        public int EnrollmentVersion { get; set; }
        public int AuthVersion { get; set; }
    }

    public enum BackupDeviceEnrollmentApprovalDecision
    {
        Approved,
        Denied,
        Unavailable
    }

    public sealed class OrderLookupResult
    {
        public bool Responded { get; set; }
        public IReadOnlyList<OrderInfo> Orders { get; set; } = Array.Empty<OrderInfo>();
    }

    public sealed class WebServer : IDisposable
    {
        internal enum LanAccessFailureKind
        {
            Unknown,
            WindowsFirewallServiceUnavailable,
            BaseFilteringEngineUnavailable
        }

        internal sealed class LanAccessSetupException : InvalidOperationException
        {
            internal LanAccessSetupException(LanAccessFailureKind failureKind, string message, Exception innerException = null)
                : base(message, innerException)
            {
                FailureKind = failureKind;
            }

            internal LanAccessFailureKind FailureKind { get; }
        }

        internal sealed record WindowsServiceDiagnostic(
            string ServiceName,
            bool QuerySucceeded,
            bool IsRunning,
            bool IsDisabled,
            uint CurrentState,
            int? StartType);

        private sealed record DeviceVideoTicket(
            string DeviceId,
            string DeviceKind,
            long RecordId,
            DateTimeOffset ExpiresAt);
        private sealed record DeviceClipTaskGrant(
            string DeviceId,
            long RecordId,
            DateTimeOffset ExpiresAt);
        private sealed record DeviceClipAssetTicket(
            string DeviceId,
            string FileName,
            string AssetKind,
            DateTimeOffset ExpiresAt);
        private sealed class PendingOrderLookup
        {
            public string RequestId { get; init; } = "";
            public IReadOnlyList<string> TrackingNumbers { get; init; } = Array.Empty<string>();
            public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
            public TaskCompletionSource<OrderLookupResult> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Claimed;
        }

        private sealed class OrderLookupResponse
        {
            public string RequestId { get; set; } = "";
            public bool Success { get; set; }
            public List<OrderInfo> Orders { get; set; }
            public string Error { get; set; } = "";
        }

        private sealed class OrderBroadcastRequest
        {
            public List<OrderInfo> Orders { get; set; } = [];
            public List<string> TargetNodeIds { get; set; } = [];
        }

        private sealed class OrderExtensionRequest
        {
            public string ProviderId { get; set; } = "";
            public string ApiVersion { get; set; } = "";
            public List<OrderInfo> Orders { get; set; } = [];
        }

        private sealed class ExtensionHeartbeatRequest
        {
            public string Version { get; set; } = "";
            public string[] Capabilities { get; set; } = [];
            public DateTimeOffset? LastSuccessfulActivityAt { get; set; }
            public int DataCount { get; set; }
        }

        private sealed class RecordingExtensionDataRequest
        {
            public string Namespace { get; set; } = "";
            public string ProviderId { get; set; } = "";
            public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);
        }

        private sealed class ExtensionEnrollmentHttpRequest
        {
            public string RequestId { get; set; } = "";
            public string RequestSecret { get; set; } = "";
            public string ExtensionInstanceId { get; set; } = "";
            public string ProviderId { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string Version { get; set; } = "";
            public string Source { get; set; } = "";
            public List<string> RequestedPermissions { get; set; } = [];
            public List<string> RequestedCapabilities { get; set; } = [];
        }

        private sealed class ExtensionScanTaskAckRequest
        {
            public string TaskId { get; set; } = "";
        }

        private const int MaxJsonBodyBytes = 64 * 1024;
        private const int MaxOrderInfoBodyBytes = 1024 * 1024;
        internal const int MaxOrderInfoItems = 200;
        private const int MaxOrderBroadcastTargets = 8;
        private HttpListener _listener;
        private readonly VideoDatabase _db;
        private readonly Func<bool> _isRecordingProvider;
        private readonly Func<string> _currentRecordingFileProvider;
        private readonly Func<VideoRecord, MkvConversionResult> _mkvConverter;
        private readonly VideoClipService _clipService;
        private readonly bool _requireAccessKey;
        private readonly string _accessKey;
        private readonly Func<string> _mobileConnectionUrlProvider;
        private readonly MobileBackupService _mobileBackupService;
        private readonly BackupPairingTokenService _backupPairingTokens;
        private readonly MobileOrderReceiverRegistry _mobileOrderReceivers;
        private readonly RecordingComputerNicknameRegistry _recordingComputerNicknames;
        private readonly OrderIntegrationActivityRegistry _orderIntegrationActivities;
        private readonly UserscriptConfigRevisionStore _userscriptConfigRevision;
        private readonly UserscriptCatalog _userscriptCatalog;
        private readonly string _extensionStateDirectory;
        private readonly bool _extensionApiEnabled;
        private readonly object _extensionEnrollmentInitializationLock = new();
        private ExtensionAuthorizationStore _extensionAuthorizations;
        private ExtensionEnrollmentService _extensionEnrollment;
        private ExtensionRequestAuthenticator _extensionRequestAuthenticator;
        private ExtensionScanTaskBroker _extensionScanTaskBroker;
        private ExtensionScanResultSubmissionCoordinator _extensionScanResultCoordinator;
        private Action _extensionResultAvailable;
        private readonly ConnectedClientRegistry _connectedClients;
        private readonly MobileAppUpdatePolicyProvider _mobileAppUpdatePolicy =
            MobileAppUpdatePolicyProvider.Shared;
        private Timer _mobileAppUpdateRefreshTimer;
        private readonly ConcurrentDictionary<string, byte> _notifiedMobileAppUpdates = new();
        private readonly ConcurrentDictionary<string, DeviceVideoTicket> _deviceVideoTickets = new();
        private readonly ConcurrentDictionary<string, DeviceClipTaskGrant> _deviceClipTasks = new();
        private readonly ConcurrentDictionary<string, DeviceClipAssetTicket> _deviceClipAssetTickets = new();
        private readonly object _deviceGrantCleanupLock = new();
        private const int DeviceVideoTicketLimit = 2048;
        private const int DeviceVideoTicketLowWater = 1536;
        private const int DeviceClipTaskLimit = 256;
        private const int DeviceClipTaskLowWater = 192;
        private const int DeviceClipAssetTicketLimit = 2048;
        private const int DeviceClipAssetTicketLowWater = 1536;
        private readonly string _mobileBackupComputerId;
        private readonly string _mobileBackupComputerName;
        private readonly string _nodeId;
        private readonly string _nodeName;
        private readonly string _deploymentPreset;
        private readonly bool _orderReceiverOnly;
        private sealed record BackupDeviceEnrollmentOperation(
            BackupDeviceEnrollmentApprovalDecision Decision,
            BackupDeviceEnrollment Enrollment = null);

        private readonly Func<BackupDeviceEnrollmentRequest, BackupDeviceEnrollmentApprovalDecision> _backupDeviceEnrollmentApprover;
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _requestSlots = new(32, 32);
        private readonly ManualResetEventSlim _requestsIdle = new(initialState: true);
        private readonly LanRequestRateLimiter _requestRateLimiter = new();
        private readonly SemaphoreSlim _transcodeSlot = new(1, 1);
        private readonly FfmpegWorkLimiter _ffmpegWorkLimiter = new();
        private readonly ShortLivedSnapshotCache<byte[]> _storageOverviewCache = new(TimeSpan.FromSeconds(10));
        private Task _listenTask;
        private Task _videoCodecBackfillTask;
        private UdpDiscoveryResponder _udpDiscoveryResponder;
        private int _activeRequests;
        private int _serverResourcesDisposed;
        private bool _disposed;
        internal TimeSpan ShutdownWaitTimeout { get; set; } = TimeSpan.FromSeconds(2);
        internal bool ServerResourcesDisposedForTesting => Volatile.Read(ref _serverResourcesDisposed) != 0;
        private static readonly string _logPath = AppPaths.WebDebugLogPath;
        private static readonly string _transCacheDir = AppPaths.TranscodeCacheDir;
        private long _transCacheMaxBytes = 1024L * 1024 * 1024; // 默认 1GB，可config覆盖

        // SQLite 是订单信息唯一持久化来源；此字典仅用于运行时快速查询。
        private readonly Dictionary<string, OrderInfo> _orderInfoCache = new();
        private readonly object _orderInfoLock = new();
        private readonly ConcurrentDictionary<string, PendingOrderLookup> _pendingOrderLookups = new();
        private readonly ConcurrentDictionary<HttpListenerContext, byte[]> _authenticatedRequestBodies = new();
        private readonly ConcurrentDictionary<HttpListenerContext, ExtensionAuthorizationContext> _authenticatedExtensionContexts = new();
        private readonly ConcurrentDictionary<HttpListenerContext, string> _authenticatedDeviceKeys = new();
        private readonly ConcurrentDictionary<HttpListenerContext, string> _authenticatedDeviceIds = new();
        private readonly ConcurrentDictionary<HttpListenerContext, string> _authenticatedDeviceKinds = new();
        private readonly ConcurrentDictionary<string, long> _backupRequestNonces = new(StringComparer.Ordinal);
        private readonly object _backupEnrollmentApprovalLock = new();
        private static readonly TimeSpan BackupEnrollmentRetryReuseWindow = TimeSpan.FromSeconds(10);
        private string _activeBackupEnrollmentKey;
        private Lazy<BackupDeviceEnrollmentOperation> _activeBackupEnrollment;
        private string _recentBackupEnrollmentKey;
        private BackupDeviceEnrollmentOperation _recentBackupEnrollment;
        private DateTimeOffset _recentBackupEnrollmentExpiresAtUtc;
        private readonly SemaphoreSlim _orderLookupSignal = new(0);
        private int _activeOrderLookupPolls;
        private long _lastOrderLookupPollUtcTicks;

        /// <summary>收到油猴脚本推送的订单信息时触发，参数为本次推送的所有订单</summary>
        public event Action<List<OrderInfo>> OrderInfoReceived;
        internal event Action<string, IReadOnlyList<RecordingExtensionField>> RecordingExtensionDataChanged;
        internal event Action<IReadOnlyList<ConnectedClientInfo>> ConnectedClientsChanged;
        internal event Action<MobileAppUpdateAvailableInfo> MobileAppUpdateAvailable;
        internal event Action<string, string> MobileBackupCompleted;
        internal event Action<bool> MobileBackupActivityChanged;

        public int Port { get; }
        public bool EnableOrderInfoLog { get; set; }
        internal bool HasActiveMobileBackups => _mobileBackupService.HasActiveUploads;
        internal Task WaitForMobileBackupsAsync(CancellationToken cancellationToken = default) =>
            _mobileBackupService.WaitForIdleAsync(cancellationToken);

        private void Log(string msg)
        {
            if (!EnableOrderInfoLog) return;
            WriteLog(msg);
        }

        private static void WriteLog(string msg)
        {
            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}";
                lock (_logPath)
                    File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch { }
        }

        public WebServer(
            VideoDatabase db,
            int port = 5280,
            int transCacheMaxMB = 1024,
            Func<bool> isRecordingProvider = null,
            Func<VideoRecord, MkvConversionResult> mkvConverter = null,
            Func<string> currentRecordingFileProvider = null,
            bool requireAccessKey = false,
            string accessKey = null,
            string listenerHost = "+",
            Func<string> mobileConnectionUrlProvider = null,
            string mobileBackupComputerId = null,
            string mobileBackupComputerName = null,
            string mobileBackupStateDirectory = null,
            Func<string> mobileBackupRecordingRootResolver = null,
            Func<string> mobileBackupArchiveTargetResolver = null,
            Action mobileBackupArchivePendingCallback = null,
            string nodeId = null,
            string nodeName = null,
            string deploymentPreset = null,
            bool orderReceiverOnly = false,
            bool nodeNameCustomized = false,
            Func<BackupDeviceEnrollmentRequest, BackupDeviceEnrollmentApprovalDecision> backupDeviceEnrollmentApprover = null,
            bool extensionApiEnabled = false)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _isRecordingProvider = isRecordingProvider ?? (() => false);
            _currentRecordingFileProvider = currentRecordingFileProvider ?? (() => null);
            _mkvConverter = mkvConverter;
            _requireAccessKey = requireAccessKey;
            _accessKey = accessKey?.Trim() ?? "";
            _mobileConnectionUrlProvider = mobileConnectionUrlProvider ?? (() => "");
            _mobileBackupComputerId = mobileBackupComputerId?.Trim() ?? "";
            _mobileBackupComputerName = string.IsNullOrWhiteSpace(mobileBackupComputerName)
                ? Environment.MachineName
                : mobileBackupComputerName.Trim();
            _nodeId = Guid.TryParse(nodeId, out Guid configuredNodeId) && configuredNodeId != Guid.Empty
                ? configuredNodeId.ToString("D")
                : Guid.TryParse(_mobileBackupComputerId, out Guid mobileComputerId) && mobileComputerId != Guid.Empty
                    ? mobileComputerId.ToString("D")
                    : Guid.NewGuid().ToString("D");
            _nodeName = string.IsNullOrWhiteSpace(nodeName)
                ? _mobileBackupComputerName
                : nodeName.Trim();
            _deploymentPreset = DeploymentPresets.IsKnown(deploymentPreset)
                ? DeploymentPresets.Normalize(deploymentPreset)
                : DeploymentPresets.RecordingHost;
            _orderReceiverOnly = orderReceiverOnly;
            _extensionApiEnabled = extensionApiEnabled;
            _backupDeviceEnrollmentApprover = backupDeviceEnrollmentApprover;
            _clipService = new VideoClipService(
                _db,
                WriteLog,
                _mkvConverter,
                IsCurrentRecordingFile,
                () => Task.Run(CleanWebCache),
                _ffmpegWorkLimiter);
            Port = port;
            _transCacheMaxBytes = (long)transCacheMaxMB * 1024 * 1024;
            _listener = CreateListener(port, listenerHost);
            MigrateLegacyOrderInfoCache();
            LoadOrderInfoCacheFromDatabase();
            string resolvedMobileBackupStateDirectory = mobileBackupStateDirectory
                ?? AppPaths.MobileBackupStateDir;
            _extensionStateDirectory = resolvedMobileBackupStateDirectory;
            _backupPairingTokens = new BackupPairingTokenService(
                resolvedMobileBackupStateDirectory,
                _accessKey);
            _mobileBackupService = new MobileBackupService(
                _db,
                resolvedMobileBackupStateDirectory,
                mobileBackupRecordingRootResolver ?? (() => Path.Combine(AppPaths.UserDataDir, "mobile-backup-recordings")),
                GetOrderInfo,
                mobileBackupArchiveTargetResolver,
                mobileBackupArchivePendingCallback);
            _mobileBackupService.ActiveUploadsChanged += hasActive =>
            {
                try { MobileBackupActivityChanged?.Invoke(hasActive); } catch { }
            };
            _mobileOrderReceivers = new MobileOrderReceiverRegistry(
                Path.Combine(resolvedMobileBackupStateDirectory, "order-receivers.json"));
            _recordingComputerNicknames = new RecordingComputerNicknameRegistry(
                Path.Combine(resolvedMobileBackupStateDirectory, "computer-nicknames.json"));
            _orderIntegrationActivities = new OrderIntegrationActivityRegistry(
                Path.Combine(resolvedMobileBackupStateDirectory, "order-integration-activity.json"));
            // 放在状态目录子目录，避免被 MobileBackupService 只扫描顶层 *.json 的上传状态清理误删。
            _userscriptConfigRevision = new UserscriptConfigRevisionStore(
                Path.Combine(resolvedMobileBackupStateDirectory, "userscript-config", "revision.json"));
            _userscriptCatalog = new UserscriptCatalog();
            if (string.Equals(_deploymentPreset, DeploymentPresets.RecordingHost, StringComparison.Ordinal))
                _recordingComputerNicknames.RegisterHost(_nodeId, _nodeName, nodeNameCustomized);
            _connectedClients = new ConnectedClientRegistry();
            _connectedClients.Changed += clients =>
            {
                try { ConnectedClientsChanged?.Invoke(clients); } catch { }
            };
        }

        internal void ConfigureExtensionEnrollment(
            ExtensionAuthorizationStore authorizations,
            Func<ExtensionEnrollmentRequest, ExtensionEnrollmentApprovalResult> approver)
        {
            if (!_extensionApiEnabled)
                throw new InvalidOperationException("扩展 API 未启用");
            if (_listener.IsListening)
                throw new InvalidOperationException("扩展授权服务必须在 Web 服务启动前配置");
            _extensionAuthorizations = authorizations
                ?? throw new ArgumentNullException(nameof(authorizations));
            _extensionRequestAuthenticator = new ExtensionRequestAuthenticator(_extensionAuthorizations);
            _extensionEnrollment = new ExtensionEnrollmentService(
                _extensionAuthorizations,
                approver ?? throw new ArgumentNullException(nameof(approver)));
        }

        internal void ConfigureExtensionTaskApi(
            ExtensionScanTaskBroker broker,
            ExtensionScanResultSubmissionCoordinator resultCoordinator,
            Action resultAvailable = null)
        {
            if (!_extensionApiEnabled)
                throw new InvalidOperationException("扩展 API 未启用");
            if (_listener.IsListening)
                throw new InvalidOperationException("扩展任务服务必须在 Web 服务启动前配置");
            _extensionScanTaskBroker = broker ?? throw new ArgumentNullException(nameof(broker));
            _extensionScanResultCoordinator = resultCoordinator
                ?? throw new ArgumentNullException(nameof(resultCoordinator));
            _extensionResultAvailable = resultAvailable;
        }

        private static HttpListener CreateListener(int port, string listenerHost)
        {
            string host = string.Equals(listenerHost, "127.0.0.1", StringComparison.Ordinal)
                ? listenerHost
                : "+";
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://{host}:{port}/");
            ConfigureListenerTimeouts(listener);
            return listener;
        }

        internal static readonly TimeSpan RequestHeaderWaitTimeout = TimeSpan.FromSeconds(20);
        internal static readonly TimeSpan RequestEntityBodyTimeout = TimeSpan.FromMinutes(2);
        internal static readonly TimeSpan IdleConnectionTimeout = TimeSpan.FromMinutes(2);

        private static void ConfigureListenerTimeouts(HttpListener listener)
        {
            try
            {
                listener.TimeoutManager.HeaderWait = RequestHeaderWaitTimeout;
                listener.TimeoutManager.EntityBody = RequestEntityBodyTimeout;
                listener.TimeoutManager.IdleConnection = IdleConnectionTimeout;
                listener.TimeoutManager.DrainEntityBody = TimeSpan.FromSeconds(10);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("WebServer", $"Unable to configure HTTP request timeouts: {ex.Message}");
            }
        }

        public void Start(bool allowAccessSetup = false)
        {
            if (IsTcpPortInUse(Port))
            {
                throw new InvalidOperationException(
                    $"Web 服务端口 {Port} 已被其他程序或尚未退出的旧版本占用，请关闭占用程序后重试");
            }

            bool accessConfigured = false;
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                if (ex.ErrorCode != 5)
                    throw new InvalidOperationException($"Web 服务监听 http://+:{Port}/ 失败，请检查端口是否被占用", ex);

                if (!allowAccessSetup)
                {
                    throw new InvalidOperationException(
                        "Web 服务缺少监听权限，需要管理员授权后才能启动",
                        ex);
                }

                // 只有调用方明确允许时，才请求管理员权限并重试
                ConfigureLanAccess(Port, includeUrlAcl: true);
                accessConfigured = true;
                try { _listener.Close(); } catch { }
                _listener = CreateListener(Port, "+");
                try
                {
                    _listener.Start();
                }
                catch (HttpListenerException retryException)
                {
                    throw new InvalidOperationException($"Web 服务监听 http://+:{Port}/ 失败，请检查端口占用、URL ACL 或防火墙权限", retryException);
                }
            }

            if (allowAccessSetup && !accessConfigured && !HasExpectedFirewallRule(Port))
            {
                try
                {
                    ConfigureLanAccess(Port, includeUrlAcl: false);
                }
                catch
                {
                    try { _listener.Stop(); } catch { }
                    throw;
                }
            }
            _mobileAppUpdatePolicy.RefreshInBackground();
            _mobileAppUpdateRefreshTimer = new Timer(
                _ => _mobileAppUpdatePolicy.RefreshInBackground(),
                null,
                TimeSpan.FromSeconds(ConnectedClientRegistry.HeartbeatIntervalSeconds),
                TimeSpan.FromSeconds(ConnectedClientRegistry.HeartbeatIntervalSeconds));
            _listenTask = Task.Run(() => ListenLoop(_cts.Token));
            _videoCodecBackfillTask = Task.Run(() => BackfillVideoCodecsAsync(_cts.Token));
            StartUdpDiscoveryResponder();
        }

        private async Task BackfillVideoCodecsAsync(CancellationToken cancellationToken)
        {
            long afterId = 0;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_isRecordingProvider() || _mobileBackupService.HasActiveUploads)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    List<(long Id, string FilePath)> batch = _db.GetVideosWithoutCodec(afterId, 20);
                    if (batch.Count == 0)
                        return;
                    foreach ((long id, string filePath) in batch)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (_isRecordingProvider() || _mobileBackupService.HasActiveUploads)
                            break;
                        afterId = id;
                        string codec = await VideoCodecProbe.ProbeAsync(filePath, cancellationToken).ConfigureAwait(false);
                        if (_db.TrySetVideoCodecIfEmpty(id, codec))
                            RuntimeLog.Info("VideoCodecProbe", $"Backfilled codec record={id}, codec={codec}");
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("VideoCodecProbe", $"Background codec backfill stopped: {ex.Message}");
            }
        }

        private void StartUdpDiscoveryResponder()
        {
            bool isHost = PackingProofCapabilities.ForPreset(_deploymentPreset)
                .Contains(PackingProofCapabilities.Host, StringComparer.OrdinalIgnoreCase);
            if (!isHost)
                return;

            _udpDiscoveryResponder = new UdpDiscoveryResponder(
                _nodeId,
                () => Port,
                () => true);
            _udpDiscoveryResponder.Start();
        }

        internal static bool IsListenerConflict(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is HttpListenerException listenerException &&
                    listenerException.ErrorCode == 183)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsTcpPortInUse(int port)
        {
            if (port <= 0 || port > 65535)
                return false;

            try
            {
                return IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners()
                    .Any(endpoint => endpoint.Port == port);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 注册 URL ACL 和防火墙规则，需要管理员权限时会弹出 UAC 提示。
        /// </summary>
        private static void ConfigureLanAccess(int port, bool includeUrlAcl)
        {
            EnsureFirewallServicesAvailable();
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string userSid = identity.User?.Value;
            if (string.IsNullOrWhiteSpace(userSid))
                throw new InvalidOperationException("无法获取当前用户 SID，不能配置局域网服务监听权限");

            RunElevatedCmd(BuildAccessSetupCommand(port, userSid, includeUrlAcl), "配置局域网服务访问权限");
        }

        internal static void RepairLanAccess(int port)
        {
            ConfigureLanAccess(port, includeUrlAcl: true);
        }

        internal const string FirewallRuleName = "快递打包监控 Web服务";
        internal const string UdpFirewallRuleName = "快递打包监控 设备发现";
        internal const int UrlAclAddFailureExitCode = 11;
        internal const int TcpFirewallAddFailureExitCode = 12;
        internal const int UdpFirewallAddFailureExitCode = 13;

        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceQueryStatus = 0x0004;
        private const int ScStatusProcessInfo = 0;
        private const uint ServiceRunning = 0x00000004;
        private const int ServiceDisabled = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatusProcess
        {
            internal uint ServiceType;
            internal uint CurrentState;
            internal uint ControlsAccepted;
            internal uint Win32ExitCode;
            internal uint ServiceSpecificExitCode;
            internal uint CheckPoint;
            internal uint WaitHint;
            internal uint ProcessId;
            internal uint ServiceFlags;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr serviceManager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatusEx(
            IntPtr service,
            int infoLevel,
            out ServiceStatusProcess serviceStatus,
            int bufferSize,
            out int bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr serviceHandle);

        internal static WindowsServiceDiagnostic InspectWindowsService(string serviceName)
        {
            int? startType = ReadServiceStartType(serviceName);
            if (!OperatingSystem.IsWindows())
                return new WindowsServiceDiagnostic(serviceName, false, false, startType == ServiceDisabled, 0, startType);

            IntPtr serviceManager = IntPtr.Zero;
            IntPtr service = IntPtr.Zero;
            try
            {
                serviceManager = OpenSCManager(null, null, ScManagerConnect);
                if (serviceManager == IntPtr.Zero)
                    return new WindowsServiceDiagnostic(serviceName, false, false, startType == ServiceDisabled, 0, startType);

                service = OpenService(serviceManager, serviceName, ServiceQueryStatus);
                if (service == IntPtr.Zero)
                    return new WindowsServiceDiagnostic(serviceName, false, false, startType == ServiceDisabled, 0, startType);

                int size = Marshal.SizeOf<ServiceStatusProcess>();
                bool queried = QueryServiceStatusEx(
                    service,
                    ScStatusProcessInfo,
                    out ServiceStatusProcess status,
                    size,
                    out _);
                return new WindowsServiceDiagnostic(
                    serviceName,
                    queried,
                    queried && status.CurrentState == ServiceRunning,
                    startType == ServiceDisabled,
                    queried ? status.CurrentState : 0,
                    startType);
            }
            catch
            {
                return new WindowsServiceDiagnostic(serviceName, false, false, startType == ServiceDisabled, 0, startType);
            }
            finally
            {
                if (service != IntPtr.Zero)
                    CloseServiceHandle(service);
                if (serviceManager != IntPtr.Zero)
                    CloseServiceHandle(serviceManager);
            }
        }

        private static int? ReadServiceStartType(string serviceName)
        {
            if (!OperatingSystem.IsWindows())
                return null;
            try
            {
                using RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}",
                    writable: false);
                return key?.GetValue("Start") is int value ? value : null;
            }
            catch
            {
                return null;
            }
        }

        private static void EnsureFirewallServicesAvailable()
        {
            WindowsServiceDiagnostic firewall = InspectWindowsService("MpsSvc");
            WindowsServiceDiagnostic filteringEngine = InspectWindowsService("BFE");
            RuntimeLog.Info(
                "Web",
                $"LAN firewall service diagnostics MpsSvc={FormatServiceDiagnostic(firewall)}, BFE={FormatServiceDiagnostic(filteringEngine)}");

            if (filteringEngine.QuerySucceeded && !filteringEngine.IsRunning)
            {
                throw new LanAccessSetupException(
                    LanAccessFailureKind.BaseFilteringEngineUnavailable,
                    "Base Filtering Engine service is not running");
            }
            if (firewall.QuerySucceeded && !firewall.IsRunning)
            {
                throw new LanAccessSetupException(
                    LanAccessFailureKind.WindowsFirewallServiceUnavailable,
                    "Windows Defender Firewall service is not running");
            }
        }

        private static string FormatServiceDiagnostic(WindowsServiceDiagnostic diagnostic)
        {
            string state = diagnostic.QuerySucceeded
                ? diagnostic.IsRunning ? "running" : $"state-{diagnostic.CurrentState}"
                : "unknown";
            string start = diagnostic.StartType?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            return $"{state},start={start},disabled={diagnostic.IsDisabled}";
        }

        internal static string BuildAccessSetupCommand(int port, string userSid, bool includeUrlAcl = true)
        {
            if (port <= 0 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));
            if (string.IsNullOrWhiteSpace(userSid))
                throw new ArgumentException("用户 SID 不能为空", nameof(userSid));

            string url = $"http://+:{port}/";
            string firewallCommand = BuildFirewallSetupCommand(port);
            if (!includeUrlAcl)
                return firewallCommand;

            string urlAclCommand = $"(echo [LAN-STEP] urlacl-delete & netsh http delete urlacl url={url} & "
                + $"echo [LAN-STEP] urlacl-add & netsh http add urlacl url={url} sddl=\"D:(A;;GX;;;{userSid})\" "
                + $"|| exit /b {UrlAclAddFailureExitCode})";
            return $"{urlAclCommand} && ({firewallCommand})";
        }

        internal static string BuildFirewallSetupCommand(int port)
        {
            if (port <= 0 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));

            string tcpRule = $"echo [LAN-STEP] firewall-tcp-delete & netsh advfirewall firewall delete rule name=\"{FirewallRuleName}\" & "
                + $"echo [LAN-STEP] firewall-tcp-add & netsh advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={port} "
                + $"|| exit /b {TcpFirewallAddFailureExitCode}";
            string udpRule = $"echo [LAN-STEP] firewall-udp-delete & netsh advfirewall firewall delete rule name=\"{UdpFirewallRuleName}\" & "
                + $"echo [LAN-STEP] firewall-udp-add & netsh advfirewall firewall add rule name=\"{UdpFirewallRuleName}\" dir=in action=allow protocol=UDP localport={UdpDiscoveryProtocol.Port} "
                + $"|| exit /b {UdpFirewallAddFailureExitCode}";
            return $"({tcpRule}) && ({udpRule})";
        }

        internal static bool HasExpectedFirewallRule(int port)
        {
            if (port <= 0 || port > 65535)
                return false;

            object policyObject = null;
            try
            {
                Type policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (policyType == null)
                    return false;

                policyObject = Activator.CreateInstance(policyType);
                dynamic policy = policyObject;
                bool tcpFound = false;
                bool udpFound = false;
                foreach (dynamic rule in policy.Rules)
                {
                    try
                    {
                        string ruleName = (string)rule.Name;
                        bool enabled = (bool)rule.Enabled;
                        bool inbound = (int)rule.Direction == 1;
                        bool allow = (int)rule.Action == 1;
                        int protocol = (int)rule.Protocol;
                        bool allProfiles = ((int)rule.Profiles & 7) == 7 || (int)rule.Profiles == int.MaxValue;
                        if (string.Equals(ruleName, FirewallRuleName, StringComparison.Ordinal)
                            && enabled
                            && inbound
                            && allow
                            && protocol == 6
                            && allProfiles
                            && FirewallPortsContain((string)rule.LocalPorts, port))
                        {
                            tcpFound = true;
                        }
                        else if (string.Equals(ruleName, UdpFirewallRuleName, StringComparison.Ordinal)
                            && enabled
                            && inbound
                            && allow
                            && protocol == 17
                            && allProfiles
                            && FirewallPortsContain((string)rule.LocalPorts, UdpDiscoveryProtocol.Port))
                        {
                            udpFound = true;
                        }
                    }
                    catch
                    {
                        // 忽略无法读取的第三方或损坏规则，继续检查同名规则。
                    }
                    finally
                    {
                        try
                        {
                            if (Marshal.IsComObject(rule))
                                Marshal.FinalReleaseComObject(rule);
                        }
                        catch { }
                    }
                }
                return tcpFound && udpFound;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Web", $"Unable to inspect Windows Firewall rule: {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    if (policyObject != null && Marshal.IsComObject(policyObject))
                        Marshal.FinalReleaseComObject(policyObject);
                }
                catch { }
            }
        }

        internal static bool FirewallPortsContain(string localPorts, int port)
        {
            if (string.IsNullOrWhiteSpace(localPorts))
                return false;

            foreach (string entry in localPorts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (entry is "*" or "Any")
                    return true;
                if (int.TryParse(entry, out int singlePort) && singlePort == port)
                    return true;

                string[] range = entry.Split('-', 2, StringSplitOptions.TrimEntries);
                if (range.Length == 2
                    && int.TryParse(range[0], out int start)
                    && int.TryParse(range[1], out int end)
                    && port >= start
                    && port <= end)
                {
                    return true;
                }
            }
            return false;
        }

        private static void RunElevatedCmd(string arguments, string actionName)
        {
            string outputPath = Path.Combine(Path.GetTempPath(), $"PackingProof-lan-repair-{Guid.NewGuid():N}.log");
            try
            {
                bool alreadyElevated = IsCurrentProcessElevated();
                RuntimeLog.Info("Web", $"LAN access setup elevation requested alreadyElevated={alreadyElevated}");
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = BuildElevatedCmdArguments(arguments, outputPath),
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null)
                    throw new InvalidOperationException($"{actionName}失败：无法启动管理员命令");

                if (!proc.WaitForExit(15000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    throw new TimeoutException($"{actionName}超时，请手动以管理员身份运行 netsh 或关闭 Web 服务");
                }

                if (proc.ExitCode != 0)
                {
                    string output = ReadElevatedCmdOutput(outputPath);
                    string failedStep = ResolveLanSetupFailureStep(proc.ExitCode, output);
                    string detail = CompactLanSetupOutput(output);
                    if (failedStep.StartsWith("firewall-", StringComparison.Ordinal))
                    {
                        try
                        {
                            EnsureFirewallServicesAvailable();
                        }
                        catch (LanAccessSetupException serviceException)
                        {
                            throw new LanAccessSetupException(
                                serviceException.FailureKind,
                                $"{actionName}失败，失败阶段：{failedStep}，输出：{detail}",
                                serviceException);
                        }
                    }
                    throw new InvalidOperationException(
                        $"{actionName}失败，netsh 退出码：{proc.ExitCode}，失败阶段：{failedStep}，输出：{detail}");
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new InvalidOperationException($"{actionName}未完成：未获得管理员授权", ex);
            }
            catch (Exception ex) when (ex is not InvalidOperationException && ex is not TimeoutException)
            {
                throw new InvalidOperationException($"{actionName}失败，可能是用户取消了管理员授权或系统拒绝执行", ex);
            }
            finally
            {
                try { File.Delete(outputPath); } catch { }
            }
        }

        internal static string BuildElevatedCmdCommand(string arguments, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(arguments))
                throw new ArgumentException("管理员命令不能为空", nameof(arguments));
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("命令输出路径不能为空", nameof(outputPath));

            return $"({arguments}) > \"{outputPath}\" 2>&1";
        }

        internal static string BuildElevatedCmdArguments(string arguments, string outputPath)
        {
            string command = BuildElevatedCmdCommand(arguments, outputPath);
            return $"/D /S /C \"{command.Replace("\"", "\"\"")}\"";
        }

        internal static string ResolveLanSetupFailureStep(int exitCode, string output) =>
            exitCode switch
            {
                UrlAclAddFailureExitCode => "urlacl-add",
                TcpFirewallAddFailureExitCode => "firewall-tcp-add",
                UdpFirewallAddFailureExitCode => "firewall-udp-add",
                _ => ExtractLastLanSetupStep(output)
            };

        internal static bool IsCurrentProcessElevated()
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity)
                    .IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        internal static string ExtractLastLanSetupStep(string output)
        {
            const string marker = "[LAN-STEP] ";
            string step = "unknown";
            foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                int markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
                if (markerIndex >= 0)
                    step = line[(markerIndex + marker.Length)..].Trim();
            }
            return step;
        }

        internal static string CompactLanSetupOutput(string output)
        {
            string compact = string.Join(
                " | ",
                output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (string.IsNullOrWhiteSpace(compact))
                return "无命令输出";
            return compact.Length <= 4000 ? compact : compact[..4000] + "…";
        }

        internal static string GetLanAccessFailureUserMessage(
            bool repairAttempted,
            bool repairButtonAvailable = false,
            Exception exception = null)
        {
            LanAccessFailureKind failureKind = ResolveLanAccessFailureKind(exception);
            if (failureKind == LanAccessFailureKind.WindowsFirewallServiceUnavailable)
            {
                return "Windows Defender Firewall 服务未开启，手机和其他电脑暂时无法连接，本机功能不受影响。请在 Windows“服务”中启动“Windows Defender Firewall”，然后重新修复；如果该服务由安全软件或组织策略管理，请在对应软件中允许 PackingProof 的局域网和防火墙修改";
            }
            if (failureKind == LanAccessFailureKind.BaseFilteringEngineUnavailable)
            {
                return "Windows 的“Base Filtering Engine”服务未开启，防火墙无法配置，手机和其他电脑暂时无法连接，本机功能不受影响。请在 Windows“服务”中启动“Base Filtering Engine”和“Windows Defender Firewall”，然后重新修复；如果服务由安全软件或组织策略管理，请在对应软件中允许 PackingProof 的局域网和防火墙修改";
            }

            if (repairAttempted)
            {
                return "局域网修复未完成，手机和其他电脑暂时无法连接，本机功能不受影响。请确认在管理员授权窗口选择了“是”；如果安全软件弹出拦截提示，请选择允许；仍失败时，请导出反馈信息发给技术支持";
            }

            string nextStep = repairButtonAvailable
                ? "请点击“修复局域网”，并在管理员授权窗口选择“是”"
                : "请重新启用局域网服务，并在管理员授权窗口选择“是”";
            return $"局域网连接失败，手机和其他电脑暂时无法连接，本机功能不受影响。{nextStep}；如果仍失败，请导出反馈信息发给技术支持";
        }

        internal static LanAccessFailureKind ResolveLanAccessFailureKind(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (current is LanAccessSetupException setupException)
                    return setupException.FailureKind;
            }
            return LanAccessFailureKind.Unknown;
        }

        private static string ReadElevatedCmdOutput(string outputPath)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                Encoding outputEncoding = Encoding.GetEncoding(
                    System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                return File.Exists(outputPath)
                    ? File.ReadAllText(outputPath, outputEncoding)
                    : "";
            }
            catch (Exception ex)
            {
                return $"读取命令输出失败：{ex.Message}";
            }
        }

        private async Task ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    try
                    {
                        if (!_requestSlots.Wait(0))
                        {
                            ctx.Response.Headers["Retry-After"] = "1";
                            SendJson(ctx, 503, new
                            {
                                errorCode = "server_busy",
                                error = "保存主机当前请求较多，请稍后重试"
                            });
                            continue;
                        }
                    }
                    catch
                    {
                        try { ctx.Response.Abort(); } catch { }
                        throw;
                    }

                    try
                    {
                        BeginActiveRequest();
                        _ = Task.Run(() =>
                        {
                            try { HandleRequest(ctx); }
                            finally
                            {
                                _requestSlots.Release();
                                EndActiveRequest();
                            }
                        });
                    }
                    catch
                    {
                        _requestSlots.Release();
                        EndActiveRequest();
                        try { ctx.Response.Abort(); } catch { }
                        throw;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            IDisposable requestLease = null;
            try
            {
                string path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";
                string method = ctx.Request.HttpMethod;
                Log($">>> {method} {path} from {ctx.Request.RemoteEndPoint}");

                LanRequestCategory requestCategory = ClassifyRequest(method, path);
                if (!_requestRateLimiter.TryEnter(
                        ctx.Request.RemoteEndPoint?.Address?.ToString(),
                        requestCategory,
                        out requestLease,
                        out int retryAfterSeconds))
                {
                    ctx.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                    SendJson(ctx, 429, new
                    {
                        errorCode = "request_rate_limited",
                        error = "请求较多，请稍后重试",
                        retryAfterSeconds
                    });
                    return;
                }

                ApplyCorsHeaders(ctx);
                ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, OPTIONS");
                ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Content-Range, X-EPM-Access-Key, X-EPM-Auth-Version, X-EPM-Timestamp, X-EPM-Nonce, X-EPM-Content-SHA256, X-EPM-Signature, X-EPM-Device-Id, X-EPM-Device-Name, X-EPM-Device-Kind, X-Chunk-SHA256, X-PackingProof-Extension-Version, X-PackingProof-Extension-Id, X-PackingProof-Extension-Credential-Generation, X-PackingProof-Extension-Timestamp, X-PackingProof-Extension-Nonce, X-PackingProof-Extension-Content-SHA256, X-PackingProof-Extension-Signature");

                if (method == "POST")
                {
                    int maxBodyBytes = path is "/api/orderinfo" or "/api/orderinfo/broadcast" or "/api/order-lookup/result" or "/api/extensions/v1/orders"
                        || path.StartsWith("/api/extensions/v1/recordings/", StringComparison.OrdinalIgnoreCase)
                        ? MaxOrderInfoBodyBytes
                        : MaxJsonBodyBytes;
                    if (ctx.Request.ContentLength64 > maxBodyBytes)
                    {
                        SendJson(ctx, 413, new { error = $"请求内容过大，最大允许 {maxBodyBytes / 1024} KB" });
                        return;
                    }
                }

                if (method == "PUT" && IsMobileBackupPath(path)
                    && ctx.Request.ContentLength64 > MobileBackupService.ChunkSizeBytes)
                {
                    SendJson(ctx, 413, new { errorCode = "chunk_too_large", error = "分块超过服务端上限" });
                    return;
                }

                if (method == "OPTIONS")
                {
                    ctx.Response.StatusCode = 204;
                    ctx.Response.OutputStream.Close();
                    return;
                }

                if (_orderReceiverOnly && !IsOrderReceiverPathAllowed(path, method))
                {
                    SendJson(ctx, 404, new { error = "Not Found" });
                    return;
                }

                bool isDeviceEnrollment = path == "/api/mobile-backup/enroll" && method == "POST";
                bool hasDeviceAssetTicket = method == "GET" && HasValidDeviceAssetTicket(ctx, path);
                if (!isDeviceEnrollment && !hasDeviceAssetTicket && IsMobileBackupPath(path)
                    && !TryAuthorizeMobileBackupRequest(ctx, out bool missingBackupKey, out bool obsoleteProtocol))
                {
                    SendJson(ctx, obsoleteProtocol ? 426 : missingBackupKey ? 401 : 403, new
                    {
                        errorCode = obsoleteProtocol ? "backup_protocol_upgrade_required"
                            : missingBackupKey ? "enrollment_required" : "device_token_invalid",
                        error = obsoleteProtocol ? "备份协议已升级，请更新客户端后重新连接"
                            : missingBackupKey ? "需要重新连接保存主机" : "设备令牌无效，请重新连接"
                    });
                    return;
                }

                bool isSignedExtensionRequest = IsSignedExtensionPath(path, method);
                if (isSignedExtensionRequest && !TryAuthorizeExtensionRequest(ctx, out int extensionAuthStatus, out string extensionAuthCode))
                {
                    SendJson(ctx, extensionAuthStatus, new
                    {
                        errorCode = extensionAuthCode,
                        error = GetExtensionAuthenticationError(extensionAuthCode)
                    });
                    return;
                }

                if (_requireAccessKey && RequiresAccessKey(path) && !isSignedExtensionRequest)
                {
                    bool authorized = TryAuthorizeRequest(ctx, out bool authorizedByQuery);
                    if (!authorized)
                    {
                        SendUnauthorized(ctx, path);
                        return;
                    }

                    if (authorizedByQuery && path == "")
                    {
                        ctx.Response.StatusCode = 302;
                        ctx.Response.RedirectLocation = "/";
                        ctx.Response.OutputStream.Close();
                        return;
                    }
                }

                switch (path)
                {
                    case "" or "/":
                        ServeIndexPage(ctx);
                        break;
                    case "/api/node-info" when method == "GET":
                        HandleNodeInfo(ctx);
                        break;
                    case "/api/recording-devices" when method == "GET":
                        HandleRecordingDevices(ctx);
                        break;
                    case "/api/videos":
                        HandleSearchVideos(ctx);
                        break;
                    case "/api/video-sources" when method == "GET":
                        HandleVideoSources(ctx);
                        break;
                    case "/api/videos/status" when method == "GET":
                        HandleVideoStatuses(ctx);
                        break;
                    case "/api/storage":
                        HandleStorageOverview(ctx);
                        break;
                    case "/api/mobile-app-download" when method == "GET":
                        HandleMobileAppDownload(ctx);
                        break;
                    case "/api/mobile-connection" when method == "GET":
                        HandleMobileConnection(ctx);
                        break;
                    case "/api/mobile-backup/capabilities" when method == "GET":
                        HandleMobileBackupCapabilities(ctx);
                        break;
                    case "/api/extensions/v1/capabilities" when method == "GET":
                        HandleExtensionCapabilities(ctx);
                        break;
                    case "/api/extensions/v1/enroll" when method == "POST":
                        HandleExtensionEnrollment(ctx);
                        break;
                    case "/api/extensions/v1/heartbeat" when method == "POST":
                        HandleExtensionHeartbeat(ctx);
                        break;
                    case "/api/extensions/v1/scan-tasks/next" when method == "GET":
                        HandlePollExtensionScanTask(ctx);
                        break;
                    case "/api/extensions/v1/scan-results" when method == "POST":
                        HandleExtensionScanResult(ctx);
                        break;
                    case var p when method == "POST"
                        && p.StartsWith("/api/extensions/v1/scan-tasks/", StringComparison.OrdinalIgnoreCase)
                        && p.EndsWith("/ack", StringComparison.OrdinalIgnoreCase):
                        HandleAcknowledgeExtensionScanTask(ctx, p);
                        break;
                    case "/api/extensions/v1/orders" when method == "POST":
                        HandleExtensionOrders(ctx);
                        break;
                    case "/api/extensions/v1/recordings/active" when method == "GET":
                        HandleActiveRecordingExtensions(ctx);
                        break;
                    case var p when p.StartsWith("/api/extensions/v1/recordings/", StringComparison.OrdinalIgnoreCase)
                        && p.EndsWith("/data", StringComparison.OrdinalIgnoreCase)
                        && method is "GET" or "POST":
                        HandleRecordingExtensionData(ctx, p, method);
                        break;
                    case "/api/mobile-backup/enroll" when method == "POST":
                        HandleBackupDeviceEnrollment(ctx);
                        break;
                    case "/api/mobile-backup/uploads" when method == "POST":
                        HandleCreateMobileBackupUpload(ctx);
                        break;
                    case "/api/mobile-backup/videos" when method == "GET":
                        HandleDeviceScopedVideos(ctx);
                        break;
                    case "/api/mobile-backup/videos/status" when method == "GET":
                        HandleDeviceScopedVideoStatuses(ctx);
                        break;
                    case var p when method == "GET" && p.StartsWith("/api/mobile-backup/clip-tasks/") && !p.EndsWith("/cancel"):
                        HandleGetDeviceClipTask(ctx, path);
                        break;
                    case var p when method == "POST" && p.StartsWith("/api/mobile-backup/clip-tasks/") && p.EndsWith("/cancel"):
                        HandleCancelDeviceClipTask(ctx, path);
                        break;
                    case var p when method == "GET" && p.StartsWith("/api/mobile-backup/clips/"):
                        HandleServeDeviceClip(ctx, path);
                        break;
                    case var p when method == "GET" && p.StartsWith("/api/mobile-backup/clip-previews/"):
                        HandleServeDeviceClipPreview(ctx, path);
                        break;
                    case "/api/connections/heartbeat" when method == "POST":
                        HandleConnectionHeartbeat(ctx);
                        break;
                    case var p when method == "GET" && p.StartsWith("/api/clip-tasks/") && !p.EndsWith("/cancel"):
                        HandleGetClipTask(ctx, path);
                        break;
                    case var p when method == "POST" && p.StartsWith("/api/clip-tasks/") && p.EndsWith("/cancel"):
                        HandleCancelClipTask(ctx, path);
                        break;
                    case var p when method == "GET" && p.StartsWith("/api/clips/"):
                        HandleServeClip(ctx, path);
                        break;
                    case var p when method == "GET" && p.StartsWith("/api/clip-previews/"):
                        HandleServeClipPreview(ctx, path);
                        break;
                    case "/kuaidizs-install-guide":
                        ServeInstallGuidePage(ctx);
                        break;
                    case "/PackingProof-Order-Integration-KDZS.user.js":
                        ServeUserscript(ctx, ctx.Request.QueryString["scriptId"]);
                        break;
                    case "/kuaidizs-order-push.user.js":
                        ServeUserscript(ctx, UserscriptCatalog.OfficialId);
                        break;
                    case var p when p.StartsWith("/api/userscripts/", StringComparison.OrdinalIgnoreCase)
                        && p.EndsWith("/download", StringComparison.OrdinalIgnoreCase):
                        ServeUserscript(ctx, p["/api/userscripts/".Length..^"/download".Length]);
                        break;
                    case "/api/orderinfo":
                        if (method == "POST")
                            HandlePushOrderInfo(ctx);
                        else
                            HandleQueryOrderInfo(ctx);
                        break;
                    case "/api/orderinfo/broadcast" when method == "POST":
                        HandleBroadcastOrderInfo(ctx);
                        break;
                    case "/api/order-lookup/pending" when method == "GET":
                        HandlePollOrderLookup(ctx);
                        break;
                    case "/api/order-lookup/result" when method == "POST":
                        HandleOrderLookupResult(ctx);
                        break;
                    default:
                        if (method == "PUT" && IsMobileBackupUploadPath(path, "/chunks", out string chunkUploadId))
                            HandleMobileBackupChunk(ctx, chunkUploadId);
                        else if (method == "POST" && IsMobileBackupUploadPath(path, "/complete", out string completeUploadId))
                            HandleCompleteMobileBackupUpload(ctx, completeUploadId);
                        else if (method == "GET" && TryParseMobileBackupAttestationPath(path, out long remoteRecordId))
                            HandleMobileBackupAttestation(ctx, remoteRecordId);
                        else if (method == "GET" && TryParseDeviceScopedVideoPath(path, "/play", out long scopedPlayId))
                            HandleDeviceScopedVideo(ctx, scopedPlayId, "play");
                        else if (method == "GET" && TryParseDeviceScopedVideoPath(path, "/download", out long scopedDownloadId))
                            HandleDeviceScopedVideo(ctx, scopedDownloadId, "download");
                        else if (method == "GET" && TryParseDeviceScopedVideoPath(path, "/thumbnail", out long scopedThumbnailId))
                            HandleDeviceScopedVideo(ctx, scopedThumbnailId, "thumbnail");
                        else if (method == "POST" && TryParseDeviceScopedVideoPath(path, "/clip/timeline", out long scopedTimelineId))
                            HandleDeviceClipTimeline(ctx, scopedTimelineId);
                        else if (method == "POST" && TryParseDeviceScopedVideoPath(path, "/clip", out long scopedClipId))
                            HandleStartDeviceClip(ctx, scopedClipId);
                        else if (method == "HEAD" && path.StartsWith("/api/videos/") && path.EndsWith("/play"))
                        {
                            // HEAD 请求只返回 headers，不启动转码/传输
                            ctx.Response.ContentType = "video/mp4";
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentLength64 = 0;
                            ctx.Response.OutputStream.Close();
                        }
                        else if (path.StartsWith("/api/videos/") && path.EndsWith("/download"))
                            HandleDownload(ctx, path);
                        else if (method == "POST" && path.StartsWith("/api/videos/") && path.EndsWith("/clip/prewarm"))
                            HandleClipPrewarm(ctx, path);
                        else if (method == "POST" && path.StartsWith("/api/videos/") && path.EndsWith("/clip/timeline"))
                            HandleClipTimeline(ctx, path);
                        else if (method == "POST" && path.StartsWith("/api/videos/") && path.EndsWith("/clip/frame"))
                            HandleClipFrame(ctx, path);
                        else if (method == "POST" && path.StartsWith("/api/videos/") && path.EndsWith("/clip/preview"))
                            HandleClipPreview(ctx, path);
                        else if (method == "POST" && path.StartsWith("/api/videos/") && path.EndsWith("/clip"))
                            HandleStartClip(ctx, path);
                        else if (method == "GET" && path.StartsWith("/api/videos/") && path.EndsWith("/thumbnail"))
                            HandleVideoThumbnail(ctx, path);
                        else if (path.StartsWith("/api/videos/") && path.EndsWith("/play"))
                            HandlePlay(ctx, path);
                        else
                            SendJson(ctx, 404, new { error = "Not Found" });
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"!!! HandleRequest 异常: {ex}");
                try { SendJson(ctx, 500, new { error = ex.Message }); } catch { }
            }
            finally
            {
                requestLease?.Dispose();
                _authenticatedRequestBodies.TryRemove(ctx, out _);
                _authenticatedExtensionContexts.TryRemove(ctx, out _);
                _authenticatedDeviceKeys.TryRemove(ctx, out _);
                _authenticatedDeviceIds.TryRemove(ctx, out _);
                _authenticatedDeviceKinds.TryRemove(ctx, out _);
            }
        }

        private static void ApplyCorsHeaders(HttpListenerContext ctx)
        {
            string origin = ctx.Request.Headers["Origin"];
            if (string.IsNullOrWhiteSpace(origin)) return;
            if (!IsAllowedCorsOrigin(origin)) return;

            ctx.Response.Headers["Access-Control-Allow-Origin"] = origin;
            ctx.Response.Headers["Vary"] = "Origin";
            if (string.Equals(
                    ctx.Request.Headers["Access-Control-Request-Private-Network"],
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            }
        }

        internal static bool RequiresAccessKey(string path)
        {
            if (string.Equals(path, "/api/extensions/v1/enroll", StringComparison.OrdinalIgnoreCase))
                return false;
            return path == ""
                || path.StartsWith("/api/videos", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/video-sources", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/storage", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/clip", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/mobile-connection", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/extensions/v1", StringComparison.OrdinalIgnoreCase);
        }

        private void HandleNodeInfo(HttpListenerContext ctx)
        {
            SendJson(ctx, 200, new PackingProofNodeInfo
            {
                Protocol = PackingProofNodeInfo.ExpectedProtocol,
                ProtocolVersion = PackingProofNodeInfo.SupportedProtocolVersion,
                NodeId = _nodeId,
                NodeName = _nodeName,
                Preset = _deploymentPreset,
                Capabilities = PackingProofCapabilities.ForPreset(_deploymentPreset).ToList(),
                HttpPort = Port,
                AccessProtected = _requireAccessKey,
                BackupCompatibility = PackingProofCapabilities.ForPreset(_deploymentPreset).Contains(
                    PackingProofCapabilities.MobileBackup,
                    StringComparer.OrdinalIgnoreCase)
                        ? BackupCompatibilityPolicy.CreateHostInfo()
                        : null
            });
        }

        private void HandleRecordingDevices(HttpListenerContext ctx)
        {
            bool includeKnown = string.Equals(
                ctx.Request.QueryString["scope"],
                "known",
                StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<RecordingDeviceInfo> devices = GetRecordingDevices(
                ctx.Request.Url?.Authority ?? "",
                includeKnown);
            SendJson(ctx, 200, new { devices });
        }

        internal IReadOnlyList<RecordingDeviceInfo> GetRecordingDevices(
            string currentAuthority,
            bool includeKnown = false)
        {
            string primaryAuthority = ResolveUserscriptPrimaryAuthority(currentAuthority);
            string hostAddress = $"http://{primaryAuthority}";
            if (RecordingDeviceCatalog.NormalizeLanHttpAddress(hostAddress, Port).Length == 0)
            {
                string fallbackAuthority = global::ExpressPackingMonitoring.WorkstationNetwork
                    .GetBestLocalAccessAddress(Port);
                hostAddress = $"http://{fallbackAuthority}";
            }

            return RecordingDeviceCatalog.Build(
                _deploymentPreset,
                _nodeId,
                _nodeName,
                Port,
                hostAddress,
                includeKnown
                    ? _mobileOrderReceivers.GetKnownRecordingDevices()
                    : _mobileOrderReceivers.GetRecordingDevices(),
                _connectedClients.GetSnapshot(),
                includeOffline: includeKnown);
        }

        internal static bool IsMobileBackupPath(string path) =>
            path?.StartsWith("/api/mobile-backup", StringComparison.OrdinalIgnoreCase) == true;

        internal static bool IsOrderReceiverPathAllowed(string path, string method) =>
            (path == "/api/node-info" && method == "GET")
            || (path == "/api/orderinfo" && method is "GET" or "POST")
            || (path == "/api/extensions/v1/capabilities" && method == "GET")
            || (path == "/api/extensions/v1/enroll" && method == "POST")
            || (path == "/api/extensions/v1/orders" && method == "POST")
            || (path == "/api/extensions/v1/recordings/active" && method == "GET")
            || (path.StartsWith("/api/extensions/v1/recordings/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/data", StringComparison.OrdinalIgnoreCase)
                && method is "GET" or "POST")
            || (path == "/api/order-lookup/pending" && method == "GET")
            || (path == "/api/order-lookup/result" && method == "POST")
            || (path == "/api/connections/heartbeat" && method == "POST")
            || (path == "/kuaidizs-install-guide" && method == "GET")
            || (path == "/kuaidizs-order-push.user.js" && method == "GET")
            || (path == "/PackingProof-Order-Integration-KDZS.user.js" && method == "GET")
            || (path.StartsWith("/api/userscripts/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/download", StringComparison.OrdinalIgnoreCase)
                && method == "GET");

        private bool TryAuthorizeMobileBackupRequest(
            HttpListenerContext ctx,
            out bool missingKey,
            out bool obsoleteProtocol)
        {
            string version = ctx.Request.Headers[BackupRequestAuthentication.VersionHeader]?.Trim() ?? "";
            obsoleteProtocol = version.Length > 0
                && !string.Equals(version, BackupRequestAuthentication.CurrentVersion.ToString(), StringComparison.Ordinal);
            if (obsoleteProtocol)
            {
                missingKey = false;
                return false;
            }
            return TryAuthorizeSignedBackupRequest(ctx, out missingKey);
        }

        internal static LanRequestCategory ClassifyRequest(string method, string path)
        {
            if (string.Equals(path, "/api/mobile-backup/enroll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "/api/extensions/v1/enroll", StringComparison.OrdinalIgnoreCase))
                return LanRequestCategory.Enrollment;
            if (string.Equals(path, "/api/connections/heartbeat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, "/api/extensions/v1/heartbeat", StringComparison.OrdinalIgnoreCase))
                return LanRequestCategory.Heartbeat;
            if (IsMobileBackupPath(path)
                && (string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("/uploads", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("/complete", StringComparison.OrdinalIgnoreCase)))
            {
                return LanRequestCategory.BackupTransfer;
            }
            if (path.Contains("/clip", StringComparison.OrdinalIgnoreCase)
                && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return LanRequestCategory.ClipWork;
            }
            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/thumbnail", StringComparison.OrdinalIgnoreCase))
            {
                return LanRequestCategory.Thumbnail;
            }
            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                && (path.EndsWith("/play", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("/download", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/api/clips/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/api/mobile-backup/clips/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/api/clip-previews/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/api/mobile-backup/clip-previews/", StringComparison.OrdinalIgnoreCase)))
            {
                return LanRequestCategory.MediaStream;
            }
            return LanRequestCategory.General;
        }

        private bool TryAuthorizeSignedBackupRequest(HttpListenerContext ctx, out bool missingKey)
        {
            string deviceId = ctx.Request.Headers["X-EPM-Device-Id"]?.Trim() ?? "";
            if (!_backupPairingTokens.TryGetDeviceCredential(
                    deviceId,
                    out string credential,
                    out string deviceKind))
            {
                missingKey = string.IsNullOrWhiteSpace(
                    ctx.Request.Headers[BackupRequestAuthentication.SignatureHeader]);
                return false;
            }
            return TryAuthorizeSignedRequest(
                ctx,
                deviceId,
                credential,
                deviceKind,
                out missingKey);
        }

        private bool TryAuthorizeSignedRequest(
            HttpListenerContext ctx,
            string deviceId,
            string credential,
            string deviceKind,
            out bool missingKey)
        {
            string timestampText = ctx.Request.Headers[BackupRequestAuthentication.TimestampHeader]?.Trim() ?? "";
            string nonce = ctx.Request.Headers[BackupRequestAuthentication.NonceHeader]?.Trim() ?? "";
            string declaredHash = ctx.Request.Headers[BackupRequestAuthentication.ContentHashHeader]?.Trim() ?? "";
            string signature = ctx.Request.Headers[BackupRequestAuthentication.SignatureHeader]?.Trim() ?? "";
            missingKey = deviceId.Length == 0 || timestampText.Length == 0 || nonce.Length == 0
                || declaredHash.Length == 0 || signature.Length == 0;
            long timestamp = 0;
            if (missingKey || !long.TryParse(timestampText, out timestamp)
                || !BackupRequestAuthentication.IsFresh(timestamp, DateTimeOffset.UtcNow)
                || nonce.Length is < 16 or > 128)
            {
                return false;
            }

            int maxBytes = string.Equals(ctx.Request.HttpMethod, "PUT", StringComparison.OrdinalIgnoreCase)
                ? MobileBackupService.ChunkSizeBytes
                : MaxJsonBodyBytes;
            byte[] body;
            try { body = ReadRequestBytesCore(ctx, maxBytes); }
            catch { return false; }
            string actualHash = BackupRequestAuthentication.ComputeContentHash(body);
            if (!BackupRequestAuthentication.FixedTimeEquals(actualHash, declaredHash))
                return false;

            string expected = BackupRequestAuthentication.CreateRequestSignature(
                credential,
                ctx.Request.HttpMethod,
                ctx.Request.Url?.PathAndQuery ?? "/",
                timestamp,
                nonce,
                actualHash,
                deviceId);
            if (!BackupRequestAuthentication.FixedTimeEquals(expected, signature))
                return false;

            string replayKey = $"{deviceId.ToLowerInvariant()}:{nonce}";
            if (!_backupRequestNonces.TryAdd(replayKey, timestamp))
                return false;
            long cutoff = DateTimeOffset.UtcNow.Subtract(
                BackupRequestAuthentication.AllowedClockSkew).ToUnixTimeSeconds();
            if (_backupRequestNonces.Count > 4096)
            {
                foreach ((string key, long value) in _backupRequestNonces)
                    if (value < cutoff) _backupRequestNonces.TryRemove(key, out _);
            }

            _authenticatedRequestBodies[ctx] = body;
            _authenticatedDeviceKeys[ctx] = credential;
            _authenticatedDeviceIds[ctx] = deviceId;
            _authenticatedDeviceKinds[ctx] = NormalizeDeviceKind(deviceKind);
            RegisterAuthorizedBackupClient(ctx, deviceId);
            return true;
        }

        private void RegisterAuthorizedBackupClient(HttpListenerContext ctx, string deviceId)
        {
            IPAddress remoteAddress = ctx.Request.RemoteEndPoint?.Address;
            string deviceName = ctx.Request.Headers["X-EPM-Device-Name"];
            try { deviceName = Uri.UnescapeDataString(deviceName ?? ""); } catch { }
            _mobileOrderReceivers.Register(
                remoteAddress,
                deviceId,
                deviceName,
                MobileOrderReceiverRegistry.OrderReceiverPort,
                [PackingProofCapabilities.Recording, PackingProofCapabilities.OrderReceiver]);
        }

        private static string NormalizeDeviceKind(string deviceKind) =>
            string.Equals(deviceKind, "pc", StringComparison.OrdinalIgnoreCase)
                ? "pc"
                : string.Equals(deviceKind, "viewer", StringComparison.OrdinalIgnoreCase)
                    ? "viewer"
                    : "mobile";

        private static bool IsMobileBackupUploadPath(string path, string suffix, out string uploadId)
        {
            uploadId = "";
            const string prefix = "/api/mobile-backup/uploads/";
            if (path == null
                || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int length = path.Length - prefix.Length - suffix.Length;
            if (length <= 0) return false;
            uploadId = path.Substring(prefix.Length, length).Trim('/');
            return uploadId.Length > 0 && !uploadId.Contains('/');
        }

        private static bool TryParseMobileBackupAttestationPath(string path, out long recordId)
        {
            recordId = 0;
            const string prefix = "/api/mobile-backup/records/";
            const string suffix = "/attestation";
            if (path == null || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return false;
            string value = path[prefix.Length..^suffix.Length].Trim('/');
            return long.TryParse(value, out recordId) && recordId > 0;
        }

        private static bool TryParseDeviceScopedVideoPath(string path, string suffix, out long recordId)
        {
            recordId = 0;
            const string prefix = "/api/mobile-backup/videos/";
            if (path == null || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return false;
            string value = path[prefix.Length..^suffix.Length].Trim('/');
            return long.TryParse(value, out recordId) && recordId > 0;
        }

        private void HandleBackupDeviceEnrollment(HttpListenerContext ctx)
        {
            if (!PackingProofCapabilities.ForPreset(_deploymentPreset).Contains(
                    PackingProofCapabilities.MobileBackup,
                    StringComparer.OrdinalIgnoreCase))
            {
                SendJson(ctx, 409, new { errorCode = "not_backup_host", error = "这台电脑当前不是录像文件备份主机" });
                return;
            }
            string remoteAddress = ctx.Request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
            BackupDeviceEnrollmentRequest request;
            try { request = ReadJsonBody<BackupDeviceEnrollmentRequest>(ctx); }
            catch
            {
                SendJson(ctx, 400, new { errorCode = "invalid_enrollment", error = "设备连接信息无效" });
                return;
            }
            string deviceId = request.DeviceId?.Trim() ?? "";
            string deviceKind = NormalizeDeviceKind(request.DeviceKind);
            request.DeviceId = deviceId;
            request.DeviceKind = deviceKind;
            request.DeviceName = (request.DeviceName ?? "").Trim();
            request.Platform = (request.Platform ?? "").Trim();
            request.RemoteAddress = remoteAddress;
            if (deviceId.Length is < 8 or > 128)
            {
                SendJson(ctx, 400, new { errorCode = "invalid_device_id", error = "设备身份无效" });
                return;
            }
            var compatibilityFailure = BackupCompatibilityPolicy.ValidateClient(request);
            if (compatibilityFailure != null)
            {
                SendJson(ctx, 426, new
                {
                    errorCode = "backup_client_upgrade_required",
                    error = compatibilityFailure.Message,
                    updateTarget = compatibilityFailure.UpdateTarget,
                    minimumVersion = compatibilityFailure.MinimumVersion,
                    minimumBuildNumber = compatibilityFailure.MinimumBuildNumber,
                    downloadUrl = compatibilityFailure.DownloadUrl,
                    protocol = BackupCompatibilityPolicy.BackupProtocol,
                    enrollmentVersion = BackupCompatibilityPolicy.EnrollmentVersion,
                    authVersion = BackupCompatibilityPolicy.AuthenticationVersion
                });
                return;
            }
            string pendingKey = $"{deviceKind}:{deviceId.ToLowerInvariant()}:{remoteAddress.ToLowerInvariant()}";
            Lazy<BackupDeviceEnrollmentOperation> pending = null;
            BackupDeviceEnrollmentOperation operation = null;
            lock (_backupEnrollmentApprovalLock)
            {
                if (_activeBackupEnrollment == null)
                {
                    if (string.Equals(
                            _recentBackupEnrollmentKey,
                            pendingKey,
                            StringComparison.OrdinalIgnoreCase)
                        && _recentBackupEnrollment != null
                        && DateTimeOffset.UtcNow <= _recentBackupEnrollmentExpiresAtUtc)
                    {
                        operation = _recentBackupEnrollment;
                    }
                    else
                    {
                        ClearRecentBackupEnrollment();
                        _activeBackupEnrollmentKey = pendingKey;
                        _activeBackupEnrollment = new Lazy<BackupDeviceEnrollmentOperation>(
                            () => ProcessBackupDeviceEnrollment(request),
                            LazyThreadSafetyMode.ExecutionAndPublication);
                    }
                }
                else if (!string.Equals(
                             _activeBackupEnrollmentKey,
                             pendingKey,
                             StringComparison.OrdinalIgnoreCase))
                {
                    RuntimeLog.Info(
                        "BackupEnrollment",
                        $"Connection deferred while another approval is active deviceKind={deviceKind}, deviceId={SafeDeviceId(deviceId)}, remote={remoteAddress}");
                    ctx.Response.Headers["Retry-After"] = "3";
                    SendJson(ctx, 429, new
                    {
                        errorCode = "enrollment_approval_busy",
                        error = "保存主机正在确认另一台设备，请稍后自动重试",
                        retryAfterSeconds = 3
                    });
                    return;
                }

                pending = _activeBackupEnrollment;
            }
            if (operation == null)
            {
                try
                {
                    operation = pending.Value;
                }
                finally
                {
                    lock (_backupEnrollmentApprovalLock)
                    {
                        if (ReferenceEquals(_activeBackupEnrollment, pending))
                        {
                            if (operation?.Decision == BackupDeviceEnrollmentApprovalDecision.Approved
                                && operation.Enrollment != null)
                            {
                                _recentBackupEnrollmentKey = pendingKey;
                                _recentBackupEnrollment = operation;
                                _recentBackupEnrollmentExpiresAtUtc =
                                    DateTimeOffset.UtcNow + BackupEnrollmentRetryReuseWindow;
                            }
                            _activeBackupEnrollment = null;
                            _activeBackupEnrollmentKey = null;
                        }
                    }
                }
            }
            if (operation.Decision == BackupDeviceEnrollmentApprovalDecision.Denied)
            {
                RuntimeLog.Info("BackupEnrollment", $"Connection denied deviceKind={deviceKind}, deviceId={SafeDeviceId(deviceId)}, remote={remoteAddress}");
                SendJson(ctx, 403, new { errorCode = "enrollment_denied", error = "保存主机已拒绝本次连接" });
                return;
            }
            if (operation.Decision != BackupDeviceEnrollmentApprovalDecision.Approved
                || operation.Enrollment == null)
            {
                RuntimeLog.Warn("BackupEnrollment", $"Approval unavailable deviceKind={deviceKind}, deviceId={SafeDeviceId(deviceId)}, remote={remoteAddress}");
                SendJson(ctx, 503, new
                {
                    errorCode = "enrollment_approval_unavailable",
                    error = "电脑端暂时无法显示连接确认窗口，请打开保存主机界面后重试"
                });
                return;
            }
            BackupDeviceEnrollment enrollment = operation.Enrollment;
            MobileOrderReceiverInfo registeredDevice = null;
            // 查看端只消费网页回放，不参与订单播报接收，不注册为订单接收设备。
            if (!string.Equals(deviceKind, "viewer", StringComparison.OrdinalIgnoreCase))
            {
                registeredDevice = _mobileOrderReceivers.Register(
                    ctx.Request.RemoteEndPoint?.Address,
                    enrollment.DeviceId,
                    request.DeviceName);
            }
            string assignedDeviceName = registeredDevice?.NodeName ?? request.DeviceName;
            string webAccessUrl = ResolveWebAccessUrl();
            SendJson(ctx, 200, new
            {
                protocol = MobileBackupService.ProtocolVersion,
                version = 2,
                authVersion = BackupRequestAuthentication.CurrentVersion,
                computerId = _nodeId,
                computerName = _nodeName,
                deviceId = enrollment.DeviceId,
                deviceToken = enrollment.DeviceCredential,
                deviceName = assignedDeviceName,
                issuedAt = enrollment.IssuedAt,
                hostVersion = BackupCompatibilityPolicy.CreateHostInfo().HostVersion,
                webAccessUrl = webAccessUrl
            });
        }

        /// <summary>
        /// 与“连接手机/电脑”使用同一链接：受保护时带 ?key=，未保护时为裸地址；
        /// 提供者不可用时返回 null，由客户端按受保护状态处理，绝不降级为无认证打开。
        /// </summary>
        private string ResolveWebAccessUrl()
        {
            try
            {
                string url = _mobileConnectionUrlProvider()?.Trim() ?? "";
                Uri parsed = null;
                return Uri.TryCreate(url, UriKind.Absolute, out parsed)
                    && (string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    ? url
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private void ClearRecentBackupEnrollment()
        {
            _recentBackupEnrollmentKey = null;
            _recentBackupEnrollment = null;
            _recentBackupEnrollmentExpiresAtUtc = default;
        }

        private BackupDeviceEnrollmentOperation ProcessBackupDeviceEnrollment(
            BackupDeviceEnrollmentRequest request)
        {
            string deviceKind = request.DeviceKind;
            string deviceId = request.DeviceId;
            RuntimeLog.Info(
                "BackupEnrollment",
                $"Connection requested deviceKind={deviceKind}, deviceId={SafeDeviceId(deviceId)}, remote={request.RemoteAddress}");
            if (_backupDeviceEnrollmentApprover == null)
                return new BackupDeviceEnrollmentOperation(BackupDeviceEnrollmentApprovalDecision.Unavailable);

            try
            {
                BackupDeviceEnrollmentApprovalDecision decision = _backupDeviceEnrollmentApprover(request);
                if (decision != BackupDeviceEnrollmentApprovalDecision.Approved)
                    return new BackupDeviceEnrollmentOperation(decision);

                BackupDeviceEnrollment enrollment = _backupPairingTokens.Enroll(deviceId, deviceKind);
                RuntimeLog.Info(
                    "BackupEnrollment",
                    $"Connection approved deviceKind={deviceKind}, deviceId={SafeDeviceId(deviceId)}, remote={request.RemoteAddress}");
                return new BackupDeviceEnrollmentOperation(decision, enrollment);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(
                    "BackupEnrollment",
                    $"Approval failed deviceKind={deviceKind}, deviceId={SafeDeviceId(deviceId)}, remote={request.RemoteAddress}",
                    ex);
                return new BackupDeviceEnrollmentOperation(BackupDeviceEnrollmentApprovalDecision.Unavailable);
            }
        }

        private void BeginActiveRequest()
        {
            if (Interlocked.Increment(ref _activeRequests) == 1)
                _requestsIdle.Reset();
        }

        private void EndActiveRequest()
        {
            if (Interlocked.Decrement(ref _activeRequests) == 0)
                _requestsIdle.Set();
        }

        private static string SafeDeviceId(string deviceId) =>
            deviceId.Length <= 8 ? deviceId : $"...{deviceId[^8..]}";

        private void HandleMobileBackupCapabilities(HttpListenerContext ctx)
        {
            string deviceId = ctx.Request.Headers["X-EPM-Device-Id"];
            string deviceName = ctx.Request.Headers["X-EPM-Device-Name"];
            try { deviceName = Uri.UnescapeDataString(deviceName ?? ""); } catch { }
            MobileOrderReceiverInfo registeredDevice = _mobileOrderReceivers.Register(
                ctx.Request.RemoteEndPoint?.Address,
                deviceId,
                deviceName);
            SendJson(ctx, 200, new
            {
                protocol = MobileBackupService.ProtocolVersion,
                version = 2,
                authVersion = BackupRequestAuthentication.CurrentVersion,
                computerId = _mobileBackupComputerId,
                computerName = _mobileBackupComputerName,
                deviceName = registeredDevice?.NodeName ?? deviceName,
                maxChunkBytes = MobileBackupService.ChunkSizeBytes,
                supportedFormats = new[] { "video/mp4" },
                    features = new
                    {
                    videoLibrary = true,
                    cursorVideoLibrary = true,
                    rangePlayback = true,
                    multipleSessionsPerFile = false,
                    libraryScope = "host",
                    deviceVideoClipping = true,
                    uploadVideoCodec = true
                },
                retryPolicy = new
                {
                    chunkMaxAttempts = 5,
                    chunkBackoffSeconds = new[] { 1, 2, 4, 8, 16 },
                    fileMaxAttempts = 3
                }
            });
        }

        private void HandleConnectionHeartbeat(HttpListenerContext ctx)
        {
            try
            {
                ConnectedClientHeartbeat heartbeat = ReadJsonBody<ConnectedClientHeartbeat>(ctx);
                if (_authenticatedDeviceIds.TryGetValue(ctx, out string authenticatedDeviceId))
                {
                    heartbeat.ClientId = authenticatedDeviceId;
                    heartbeat.NodeId = authenticatedDeviceId;
                }
                string remoteAddress = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";
                string assignedDisplayName = "";
                if (string.Equals(heartbeat.ClientType, "recording-workstation", StringComparison.OrdinalIgnoreCase))
                {
                    assignedDisplayName = _recordingComputerNicknames.Assign(
                        heartbeat.NodeId,
                        heartbeat.DisplayName,
                        heartbeat.NicknameCustomized);
                    heartbeat.DisplayName = assignedDisplayName;
                }
                if (heartbeat.Connected != false
                    && string.Equals(heartbeat.ClientType, "mobile-app", StringComparison.OrdinalIgnoreCase)
                    && IPAddress.TryParse(remoteAddress, out IPAddress mobileAddress))
                {
                    MobileOrderReceiverInfo registeredDevice = _mobileOrderReceivers.Register(
                        mobileAddress,
                        heartbeat.NodeId,
                        heartbeat.DisplayName,
                        heartbeat.OrderReceiverPort,
                        heartbeat.Capabilities);
                    assignedDisplayName = registeredDevice?.NodeName
                        ?? heartbeat.DisplayName?.Trim()
                        ?? "";
                    heartbeat.DisplayName = assignedDisplayName;
                }
                _connectedClients.Heartbeat(heartbeat, remoteAddress);
                _mobileAppUpdatePolicy.RefreshInBackground();
                NotifyMobileAppUpdateIfNeeded(heartbeat);
                MobileAppUpdatePolicy updatePolicy = MobileAppUpdatePolicyProvider.MinimumPolicy;
                MobileAppReleaseInfo latestRelease = _mobileAppUpdatePolicy.LatestRelease;
                SendJson(ctx, 200, new
                {
                    ok = true,
                    assignedDisplayName = assignedDisplayName.Length > 0 ? assignedDisplayName : null,
                    heartbeatIntervalSeconds = ConnectedClientRegistry.HeartbeatIntervalSeconds,
                    expiresInSeconds = ConnectedClientRegistry.ExpirationSeconds,
                    mobileAppUpdate = new
                    {
                        schemaVersion = updatePolicy.SchemaVersion,
                        minimumVersion = updatePolicy.MinimumVersion,
                        minimumBuildNumber = updatePolicy.MinimumBuildNumber,
                        message = updatePolicy.Message,
                        latestVersion = latestRelease?.Version ?? "",
                        latestBuildNumber = latestRelease?.BuildNumber ?? 0,
                        latestTag = latestRelease?.TagName ?? "",
                        downloadUrl = MobileAppUpdatePolicyProvider.ReleasesUrl
                    }
                });
            }
            catch (ConnectedClientValidationException ex)
            {
                int statusCode = ex.ErrorCode is "connection_registry_full" or "too_many_clients" ? 429 : 400;
                SendJson(ctx, statusCode, new { errorCode = ex.ErrorCode, error = ex.Message });
            }
            catch (JsonException ex)
            {
                SendJson(ctx, 400, new { errorCode = "invalid_json", error = ex.Message });
            }
        }

        internal IReadOnlyList<ConnectedClientInfo> GetConnectedClients() => _connectedClients.GetSnapshot();

        internal IReadOnlyList<OrderIntegrationDeviceStatus> GetOrderIntegrationDeviceStatuses()
        {
            IReadOnlyDictionary<string, OrderIntegrationActivity> activities =
                _orderIntegrationActivities.GetSnapshot();
            var statuses = GetRecordingDevices("", includeKnown: true)
                .Select(device =>
                {
                    activities.TryGetValue(device.NodeId, out OrderIntegrationActivity activity);
                    return new OrderIntegrationDeviceStatus(
                        device.NodeId,
                        device.NodeName,
                        device.DeviceType,
                        device.Online,
                        activity?.LastActivityUtc,
                        activity?.ReceivedCount ?? 0);
                })
                .ToList();
            HashSet<string> knownNodeIds = statuses
                .Select(device => device.NodeId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> onlineNodeIds = _connectedClients.GetSnapshot()
                .Where(client => string.Equals(client.ClientType, "recording-workstation", StringComparison.OrdinalIgnoreCase))
                .Select(client => client.NodeId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (RecordingComputerNicknameInfo computer in _recordingComputerNicknames.GetKnown())
            {
                if (!knownNodeIds.Add(computer.NodeId)) continue;
                activities.TryGetValue(computer.NodeId, out OrderIntegrationActivity activity);
                statuses.Add(new OrderIntegrationDeviceStatus(
                    computer.NodeId,
                    computer.DisplayName,
                    "pc",
                    onlineNodeIds.Contains(computer.NodeId),
                    activity?.LastActivityUtc,
                    activity?.ReceivedCount ?? 0));
            }
            return statuses
                .OrderByDescending(device => device.Online)
                .ThenBy(device => device.DisplayName, StringComparer.CurrentCulture)
                .ToArray();
        }

        private void NotifyMobileAppUpdateIfNeeded(ConnectedClientHeartbeat heartbeat)
        {
            if (!ShouldNotifyUnknownMobileVersion(heartbeat))
                return;

            MobileAppReleaseInfo latest = _mobileAppUpdatePolicy.LatestRelease
                ?? new MobileAppReleaseInfo(
                    "",
                    MobileAppUpdatePolicyProvider.MinimumPolicy.MinimumVersion,
                    MobileAppUpdatePolicyProvider.MinimumPolicy.MinimumBuildNumber,
                    MobileAppUpdatePolicyProvider.ReleasesUrl);

            string nodeId = string.IsNullOrWhiteSpace(heartbeat.NodeId)
                ? heartbeat.ClientId.Trim()
                : heartbeat.NodeId.Trim();
            string notificationKey = $"{nodeId}:unknown-version";
            if (!_notifiedMobileAppUpdates.TryAdd(notificationKey, 0))
                return;

            var update = new MobileAppUpdateAvailableInfo(
                heartbeat.DisplayName?.Trim() ?? "",
                heartbeat.AppVersion?.Trim() ?? "",
                heartbeat.AppBuildNumber.GetValueOrDefault(),
                latest);
            try { MobileAppUpdateAvailable?.Invoke(update); } catch { }
        }

        internal static bool ShouldNotifyUnknownMobileVersion(ConnectedClientHeartbeat heartbeat)
        {
            if (!string.Equals(heartbeat?.ClientType, "mobile-app", StringComparison.OrdinalIgnoreCase)
                || heartbeat.Connected == false)
                return false;

            return string.IsNullOrWhiteSpace(heartbeat.AppVersion)
                || heartbeat.AppBuildNumber.GetValueOrDefault() <= 0
                || !Version.TryParse(heartbeat.AppVersion.Trim(), out _);
        }

        private void HandleCreateMobileBackupUpload(HttpListenerContext ctx)
        {
            try
            {
                MobileBackupCreateRequest request = ReadJsonBody<MobileBackupCreateRequest>(ctx);
                MobileBackupCreateResult result = _mobileBackupService.CreateOrResume(request);
                SendJson(ctx, 200, new
                {
                    uploadId = result.UploadId,
                    offset = result.Offset,
                    chunkSize = result.ChunkSize,
                    fileReady = result.FileReady
                });
            }
            catch (Exception ex)
            {
                SendMobileBackupError(ctx, ex);
            }
        }

        private void HandleMobileBackupChunk(HttpListenerContext ctx, string uploadId)
        {
            try
            {
                if (!TryParseContentRange(ctx.Request.Headers["Content-Range"], out long start, out long end, out long total))
                    throw new MobileBackupValidationException("invalid_content_range", "Content-Range 格式应为 bytes start-end/total");
                string chunkSha256 = ctx.Request.Headers["X-Chunk-SHA256"] ?? "";
                byte[] content = ReadRequestBytes(ctx, MobileBackupService.ChunkSizeBytes);
                long offset = _mobileBackupService.AppendChunk(uploadId, start, end, total, content, chunkSha256);
                SendJson(ctx, 200, new { uploadId, offset });
            }
            catch (Exception ex)
            {
                SendMobileBackupError(ctx, ex);
            }
        }

        private void HandleCompleteMobileBackupUpload(HttpListenerContext ctx, string uploadId)
        {
            try
            {
                MobileBackupCompleteRequest request = ReadJsonBody<MobileBackupCompleteRequest>(ctx);
                if (!_authenticatedDeviceIds.TryGetValue(ctx, out string authenticatedDeviceId))
                {
                    SendJson(ctx, 403, new { errorCode = "device_identity_required", error = "设备身份验证失败，请重新连接" });
                    return;
                }
                request.SourceDeviceId = authenticatedDeviceId;
                _mobileOrderReceivers.Register(
                    ctx.Request.RemoteEndPoint?.Address,
                    request.SourceDeviceId,
                    request.SourceDeviceName);
                MobileBackupCompleteResult result = _mobileBackupService.Complete(uploadId, request);
                try
                {
                    MobileBackupCompleted?.Invoke(
                        request.SourceDeviceId?.Trim() ?? "",
                        request.SourceDeviceName?.Trim() ?? "");
                }
                catch { }
                int authVersion = 0;
                long verifiedAtUnixSeconds = 0;
                long fileSizeBytes = 0;
                string receiptSignature = "";
                string receiptSessionId = request.Sessions[0].Id;
                if (_authenticatedDeviceKeys.TryGetValue(ctx, out string deviceCredential))
                {
                    VideoRecord verifiedRecord = _db.GetVideoById(result.RecordId);
                    fileSizeBytes = verifiedRecord?.FileSizeBytes ?? 0;
                    string resolvedVerifiedPath = verifiedRecord == null
                        ? ""
                        : PlaybackFileResolver.ResolvePlaybackPath(verifiedRecord);
                    if (fileSizeBytes <= 0 && !string.IsNullOrWhiteSpace(resolvedVerifiedPath))
                        fileSizeBytes = new FileInfo(resolvedVerifiedPath).Length;
                    verifiedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    authVersion = BackupRequestAuthentication.CurrentVersion;
                    receiptSignature = BackupRequestAuthentication.CreateReceiptSignature(
                        deviceCredential,
                        _nodeId,
                        request.SourceDeviceId,
                        receiptSessionId,
                        result.FileSha256,
                        fileSizeBytes,
                        result.RecordId,
                        verifiedAtUnixSeconds);
                }
                SendJson(ctx, 200, new
                {
                    status = result.Status,
                    fileSha256 = result.FileSha256,
                    recordId = result.RecordId,
                    alreadyCompleted = result.AlreadyCompleted,
                    authVersion,
                    hostNodeId = authVersion > 0 ? _nodeId : null,
                    sourceDeviceId = authVersion > 0 ? request.SourceDeviceId : null,
                    sourceSessionId = authVersion > 0 ? receiptSessionId : null,
                    fileSizeBytes,
                    verifiedAtUnixSeconds,
                    receiptSignature = authVersion > 0 ? receiptSignature : null,
                    message = "电脑校验完成，备份成功"
                });
            }
            catch (Exception ex)
            {
                SendMobileBackupError(ctx, ex);
            }
        }

        private void HandleMobileBackupAttestation(HttpListenerContext ctx, long recordId)
        {
            if (!_authenticatedDeviceKeys.TryGetValue(ctx, out string deviceCredential))
            {
                SendJson(ctx, 403, new { errorCode = "signed_auth_required", error = "需要重新扫码以启用安全清理" });
                return;
            }
            VideoRecord record = _db.GetVideoById(recordId);
            string sourceDeviceId = ctx.Request.Headers["X-EPM-Device-Id"]?.Trim() ?? "";
            string resolvedAttestationPath = record == null
                ? ""
                : PlaybackFileResolver.ResolvePlaybackPath(record);
            if (record == null || record.IsDeleted || string.IsNullOrWhiteSpace(resolvedAttestationPath)
                || !string.Equals(record.SourceDeviceId, sourceDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                SendJson(ctx, 404, new { errorCode = "verified_record_missing", error = "保存主机未找到完整录像" });
                return;
            }
            long fileSizeBytes = new FileInfo(resolvedAttestationPath).Length;
            if (fileSizeBytes <= 0 || string.IsNullOrWhiteSpace(record.ContentSha256))
            {
                SendJson(ctx, 409, new { errorCode = "verified_record_invalid", error = "保存主机录像尚未完成校验" });
                return;
            }
            long verifiedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string receiptSignature = BackupRequestAuthentication.CreateReceiptSignature(
                deviceCredential,
                _nodeId,
                sourceDeviceId,
                record.SourceSessionId,
                record.ContentSha256,
                fileSizeBytes,
                record.Id,
                verifiedAtUnixSeconds);
            SendJson(ctx, 200, new
            {
                status = "verified",
                fileSha256 = record.ContentSha256,
                recordId = record.Id,
                authVersion = BackupRequestAuthentication.CurrentVersion,
                hostNodeId = _nodeId,
                sourceDeviceId,
                sourceSessionId = record.SourceSessionId,
                fileSizeBytes,
                verifiedAtUnixSeconds,
                receiptSignature
            });
        }

        private static void SendMobileBackupError(HttpListenerContext ctx, Exception exception)
        {
            switch (exception)
            {
                case MobileBackupOffsetException offset:
                    SendJson(ctx, 409, new
                    {
                        errorCode = "offset_mismatch",
                        error = offset.Message,
                        expectedOffset = offset.ExpectedOffset
                    });
                    break;
                case MobileBackupFileHashException fileHash:
                    SendJson(ctx, 422, new
                    {
                        errorCode = "sha256_mismatch",
                        error = fileHash.Message,
                        expectedOffset = 0,
                        retryWholeFile = true,
                        maxFileAttempts = 3
                    });
                    break;
                case MobileBackupValidationException validation:
                    int statusCode = validation.ErrorCode == "upload_not_found" ? 404
                        : validation.ErrorCode.Contains("sha256_mismatch", StringComparison.Ordinal) ? 422
                        : 400;
                    SendJson(ctx, statusCode, new { errorCode = validation.ErrorCode, error = validation.Message });
                    break;
                case JsonException json:
                    SendJson(ctx, 400, new { errorCode = "invalid_json", error = json.Message });
                    break;
                case InvalidDataException invalidData:
                    SendJson(ctx, 400, new { errorCode = "invalid_request", error = invalidData.Message });
                    break;
                default:
                    SendJson(ctx, 500, new { errorCode = "mobile_backup_failed", error = exception.Message });
                    break;
            }
        }

        private void HandleDeviceScopedVideos(HttpListenerContext ctx)
        {
            if (!TryGetAuthenticatedDevicePrincipal(ctx, out string deviceId, out string deviceKind)) return;
            bool hostLibrary = IsMobileDevice(deviceKind);
            var qs = ctx.Request.QueryString;
            int page = int.TryParse(qs["page"], out int parsedPage) ? Math.Max(1, parsedPage) : 1;
            int pageSize = int.TryParse(qs["size"], out int parsedSize) ? Math.Clamp(parsedSize, 1, 100) : 50;
            string keyword = qs["keyword"] ?? "";
            var result = _db.QueryVideosPaged(
                null,
                null,
                string.IsNullOrWhiteSpace(keyword) ? null : keyword,
                page,
                pageSize,
                includeDeleted: !string.IsNullOrWhiteSpace(keyword),
                sourceType: hostLibrary ? "" : "external",
                deviceId: hostLibrary ? "" : deviceId);
            int deviceTotal = _db.QueryVideosPaged(
                null,
                null,
                null,
                1,
                1,
                sourceType: "external",
                deviceId: deviceId).Total;
            var data = result.Records.Select(record =>
            {
                string ticket = CreateDeviceVideoTicket(deviceId, deviceKind, record.Id);
                return new
                {
                    record.Id,
                    record.OrderId,
                    trackingNumber = record.TrackingNumber ?? "",
                    record.Mode,
                    record.FileName,
                    videoCodec = record.VideoCodec ?? "",
                    sourceType = record.SourceType ?? "pc",
                    sourceDeviceId = record.SourceDeviceId ?? "",
                    sourceDeviceName = ResolveVideoSourceDisplayName(
                        record.SourceType,
                        record.SourceDeviceId,
                        record.SourceDeviceName,
                        record.SourceDeviceKind,
                        _nodeName),
                    sourceDeviceKind = record.SourceDeviceKind ?? "",
                    sourceSessionId = record.SourceSessionId ?? "",
                    contentSha256 = record.ContentSha256 ?? "",
                    sizeMB = Math.Round(record.FileSizeBytes / 1048576.0, 1),
                    startTime = record.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    durationSec = Math.Round(record.DurationSeconds, 0),
                    duration = TimeSpan.FromSeconds(record.DurationSeconds).ToString(@"mm\:ss"),
                    status = record.IsDeleted ? "deleted" : (string.IsNullOrWhiteSpace(PlaybackFileResolver.ResolvePlaybackPath(record)) ? "missing" : "available"),
                    statusReason = record.IsDeleted
                        ? (string.IsNullOrWhiteSpace(record.DeleteReason) ? "已清理" : record.DeleteReason)
                        : (string.IsNullOrWhiteSpace(PlaybackFileResolver.ResolvePlaybackPath(record)) ? "监控端未在记录的文件位置找到录像，可能已被移动、删除或清理" : ""),
                    exists = !record.IsDeleted && !string.IsNullOrWhiteSpace(PlaybackFileResolver.ResolvePlaybackPath(record)),
                    playUrl = $"/api/mobile-backup/videos/{record.Id}/play?ticket={ticket}",
                    thumbnailUrl = $"/api/mobile-backup/videos/{record.Id}/thumbnail?ticket={ticket}",
                    remote = true
                };
            });
            SendJson(ctx, 200, new { total = result.Total, deviceTotal, page, pageSize, data });
        }

        private void HandleDeviceScopedVideoStatuses(HttpListenerContext ctx)
        {
            if (!TryGetAuthenticatedDevicePrincipal(ctx, out string deviceId, out string deviceKind)) return;
            bool hostLibrary = IsMobileDevice(deviceKind);
            long[] ids = (ctx.Request.QueryString["ids"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => long.TryParse(value, out long id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .Take(100)
                .ToArray();
            var data = ids.Select(id =>
            {
                VideoRecord record = _db.GetVideoById(id);
                bool authorized = CanAccessDeviceVideo(record, deviceId, hostLibrary, includeDeleted: true);
                bool exists = authorized && !record!.IsDeleted
                    && !string.IsNullOrWhiteSpace(PlaybackFileResolver.ResolvePlaybackPath(record));
                string status = !authorized || (!record!.IsDeleted && !exists)
                    ? "missing"
                    : record.IsDeleted ? "deleted" : "available";
                string reason = !authorized ? "记录不存在"
                    : record!.IsDeleted ? (string.IsNullOrWhiteSpace(record.DeleteReason) ? "已清理" : record.DeleteReason)
                    : exists ? "" : "文件缺失";
                return new { id, status, exists, reason };
            });
            SendJson(ctx, 200, new { data });
        }

        private void HandleDeviceScopedVideo(HttpListenerContext ctx, long recordId, string operation)
        {
            string deviceId;
            string deviceKind;
            if (!TryGetDeviceVideoTicket(ctx, recordId, out deviceId, out deviceKind)
                && !TryGetAuthenticatedDevicePrincipal(ctx, out deviceId, out deviceKind)) return;
            VideoRecord record = _db.GetVideoById(recordId);
            if (!CanAccessDeviceVideo(record, deviceId, IsMobileDevice(deviceKind)))
            {
                SendJson(ctx, 404, new { errorCode = "video_not_found", error = "未找到可访问的录像" });
                return;
            }
            string syntheticPath = $"/api/videos/{recordId}/{operation}";
            if (operation == "play") HandlePlay(ctx, syntheticPath);
            else if (operation == "download") HandleDownload(ctx, syntheticPath);
            else HandleVideoThumbnail(ctx, syntheticPath);
        }

        private void HandleDeviceClipTimeline(HttpListenerContext ctx, long recordId)
        {
            if (!TryGetMobileLibraryPrincipal(ctx, recordId, out string deviceId)) return;
            try
            {
                ClipRangeRequest request = ReadJsonBody<ClipRangeRequest>(ctx);
                ClipTimelineResult result = request.FrameIndex >= 0
                    ? _clipService.CreateTimelinePreviewFrame(recordId, request.FrameCount, request.FrameIndex)
                    : _clipService.CreateTimelinePreviews(recordId, request.FrameCount);
                foreach (ClipTimelineFrame frame in result.Frames)
                {
                    string fileName = Path.GetFileName(frame.Url);
                    string ticket = CreateDeviceClipAssetTicket(deviceId, fileName, "preview");
                    frame.Url = $"/api/mobile-backup/clip-previews/{fileName}?ticket={ticket}";
                }
                SendJson(ctx, 200, result);
            }
            catch (Exception ex)
            {
                Log($"HandleDeviceClipTimeline 异常: {ex.Message}");
                SendJson(ctx, 400, new { success = false, error = ex.Message });
            }
        }

        private void HandleStartDeviceClip(HttpListenerContext ctx, long recordId)
        {
            if (!TryGetMobileLibraryPrincipal(ctx, recordId, out string deviceId)) return;
            try
            {
                ClipRangeRequest request = ReadJsonBody<ClipRangeRequest>(ctx);
                string taskId = _clipService.StartClip(recordId, request.StartSeconds, request.EndSeconds);
                RegisterDeviceClipTaskGrant(
                    taskId,
                    new DeviceClipTaskGrant(
                        deviceId,
                        recordId,
                        DateTimeOffset.UtcNow.AddHours(2)));
                SendJson(ctx, 200, new { success = true, taskId });
            }
            catch (Exception ex)
            {
                Log($"HandleStartDeviceClip 异常: {ex.Message}");
                SendJson(ctx, 400, new { success = false, error = ex.Message });
            }
        }

        private void HandleGetDeviceClipTask(HttpListenerContext ctx, string path)
        {
            string taskId = Path.GetFileName(path);
            if (!TryAuthorizeDeviceClipTask(ctx, taskId, out string deviceId)) return;
            ClipTaskSnapshot task = _clipService.GetTask(taskId);
            if (task == null)
            {
                _deviceClipTasks.TryRemove(taskId, out _);
                SendJson(ctx, 404, new { success = false, errorCode = "clip_task_not_found", status = "not_found", message = "剪辑任务不存在", downloadUrl = "" });
                return;
            }
            if (!string.IsNullOrWhiteSpace(task.DownloadUrl))
            {
                string fileName = Path.GetFileName(task.DownloadUrl);
                string ticket = CreateDeviceClipAssetTicket(deviceId, fileName, "clip");
                task.DownloadUrl = $"/api/mobile-backup/clips/{fileName}?ticket={ticket}";
                task.PlayUrl = task.DownloadUrl + "&inline=1";
            }
            SendJson(ctx, 200, task);
        }

        private void HandleCancelDeviceClipTask(HttpListenerContext ctx, string path)
        {
            string taskId = path.Replace("/api/mobile-backup/clip-tasks/", "")
                .Replace("/cancel", "")
                .Trim('/');
            if (!TryAuthorizeDeviceClipTask(ctx, taskId, out _)) return;
            ClipTaskSnapshot task = _clipService.CancelTask(taskId);
            _deviceClipTasks.TryRemove(taskId, out _);
            if (task == null)
            {
                SendJson(ctx, 404, new { success = false, errorCode = "clip_task_not_found", status = "not_found", message = "剪辑任务不存在", downloadUrl = "" });
                return;
            }
            SendJson(ctx, 200, task);
        }

        private void HandleServeDeviceClipPreview(HttpListenerContext ctx, string path)
        {
            string fileName = Path.GetFileName(path);
            if (!TryGetDeviceClipAssetTicket(ctx, fileName, "preview", out _))
            {
                SendJson(ctx, 403, new { errorCode = "clip_ticket_invalid", error = "剪辑预览已过期，请重新打开剪辑" });
                return;
            }
            string filePath = _clipService.ResolvePreviewPath(fileName);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                SendJson(ctx, 404, new { errorCode = "clip_preview_not_found", error = "预览图不存在" });
                return;
            }
            ServeFileWithRange(ctx, filePath, inline: true);
        }

        private void HandleServeDeviceClip(HttpListenerContext ctx, string path)
        {
            string fileName = Path.GetFileName(path);
            if (!TryGetDeviceClipAssetTicket(ctx, fileName, "clip", out _))
            {
                SendJson(ctx, 403, new { errorCode = "clip_ticket_invalid", error = "剪辑文件链接已过期，请重新生成" });
                return;
            }
            string filePath = _clipService.ResolveClipPath(fileName);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                SendJson(ctx, 404, new { errorCode = "clip_file_not_found", error = "剪辑文件不存在" });
                return;
            }
            ServeFileWithRange(ctx, filePath, inline: ShouldServeClipInline(ctx.Request.QueryString["inline"]));
        }

        private bool TryGetMobileLibraryPrincipal(
            HttpListenerContext ctx,
            long recordId,
            out string deviceId)
        {
            deviceId = "";
            if (!TryGetAuthenticatedDevicePrincipal(ctx, out string authenticatedDeviceId, out string deviceKind))
                return false;
            if (!IsMobileDevice(deviceKind))
            {
                SendJson(ctx, 403, new { errorCode = "device_library_forbidden", error = "录制工位只能访问本设备录像" });
                return false;
            }
            if (!CanAccessDeviceVideo(_db.GetVideoById(recordId), authenticatedDeviceId, hostLibrary: true))
            {
                SendJson(ctx, 404, new { errorCode = "video_not_found", error = "未找到可访问的录像" });
                return false;
            }
            deviceId = authenticatedDeviceId;
            return true;
        }

        private bool TryAuthorizeDeviceClipTask(
            HttpListenerContext ctx,
            string taskId,
            out string deviceId)
        {
            deviceId = "";
            if (!TryGetAuthenticatedDevicePrincipal(ctx, out string authenticatedDeviceId, out string deviceKind))
                return false;
            _deviceClipTasks.TryGetValue(taskId, out DeviceClipTaskGrant grant);
            if (!IsMobileDevice(deviceKind)
                || grant == null
                || grant.ExpiresAt <= DateTimeOffset.UtcNow
                || !string.Equals(grant.DeviceId, authenticatedDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                if (grant != null && grant.ExpiresAt <= DateTimeOffset.UtcNow)
                    _deviceClipTasks.TryRemove(taskId, out _);
                SendJson(ctx, 404, new { errorCode = "clip_task_not_found", error = "剪辑任务不存在" });
                return false;
            }
            deviceId = authenticatedDeviceId;
            return true;
        }

        private string CreateDeviceClipAssetTicket(string deviceId, string fileName, string assetKind)
        {
            string value = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            lock (_deviceGrantCleanupLock)
            {
                _deviceClipAssetTickets[value] = new DeviceClipAssetTicket(
                    deviceId,
                    fileName,
                    assetKind,
                    DateTimeOffset.UtcNow.AddMinutes(10));
                TrimExpiredAndOverflow(
                    _deviceClipAssetTickets,
                    DeviceClipAssetTicketLimit,
                    DeviceClipAssetTicketLowWater,
                    ticket => ticket.ExpiresAt);
            }
            return value;
        }

        private bool TryGetDeviceClipAssetTicket(
            HttpListenerContext ctx,
            string fileName,
            string assetKind,
            out string deviceId)
        {
            deviceId = "";
            string value = ctx.Request.QueryString["ticket"] ?? "";
            if (value.Length != 48
                || !_deviceClipAssetTickets.TryGetValue(value, out DeviceClipAssetTicket ticket)
                || ticket.ExpiresAt <= DateTimeOffset.UtcNow
                || !string.Equals(ticket.FileName, fileName, StringComparison.Ordinal)
                || !string.Equals(ticket.AssetKind, assetKind, StringComparison.Ordinal))
            {
                if (value.Length > 0) _deviceClipAssetTickets.TryRemove(value, out _);
                return false;
            }
            deviceId = ticket.DeviceId;
            return true;
        }

        private void RegisterDeviceClipTaskGrant(string taskId, DeviceClipTaskGrant grant)
        {
            lock (_deviceGrantCleanupLock)
            {
                _deviceClipTasks[taskId] = grant;
                TrimExpiredAndOverflow(
                    _deviceClipTasks,
                    DeviceClipTaskLimit,
                    DeviceClipTaskLowWater,
                    ticket => ticket.ExpiresAt);
            }
        }

        private string CreateDeviceVideoTicket(string deviceId, string deviceKind, long recordId)
        {
            string value = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            lock (_deviceGrantCleanupLock)
            {
                _deviceVideoTickets[value] = new DeviceVideoTicket(
                    deviceId,
                    NormalizeDeviceKind(deviceKind),
                    recordId,
                    DateTimeOffset.UtcNow.AddMinutes(10));
                TrimExpiredAndOverflow(
                    _deviceVideoTickets,
                    DeviceVideoTicketLimit,
                    DeviceVideoTicketLowWater,
                    ticket => ticket.ExpiresAt);
            }
            return value;
        }

        internal static int TrimExpiredAndOverflow<T>(
            ConcurrentDictionary<string, T> entries,
            int maximumCount,
            int lowWaterCount,
            Func<T, DateTimeOffset> expiresAtProvider)
        {
            ArgumentNullException.ThrowIfNull(entries);
            ArgumentNullException.ThrowIfNull(expiresAtProvider);
            if (maximumCount <= 0 || lowWaterCount < 0 || lowWaterCount >= maximumCount)
                throw new ArgumentOutOfRangeException(nameof(maximumCount));
            if (entries.Count <= maximumCount)
                return 0;

            int removed = 0;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach ((string key, T value) in entries)
            {
                if (expiresAtProvider(value) <= now && entries.TryRemove(key, out _))
                    removed++;
            }

            int overflow = entries.Count - lowWaterCount;
            if (overflow <= 0)
                return removed;

            foreach ((string key, T _) in entries
                         .OrderBy(pair => expiresAtProvider(pair.Value))
                         .Take(overflow))
            {
                if (entries.TryRemove(key, out _))
                    removed++;
            }
            return removed;
        }

        private bool HasValidDeviceAssetTicket(HttpListenerContext ctx, string path)
        {
            bool ticketPath = TryParseDeviceScopedVideoPath(path, "/play", out long recordId)
                || TryParseDeviceScopedVideoPath(path, "/download", out recordId)
                || TryParseDeviceScopedVideoPath(path, "/thumbnail", out recordId);
            if (ticketPath && TryGetDeviceVideoTicket(ctx, recordId, out _, out _)) return true;
            if (path.StartsWith("/api/mobile-backup/clip-previews/", StringComparison.OrdinalIgnoreCase))
                return TryGetDeviceClipAssetTicket(ctx, Path.GetFileName(path), "preview", out _);
            if (path.StartsWith("/api/mobile-backup/clips/", StringComparison.OrdinalIgnoreCase))
                return TryGetDeviceClipAssetTicket(ctx, Path.GetFileName(path), "clip", out _);
            return false;
        }

        private bool TryGetDeviceVideoTicket(
            HttpListenerContext ctx,
            long recordId,
            out string deviceId,
            out string deviceKind)
        {
            deviceId = "";
            deviceKind = "";
            string value = ctx.Request.QueryString["ticket"] ?? "";
            if (value.Length != 48
                || !_deviceVideoTickets.TryGetValue(value, out DeviceVideoTicket ticket)
                || ticket.RecordId != recordId
                || ticket.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                if (value.Length > 0) _deviceVideoTickets.TryRemove(value, out _);
                return false;
            }
            deviceId = ticket.DeviceId;
            deviceKind = ticket.DeviceKind;
            return true;
        }

        private bool TryGetAuthenticatedDevicePrincipal(
            HttpListenerContext ctx,
            out string deviceId,
            out string deviceKind)
        {
            if (_authenticatedDeviceIds.TryGetValue(ctx, out deviceId!)
                && deviceId.Length > 0
                && _authenticatedDeviceKinds.TryGetValue(ctx, out deviceKind!)
                && deviceKind.Length > 0)
                return true;
            deviceId = "";
            deviceKind = "";
            SendJson(ctx, 403, new { errorCode = "device_identity_required", error = "设备身份验证失败，请重新连接" });
            return false;
        }

        private static bool IsMobileDevice(string deviceKind) =>
            string.Equals(deviceKind, "mobile", StringComparison.OrdinalIgnoreCase);

        private static bool CanAccessDeviceVideo(
            VideoRecord record,
            string deviceId,
            bool hostLibrary,
            bool includeDeleted = false)
        {
            if (record == null || (!includeDeleted && record.IsDeleted)) return false;
            return hostLibrary
                || (string.Equals(record.SourceType, "external", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(record.SourceDeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        }

        private bool TryGetAuthenticatedDeviceId(HttpListenerContext ctx, out string deviceId)
        {
            if (_authenticatedDeviceIds.TryGetValue(ctx, out deviceId!) && deviceId.Length > 0)
                return true;
            deviceId = "";
            SendJson(ctx, 403, new { errorCode = "device_identity_required", error = "设备身份验证失败，请重新连接" });
            return false;
        }

        internal static bool TryParseContentRange(string value, out long start, out long end, out long total)
        {
            start = end = total = 0;
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase))
                return false;
            string[] rangeAndTotal = value[6..].Split('/', 2);
            if (rangeAndTotal.Length != 2) return false;
            string[] bounds = rangeAndTotal[0].Split('-', 2);
            return bounds.Length == 2
                && long.TryParse(bounds[0], out start)
                && long.TryParse(bounds[1], out end)
                && long.TryParse(rangeAndTotal[1], out total)
                && start >= 0 && end >= start && total > end;
        }

        private void HandleMobileConnection(HttpListenerContext ctx)
        {
            string url;
            try
            {
                url = _mobileConnectionUrlProvider()?.Trim() ?? "";
            }
            catch (Exception ex)
            {
                SendJson(ctx, 503, new { error = $"手机连接网址暂不可用: {ex.Message}" });
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsed)
                || parsed.IsLoopback
                || !string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                SendJson(ctx, 503, new { error = "监控端尚未准备好可供手机访问的局域网网址" });
                return;
            }

            SendJson(ctx, 200, new
            {
                url,
                qrCode = MobileConnectionService.CreateQrDataUri(url),
                accessProtected = _requireAccessKey
            });
        }

        private void HandleMobileAppDownload(HttpListenerContext ctx)
        {
            _mobileAppUpdatePolicy.RefreshInBackground();
            MobileAppDownloadInfo info = CreateMobileAppDownloadInfo(
                _mobileAppUpdatePolicy.LatestRelease);
            SendJson(ctx, 200, info);
        }

        internal static MobileAppDownloadInfo CreateMobileAppDownloadInfo(
            MobileAppReleaseInfo latestRelease)
        {
            string downloadUrl = MobileAppUpdatePolicyProvider.ReleasesUrl;
            string iosDownloadUrl = MobileAppUpdatePolicyProvider.TestFlightJoinUrl;
            return new MobileAppDownloadInfo(
                latestRelease?.Version ?? "",
                downloadUrl,
                MobileConnectionService.CreateQrDataUri(downloadUrl),
                iosDownloadUrl,
                MobileConnectionService.CreateQrDataUri(iosDownloadUrl));
        }

        private bool TryAuthorizeRequest(HttpListenerContext ctx, out bool authorizedByQuery)
        {
            authorizedByQuery = false;
            if (string.IsNullOrWhiteSpace(_accessKey)) return false;

            string headerKey = ctx.Request.Headers["X-EPM-Access-Key"];
            if (AccessKeysEqual(headerKey, _accessKey))
                return true;

            string queryKey = ctx.Request.QueryString["key"];
            if (AccessKeysEqual(queryKey, _accessKey))
            {
                authorizedByQuery = true;
                SetAccessCookie(ctx);
                return true;
            }

            string cookieValue = ctx.Request.Cookies["EPM_WEB_ACCESS"]?.Value;
            return AccessKeysEqual(cookieValue, ComputeAccessCookieValue(_accessKey));
        }

        private void SetAccessCookie(HttpListenerContext ctx)
        {
            var cookie = new Cookie("EPM_WEB_ACCESS", ComputeAccessCookieValue(_accessKey), "/")
            {
                HttpOnly = true,
                Expires = DateTime.UtcNow.AddDays(30)
            };
            ctx.Response.SetCookie(cookie);
        }

        internal static bool AccessKeysEqual(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            byte[] leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
            byte[] rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
            return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
        }

        private static string ComputeAccessCookieValue(string accessKey)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessKey))).ToLowerInvariant();
        }

        private static void SendUnauthorized(HttpListenerContext ctx, string path)
        {
            if (path == "")
            {
                const string html = """
<!DOCTYPE html><html lang="zh-CN"><head><meta charset="utf-8"><title>需要访问链接</title></head>
<body style="font-family:Microsoft YaHei UI,sans-serif;padding:32px;color:#172033">
<h1>此监控网页已启用访问保护</h1><p>请在监控端点击“复制并打开监控网页”，使用复制的完整链接访问。</p>
</body></html>
""";
                byte[] bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
                return;
            }

            SendJson(ctx, 401, new { error = "需要有效的监控网页访问链接" });
        }

        private static bool IsAllowedCorsOrigin(string origin)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;

            string host = uri.Host;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("::1", StringComparison.OrdinalIgnoreCase))
                return true;

            if (host.Equals("kuaidizs.cn", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".kuaidizs.cn", StringComparison.OrdinalIgnoreCase))
                return true;

            if (IPAddress.TryParse(host, out var ip))
                return IsPrivateAddress(ip);

            return false;
        }

        private static bool IsPrivateAddress(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return true;

            byte[] bytes = ip.GetAddressBytes();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return bytes[0] == 10 ||
                       (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                       (bytes[0] == 192 && bytes[1] == 168) ||
                       (bytes[0] == 169 && bytes[1] == 254);
            }

            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        }

        // ───── API: 推送订单信息 (来自油猴脚本) ─────
        private void HandleBroadcastOrderInfo(HttpListenerContext ctx)
        {
            try
            {
                string body = ReadRequestBody(ctx, MaxOrderInfoBodyBytes);
                var request = JsonSerializer.Deserialize<OrderBroadcastRequest>(body, _jsonOptions);
                List<OrderInfo> items = request?.Orders;
                if (items == null || items.Count == 0)
                {
                    SendJson(ctx, 400, new { error = "空数据" });
                    return;
                }

                ValidateOrderInfoItems(items);
                string[] requestedTargetNodeIds = (request?.TargetNodeIds ?? [])
                    .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
                    .Select(nodeId => nodeId.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (requestedTargetNodeIds.Length > MaxOrderBroadcastTargets)
                {
                    SendJson(ctx, 400, new
                    {
                        error = $"一次最多指定 {MaxOrderBroadcastTargets} 台订单接收设备",
                        maxTargets = MaxOrderBroadcastTargets,
                        requestedTargets = requestedTargetNodeIds.Length
                    });
                    return;
                }

                HashSet<string> targetNodeIds = requestedTargetNodeIds
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (targetNodeIds.Count == 0)
                {
                    SendJson(ctx, 400, new { error = "没有指定订单接收设备" });
                    return;
                }
                IReadOnlyList<RecordingDeviceInfo> devices = GetRecordingDevices(
                    ctx.Request.Url?.Authority ?? "",
                    includeKnown: false)
                    .Where(device => targetNodeIds.Contains(device.NodeId))
                    .ToArray();
                if (devices.Count == 0)
                {
                    SendJson(ctx, 409, new { error = "当前没有在线的订单接收设备" });
                    return;
                }

                IReadOnlyList<OrderInfoRelayResult> results = OrderInfoRelay.BroadcastAsync(
                    devices,
                    _nodeId,
                    items,
                    OrderInfoRelay.SendAsync,
                    device =>
                    {
                        var (_, testCount) = AcceptOrderInfos(items);
                        return new OrderInfoRelayResult(
                            device.NodeId,
                            device.NodeName,
                            device.DeviceType,
                            device.Address,
                            true,
                            200,
                            testCount,
                            items.Count);
                    },
                    _cts.Token).GetAwaiter().GetResult();
                foreach (OrderInfoRelayResult result in results.Where(result => result.Ok))
                {
                    int receivedCount = result.ReceivedCount > 0 ? result.ReceivedCount : items.Count;
                    _orderIntegrationActivities.RecordReceived(result.NodeId, receivedCount);
                }
                SendJson(ctx, 200, new
                {
                    success = results.Any(result => result.Ok),
                    ok = results.Any(result => result.Ok),
                    results
                });
            }
            catch (Exception ex)
            {
                Log($"HandleBroadcastOrderInfo 异常: {ex.Message}");
                SendJson(ctx, 400, new { error = ex.Message });
            }
        }

        private void HandlePushOrderInfo(HttpListenerContext ctx)
        {
            try
            {
                string body = ReadRequestBody(ctx, MaxOrderInfoBodyBytes);
                var items = JsonSerializer.Deserialize<List<OrderInfo>>(body, _jsonOptions);
                if (items == null || items.Count == 0)
                {
                    SendJson(ctx, 400, new { error = "空数据" });
                    return;
                }

                ValidateOrderInfoItems(items);

                (int count, int testCount) = AcceptOrderInfos(items);

                if (EnableOrderInfoLog)
                {
                    Log($"HandlePushOrderInfo: 接收 {count} 条订单信息, 测试={testCount}, 缓存总数={_orderInfoCache.Count}");
                    foreach (var item in items)
                    {
                        if (!string.IsNullOrWhiteSpace(item.TrackingNumber))
                            Log($"  订单: 运单号={item.TrackingNumber}, 订单号={item.OrderId}, 测试={item.IsTest}, 打印后退款={item.IsPrintedRefund}, 退款状态=[{item.RefundStatus}], 买家留言=[{item.BuyerMessage}], 卖家备注=[{item.SellerMemo}], 商品=[{item.ProductInfo}]");
                    }
                }

                SendJson(ctx, 200, new
                {
                    success = true,
                    ok = true,
                    nodeId = _nodeId,
                    nodeName = _nodeName,
                    receivedCount = count,
                    count,
                    testCount
                });
            }
            catch (Exception ex)
            {
                Log($"HandlePushOrderInfo 异常: {ex.Message}");
                SendJson(ctx, 400, new { error = ex.Message });
            }
        }

        private void HandleExtensionCapabilities(HttpListenerContext ctx)
        {
            SendJson(ctx, 200, new
            {
                apiVersion = "v1",
                product = "PackingProof",
                nodeId = _nodeId,
                nodeName = _nodeName,
                accessKeyRequired = _requireAccessKey,
                extensionApiEnabled = _extensionApiEnabled,
                features = new
                {
                    ordersWrite = true,
                    signedScanTasks = true,
                    totalItemCount = true,
                    mergedOrderCount = true,
                        providerId = true,
                        recordingMetadataWrite = true,
                        watermarkFields = true
                },
                limits = new
                {
                    maxOrdersPerRequest = MaxOrderInfoItems,
                    maxRequestBytes = MaxOrderInfoBodyBytes,
                    maxFieldCharacters = 4000
                }
            });
        }

        private static bool IsSignedExtensionPath(string path, string method) =>
            (string.Equals(path, "/api/extensions/v1/heartbeat", StringComparison.OrdinalIgnoreCase)
                && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            || (string.Equals(path, "/api/extensions/v1/scan-tasks/next", StringComparison.OrdinalIgnoreCase)
                && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            || (string.Equals(path, "/api/extensions/v1/scan-results", StringComparison.OrdinalIgnoreCase)
                && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            || (path.StartsWith("/api/extensions/v1/scan-tasks/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/ack", StringComparison.OrdinalIgnoreCase)
                && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase));

        private bool TryAuthorizeExtensionRequest(
            HttpListenerContext ctx,
            out int statusCode,
            out string errorCode)
        {
            statusCode = 401;
            errorCode = "extension_auth_required";
            if (!_extensionApiEnabled || _extensionRequestAuthenticator == null)
            {
                statusCode = 403;
                errorCode = "extension_disabled";
                return false;
            }

            byte[] body;
            try { body = ReadRequestBytesCore(ctx, MaxJsonBodyBytes); }
            catch (InvalidDataException)
            {
                statusCode = 413;
                errorCode = "extension_request_too_large";
                return false;
            }

            int version = 0;
            int credentialGeneration = 0;
            long timestamp = 0;
            bool validHeaders = int.TryParse(
                    ctx.Request.Headers[ExtensionRequestSignature.VersionHeader],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out version)
                && int.TryParse(
                    ctx.Request.Headers[ExtensionRequestSignature.CredentialGenerationHeader],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out credentialGeneration)
                && long.TryParse(
                    ctx.Request.Headers[ExtensionRequestSignature.TimestampHeader],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out timestamp);
            if (!validHeaders)
                return false;

            var request = new ExtensionSignedRequest
            {
                Version = version,
                ExtensionInstanceId = ctx.Request.Headers[ExtensionRequestSignature.InstanceIdHeader] ?? "",
                CredentialGeneration = credentialGeneration,
                Timestamp = timestamp,
                Nonce = ctx.Request.Headers[ExtensionRequestSignature.NonceHeader] ?? "",
                ContentHash = ctx.Request.Headers[ExtensionRequestSignature.ContentHashHeader] ?? "",
                Signature = ctx.Request.Headers[ExtensionRequestSignature.SignatureHeader] ?? "",
                Method = ctx.Request.HttpMethod,
                RequestTarget = ctx.Request.Url?.PathAndQuery ?? "/",
                Body = body
            };
            ExtensionAuthenticationResult result = _extensionRequestAuthenticator.Authenticate(request);
            if (result.Disposition != ExtensionAuthenticationDisposition.Accepted
                || result.Authorization == null)
            {
                (statusCode, errorCode) = MapExtensionAuthenticationFailure(result.Disposition);
                return false;
            }

            _authenticatedRequestBodies[ctx] = body;
            _authenticatedExtensionContexts[ctx] = result.Authorization;
            return true;
        }

        private static (int StatusCode, string ErrorCode) MapExtensionAuthenticationFailure(
            ExtensionAuthenticationDisposition disposition) => disposition switch
        {
            ExtensionAuthenticationDisposition.UnsupportedVersion => (426, "extension_auth_version_unsupported"),
            ExtensionAuthenticationDisposition.StaleTimestamp => (401, "extension_auth_timestamp_stale"),
            ExtensionAuthenticationDisposition.UnknownOrRevokedExtension => (403, "extension_auth_revoked"),
            ExtensionAuthenticationDisposition.CredentialGenerationMismatch => (403, "extension_auth_generation_mismatch"),
            ExtensionAuthenticationDisposition.ContentHashMismatch => (401, "extension_auth_content_hash_mismatch"),
            ExtensionAuthenticationDisposition.InvalidSignature => (401, "extension_auth_signature_invalid"),
            ExtensionAuthenticationDisposition.ReplayDetected => (409, "extension_auth_replay_detected"),
            ExtensionAuthenticationDisposition.ReplayCapacityExceeded => (429, "extension_auth_replay_capacity"),
            _ => (401, "extension_auth_invalid_request")
        };

        private static string GetExtensionAuthenticationError(string errorCode) => errorCode switch
        {
            "extension_disabled" => "主机尚未启用扩展 API",
            "extension_request_too_large" => "扩展请求内容过大",
            "extension_auth_version_unsupported" => "扩展签名协议版本不受支持",
            "extension_auth_timestamp_stale" => "扩展请求时间已过期，请校准系统时间",
            "extension_auth_revoked" => "扩展授权不存在或已撤销",
            "extension_auth_generation_mismatch" => "扩展凭据已更新，请重新授权",
            "extension_auth_content_hash_mismatch" => "扩展请求内容校验失败",
            "extension_auth_signature_invalid" => "扩展请求签名无效",
            "extension_auth_replay_detected" => "扩展请求已被处理，请勿重复发送",
            "extension_auth_replay_capacity" => "扩展请求过于频繁，请稍后重试",
            _ => "需要有效的扩展签名"
        };

        private void HandleExtensionHeartbeat(HttpListenerContext ctx)
        {
            if (!_authenticatedExtensionContexts.TryGetValue(
                    ctx,
                    out ExtensionAuthorizationContext authorization))
            {
                SendJson(ctx, 401, new
                {
                    errorCode = "extension_auth_required",
                    error = "需要有效的扩展签名"
                });
                return;
            }

            ExtensionHeartbeatRequest request;
            try { request = ReadJsonBody<ExtensionHeartbeatRequest>(ctx); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                SendJson(ctx, 400, new { errorCode = "extension_heartbeat_invalid", error = "扩展心跳内容无效" });
                return;
            }
            _extensionAuthorizations?.RecordHeartbeat(
                authorization.ExtensionInstanceId,
                request.Version);

            SendJson(ctx, 200, new
            {
                ok = true,
                apiVersion = "v1",
                extensionInstanceId = authorization.ExtensionInstanceId,
                serverTime = DateTimeOffset.UtcNow
            });
        }

        private void HandlePollExtensionScanTask(HttpListenerContext ctx)
        {
            if (!TryGetExtensionAuthorization(
                    ctx,
                    ExtensionPermissions.ScanTasksRead,
                    out ExtensionAuthorizationContext authorization))
                return;
            if (_extensionScanTaskBroker == null)
            {
                SendJson(ctx, 503, new { errorCode = "extension_task_service_unavailable", error = "扩展任务服务尚未就绪" });
                return;
            }

            int waitSeconds = 20;
            if (int.TryParse(ctx.Request.QueryString["waitSeconds"], out int requestedWait))
                waitSeconds = Math.Clamp(requestedWait, 0, 25);
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(waitSeconds);
            ExtensionScanDelivery delivery = null;
            do
            {
                ExtensionScanDelivery candidate = _extensionScanTaskBroker.Poll(
                    authorization.ExtensionInstanceId);
                if (candidate != null
                    && authorization.SupportsCapability(candidate.Capability)
                    && authorization.IsBoundToOriginNode(candidate.ScanEvent.OriginNodeId))
                {
                    delivery = candidate;
                    break;
                }
                if (waitSeconds == 0 || DateTimeOffset.UtcNow >= deadline)
                    break;
                Thread.Sleep(200);
            } while (true);

            if (delivery == null)
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.OutputStream.Close();
                return;
            }
            SendJson(ctx, 200, ToExtensionDeliveryResponse(delivery));
        }

        private void HandleAcknowledgeExtensionScanTask(HttpListenerContext ctx, string path)
        {
            if (!TryGetExtensionAuthorization(
                    ctx,
                    ExtensionPermissions.ScanTasksRead,
                    out ExtensionAuthorizationContext authorization))
                return;
            if (_extensionScanTaskBroker == null)
            {
                SendJson(ctx, 503, new { errorCode = "extension_task_service_unavailable", error = "扩展任务服务尚未就绪" });
                return;
            }

            const string prefix = "/api/extensions/v1/scan-tasks/";
            string deliveryId = path[prefix.Length..^"/ack".Length];
            ExtensionScanTaskAckRequest request;
            try { request = ReadJsonBody<ExtensionScanTaskAckRequest>(ctx); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                SendJson(ctx, 400, new { errorCode = "extension_ack_invalid", error = "任务确认内容无效" });
                return;
            }

            ExtensionScanAcknowledgementDisposition result;
            try
            {
                result = _extensionScanTaskBroker.Acknowledge(
                    authorization.ExtensionInstanceId,
                    deliveryId,
                    request.TaskId);
            }
            catch (InvalidDataException ex)
            {
                SendJson(ctx, 400, new { errorCode = "extension_ack_invalid", error = ex.Message });
                return;
            }
            switch (result)
            {
                case ExtensionScanAcknowledgementDisposition.Accepted:
                case ExtensionScanAcknowledgementDisposition.Duplicate:
                    SendJson(ctx, 200, new { ok = true, duplicate = result == ExtensionScanAcknowledgementDisposition.Duplicate });
                    break;
                case ExtensionScanAcknowledgementDisposition.DeliveryNotFound:
                    SendJson(ctx, 404, new { errorCode = "extension_delivery_not_found", error = "任务投递不存在" });
                    break;
                case ExtensionScanAcknowledgementDisposition.Expired:
                    SendJson(ctx, 410, new { errorCode = "extension_delivery_expired", error = "任务投递已过期" });
                    break;
                default:
                    SendJson(ctx, 403, new { errorCode = "extension_delivery_forbidden", error = "扩展无权确认此任务" });
                    break;
            }
        }

        private void HandleExtensionScanResult(HttpListenerContext ctx)
        {
            if (!TryGetExtensionAuthorization(
                    ctx,
                    ExtensionPermissions.ScanResultsWrite,
                    out ExtensionAuthorizationContext authorization))
                return;
            if (_extensionScanResultCoordinator == null)
            {
                SendJson(ctx, 503, new { errorCode = "extension_result_service_unavailable", error = "扩展结果服务尚未就绪" });
                return;
            }

            ExtensionScanResultRequest request;
            try
            {
                request = ReadJsonBody<ExtensionScanResultRequest>(ctx);
                ExtensionScanResultSubmissionOutcome outcome = _extensionScanResultCoordinator.Submit(
                    authorization,
                    request);
                SendExtensionScanResultOutcome(ctx, outcome);
                if (outcome.Disposition == ExtensionScanResultSubmissionDisposition.Accepted)
                {
                    int dataCount = Math.Max(1, request.Orders.Count + request.Measurements.Count);
                    _extensionAuthorizations?.RecordBusinessActivity(
                        authorization.ExtensionInstanceId,
                        dataCount);
                    try { _extensionResultAvailable?.Invoke(); }
                    catch (Exception ex) { RuntimeLog.Error("ExtensionResult", "Result processing trigger failed", ex); }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                SendJson(ctx, 403, new { errorCode = "extension_result_forbidden", error = ex.Message });
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                SendJson(ctx, 400, new { errorCode = "extension_result_invalid", error = ex.Message });
            }
        }

        private bool TryGetExtensionAuthorization(
            HttpListenerContext ctx,
            string permission,
            out ExtensionAuthorizationContext authorization)
        {
            if (!_authenticatedExtensionContexts.TryGetValue(ctx, out authorization))
            {
                SendJson(ctx, 401, new { errorCode = "extension_auth_required", error = "需要有效的扩展签名" });
                return false;
            }
            if (!authorization.HasPermission(permission))
            {
                SendJson(ctx, 403, new { errorCode = "extension_permission_denied", error = "扩展没有调用此接口的权限" });
                return false;
            }
            return true;
        }

        private static object ToExtensionDeliveryResponse(ExtensionScanDelivery delivery) => new
        {
            deliveryId = delivery.DeliveryId,
            taskId = delivery.ScanEvent.TaskId,
            originNodeId = delivery.ScanEvent.OriginNodeId,
            recordingSessionId = delivery.ScanEvent.RecordingSessionId,
            trackingNumber = delivery.ScanEvent.TrackingNumber,
            recordingMode = delivery.ScanEvent.RecordingMode,
            capability = delivery.Capability,
            occurredAt = delivery.ScanEvent.OccurredAtUtc,
            softDeadline = delivery.ScanEvent.SoftDeadlineUtc,
            expiresAt = delivery.ScanEvent.ExpiresAtUtc,
            deliveryAttempt = delivery.DeliveryAttempts
        };

        private static void SendExtensionScanResultOutcome(
            HttpListenerContext ctx,
            ExtensionScanResultSubmissionOutcome outcome)
        {
            switch (outcome.Disposition)
            {
                case ExtensionScanResultSubmissionDisposition.Accepted:
                    SendJson(ctx, 202, new { ok = true, inboxId = outcome.InboxId, duplicate = false });
                    break;
                case ExtensionScanResultSubmissionDisposition.Duplicate:
                case ExtensionScanResultSubmissionDisposition.BusinessDuplicate:
                    SendJson(ctx, 200, new { ok = true, inboxId = outcome.InboxId, duplicate = true });
                    break;
                case ExtensionScanResultSubmissionDisposition.Expired:
                    SendJson(ctx, 410, new { errorCode = "extension_delivery_expired", error = "任务投递已过期" });
                    break;
                case ExtensionScanResultSubmissionDisposition.DeliveryNotFound:
                    SendJson(ctx, 404, new { errorCode = "extension_delivery_not_found", error = "任务投递不存在" });
                    break;
                default:
                    SendJson(ctx, 409, new { errorCode = "extension_result_conflict", error = "扩展结果修订与已接收内容冲突" });
                    break;
            }
        }

        private void HandleExtensionEnrollment(HttpListenerContext ctx)
        {
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Headers["Pragma"] = "no-cache";
            if (!_extensionApiEnabled)
            {
                SendJson(ctx, 403, new
                {
                    errorCode = "extension_disabled",
                    error = "主机尚未启用扩展 API"
                });
                return;
            }
            IPAddress remoteAddress = ctx.Request.RemoteEndPoint?.Address;
            if (remoteAddress == null || !IsPrivateAddress(remoteAddress))
            {
                SendJson(ctx, 403, new
                {
                    errorCode = "extension_enrollment_private_network_required",
                    error = "扩展授权只允许从本机或私有局域网发起"
                });
                return;
            }

            ExtensionEnrollmentHttpRequest payload;
            try
            {
                payload = ReadJsonBody<ExtensionEnrollmentHttpRequest>(ctx);
                if (payload == null) throw new InvalidDataException("授权请求为空");
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                SendJson(ctx, 400, new
                {
                    errorCode = "extension_enrollment_invalid",
                    error = "扩展授权信息无效"
                });
                return;
            }

            ExtensionEnrollmentOutcome outcome;
            try
            {
                outcome = GetExtensionEnrollmentService().Enroll(new ExtensionEnrollmentRequest
                {
                    RequestId = payload.RequestId,
                    RequestSecret = payload.RequestSecret,
                    ExtensionInstanceId = payload.ExtensionInstanceId,
                    ProviderId = payload.ProviderId,
                    DisplayName = payload.DisplayName,
                    Version = payload.Version,
                    Source = payload.Source,
                    RemoteAddress = remoteAddress.ToString(),
                    RequestedPermissions = payload.RequestedPermissions ?? [],
                    RequestedCapabilities = payload.RequestedCapabilities ?? []
                });
            }
            catch (InvalidDataException ex)
            {
                RuntimeLog.Info(
                    "ExtensionEnrollment",
                    $"Rejected invalid enrollment remote={remoteAddress}");
                SendJson(ctx, 400, new
                {
                    errorCode = "extension_enrollment_invalid",
                    error = ex.Message
                });
                return;
            }

            if (outcome.Disposition == ExtensionEnrollmentDisposition.Denied)
            {
                SendJson(ctx, 403, new
                {
                    errorCode = "extension_enrollment_denied",
                    error = "电脑端已拒绝此扩展连接"
                });
                return;
            }
            if (outcome.Disposition == ExtensionEnrollmentDisposition.Unavailable)
            {
                SendJson(ctx, 503, new
                {
                    errorCode = "extension_enrollment_approval_unavailable",
                    error = "电脑端暂时无法显示扩展授权窗口，请打开主界面后重试"
                });
                return;
            }
            if (outcome.Disposition == ExtensionEnrollmentDisposition.RequestConflict)
            {
                SendJson(ctx, 409, new
                {
                    errorCode = "extension_enrollment_request_conflict",
                    error = "授权请求标识已被另一份请求使用，请生成新的请求标识和随机秘密"
                });
                return;
            }

            ExtensionEnrollmentCredential enrollment = outcome.Enrollment;
            if (enrollment == null)
            {
                SendJson(ctx, 503, new
                {
                    errorCode = "extension_enrollment_approval_unavailable",
                    error = "扩展授权结果暂时不可用，请稍后重试"
                });
                return;
            }

            ExtensionAuthorizationContext authorization = enrollment.Authorization;
            SendJson(ctx, 200, new
            {
                apiVersion = "v1",
                extensionInstanceId = authorization.ExtensionInstanceId,
                credential = enrollment.Credential,
                credentialGeneration = authorization.CredentialGeneration,
                permissions = authorization.Permissions,
                capabilities = authorization.Capabilities,
                routingScope = authorization.RoutingScope == ExtensionRoutingScope.AllLocalRecordingNodes
                    ? "all_local_recording_nodes"
                    : "selected_recording_nodes",
                boundOriginNodeIds = authorization.BoundOriginNodeIds,
                approvedAtUtc = authorization.ApprovedAtUtc
            });
        }

        private ExtensionEnrollmentService GetExtensionEnrollmentService()
        {
            if (!_extensionApiEnabled)
                throw new InvalidOperationException("扩展 API 未启用");
            if (_extensionEnrollment != null) return _extensionEnrollment;
            lock (_extensionEnrollmentInitializationLock)
            {
                if (_extensionEnrollment != null) return _extensionEnrollment;
                _extensionAuthorizations = new ExtensionAuthorizationStore(_extensionStateDirectory);
                _extensionRequestAuthenticator = new ExtensionRequestAuthenticator(_extensionAuthorizations);
                _extensionEnrollment = new ExtensionEnrollmentService(
                    _extensionAuthorizations,
                    _ => new ExtensionEnrollmentApprovalResult
                    {
                        Disposition = ExtensionEnrollmentApprovalDisposition.Unavailable
                    });
                return _extensionEnrollment;
            }
        }

        private void HandleActiveRecordingExtensions(HttpListenerContext ctx)
        {
            SendJson(ctx, 200, new
            {
                apiVersion = "v1",
                recordings = _db.GetActiveRecordingSessions().Select(record => new
                {
                    recordingSessionId = record.SourceSessionId,
                    videoRecordId = record.Id,
                    trackingNumber = record.TrackingNumber,
                    orderId = record.OrderId,
                    mode = record.Mode,
                    startTime = record.StartTime
                }).ToList()
            });
        }

        private void HandleRecordingExtensionData(HttpListenerContext ctx, string path, string method)
        {
            if (!TryParseRecordingExtensionDataPath(path, out string recordingSessionId))
            {
                SendJson(ctx, 400, new { errorCode = "invalid_recording_session", error = "录像会话 ID 无效" });
                return;
            }

            if (method == "GET")
            {
                SendJson(ctx, 200, new
                {
                    apiVersion = "v1",
                    recordingSessionId,
                    fields = _db.GetRecordingExtensionFields(recordingSessionId)
                });
                return;
            }

            try
            {
                if (_db.GetActiveRecordingBySession(recordingSessionId) == null)
                {
                    SendJson(ctx, 409, new { errorCode = "recording_not_active", error = "录像会话不存在或已结束" });
                    return;
                }

                RecordingExtensionDataRequest request = ReadJsonBody<RecordingExtensionDataRequest>(ctx);
                ValidateRecordingExtensionData(request);
                _db.UpsertRecordingExtensionFields(
                    recordingSessionId,
                    request.Namespace.Trim(),
                    request.ProviderId.Trim(),
                    request.Fields,
                    DateTime.UtcNow);
                try
                {
                    RecordingExtensionDataChanged?.Invoke(
                        recordingSessionId,
                        _db.GetRecordingExtensionFields(recordingSessionId));
                }
                catch (Exception ex)
                {
                    Log($"RecordingExtensionDataChanged 回调失败: {ex.Message}");
                }
                SendJson(ctx, 200, new
                {
                    success = true,
                    ok = true,
                    apiVersion = "v1",
                    recordingSessionId,
                    updatedCount = request.Fields.Count
                });
            }
            catch (JsonException ex)
            {
                Log($"HandleRecordingExtensionData JSON 无效: {ex.Message}");
                SendJson(ctx, 400, new { errorCode = "invalid_json", error = "请求内容不是有效的 JSON" });
            }
            catch (InvalidDataException ex)
            {
                Log($"HandleRecordingExtensionData 数据无效: {ex.Message}");
                SendJson(ctx, 400, new { errorCode = "invalid_extension_data", error = ex.Message });
            }
        }

        private static void ValidateRecordingExtensionData(RecordingExtensionDataRequest request)
        {
            if (request == null) throw new InvalidDataException("请求内容无效");
            ValidateExtensionIdentifier(request.Namespace, "命名空间", 64);
            ValidateExtensionIdentifier(request.ProviderId, "来源标识", 128);
            if (request.Fields == null || request.Fields.Count == 0)
                throw new InvalidDataException("至少需要提交一个扩展字段");
            if (request.Fields.Count > 32)
                throw new InvalidDataException("单次最多提交 32 个扩展字段");
            foreach (var pair in request.Fields)
            {
                ValidateExtensionIdentifier(pair.Key, "字段名", 64);
                ValidateFieldLength(pair.Value, 1000, "字段值");
            }
        }

        private static void ValidateExtensionIdentifier(string value, string fieldName, int maxLength)
        {
            string normalized = value?.Trim() ?? "";
            if (normalized.Length == 0 || normalized.Length > maxLength
                || normalized.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-')))
                throw new InvalidDataException($"{fieldName}格式无效");
        }

        private static bool TryParseRecordingExtensionDataPath(string path, out string recordingSessionId)
        {
            recordingSessionId = "";
            const string prefix = "/api/extensions/v1/recordings/";
            const string suffix = "/data";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return false;
            string value = path[prefix.Length..^suffix.Length].Trim('/');
            if (value.Length == 0 || value.Length > 128) return false;
            recordingSessionId = Uri.UnescapeDataString(value);
            return recordingSessionId.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');
        }

        private void HandleExtensionOrders(HttpListenerContext ctx)
        {
            try
            {
                OrderExtensionRequest request = ReadJsonBody<OrderExtensionRequest>(ctx);
                if (request == null || request.Orders == null || request.Orders.Count == 0)
                {
                    SendJson(ctx, 400, new { errorCode = "orders_empty", error = "订单数据不能为空" });
                    return;
                }

                string providerId = (request.ProviderId ?? "").Trim();
                ValidateFieldLength(providerId, 128, "来源标识");
                if (string.IsNullOrWhiteSpace(providerId))
                {
                    SendJson(ctx, 400, new { errorCode = "provider_required", error = "缺少来源标识 providerId" });
                    return;
                }

                if (!string.IsNullOrWhiteSpace(request.ApiVersion)
                    && !string.Equals(request.ApiVersion.Trim(), "v1", StringComparison.OrdinalIgnoreCase))
                {
                    SendJson(ctx, 426, new { errorCode = "extension_api_version_unsupported", error = "扩展接口版本不受支持，请使用 v1" });
                    return;
                }

                foreach (OrderInfo item in request.Orders)
                {
                    if (item != null)
                        item.ProviderId = providerId;
                }
                ValidateOrderInfoItems(request.Orders);
                (int count, int testCount) = AcceptOrderInfos(request.Orders);

                SendJson(ctx, 200, new
                {
                    success = true,
                    ok = true,
                    apiVersion = "v1",
                    providerId,
                    nodeId = _nodeId,
                    nodeName = _nodeName,
                    receivedCount = count,
                    count,
                    testCount
                });
            }
            catch (JsonException ex)
            {
                Log($"HandleExtensionOrders JSON 无效: {ex.Message}");
                SendJson(ctx, 400, new { errorCode = "invalid_json", error = "请求内容不是有效的 JSON" });
            }
            catch (InvalidDataException ex)
            {
                Log($"HandleExtensionOrders 数据无效: {ex.Message}");
                SendJson(ctx, 400, new { errorCode = "invalid_order_data", error = ex.Message });
            }
            catch (Exception ex)
            {
                Log($"HandleExtensionOrders 异常: {ex.Message}");
                SendJson(ctx, 500, new { errorCode = "extension_orders_failed", error = "服务器暂时无法处理订单数据" });
            }
        }

        private (int Count, int TestCount) AcceptOrderInfos(List<OrderInfo> items)
        {
            var realItems = items.Where(item => !item.IsTest).ToList();
            int count = StoreOrderInfos(realItems, preserveConfirmedRefund: true);
            int testCount = items.Count(item => item.IsTest);
            try { OrderInfoReceived?.Invoke(items); } catch { }
            return (count, testCount);
        }

        private int StoreOrderInfos(List<OrderInfo> items, bool preserveConfirmedRefund)
        {
            int count = 0;
            if (items == null || items.Count == 0) return count;

            lock (_orderInfoLock)
            {
                DateTime cutoff = DateTime.Now.Subtract(VideoDatabase.OrderInfoRetention);
                foreach (string expiredKey in _orderInfoCache
                    .Where(x => x.Value.PushTime < cutoff)
                    .Select(x => x.Key)
                    .ToList())
                {
                    _orderInfoCache.Remove(expiredKey);
                }

                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.TrackingNumber)) continue;
                    string key = item.TrackingNumber.Trim().ToUpperInvariant();
                    if (preserveConfirmedRefund && _orderInfoCache.TryGetValue(key, out var existing) && existing.IsPrintedRefund && !item.IsPrintedRefund)
                    {
                        // 普通页面的旧 DOM 不覆盖已确认退款；扫码触发的实时查询可以覆盖。
                        item.HasRefund = true;
                        item.IsPrintedRefund = true;
                        if (string.IsNullOrWhiteSpace(item.RefundStatus))
                            item.RefundStatus = existing.RefundStatus;
                        if (string.IsNullOrWhiteSpace(item.RefundProductInfo))
                            item.RefundProductInfo = existing.RefundProductInfo;
                    }
                    item.PushTime = DateTime.Now;
                    _orderInfoCache[key] = item;
                    count++;
                }

                if (_orderInfoCache.Count > VideoDatabase.MaxOrderInfoRecords)
                {
                    foreach (string overflowKey in _orderInfoCache
                        .OrderByDescending(x => x.Value.PushTime)
                        .Skip(VideoDatabase.MaxOrderInfoRecords)
                        .Select(x => x.Key)
                        .ToList())
                    {
                        _orderInfoCache.Remove(overflowKey);
                    }
                }
            }

            if (count > 0)
            {
                _db.UpsertOrderInfos(items);
                _db.CleanupExpiredOrderInfos();
                _db.UpdateRecentVideoOrderInfos(items);
            }
            return count;
        }

        // ───── API: 查询订单信息 ─────
        private void HandleQueryOrderInfo(HttpListenerContext ctx)
        {
            string trackingNo = (ctx.Request.QueryString["trackingNo"] ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(trackingNo))
            {
                SendJson(ctx, 400, new { error = "缺少 trackingNo 参数" });
                return;
            }

            lock (_orderInfoLock)
            {
                if (_orderInfoCache.TryGetValue(trackingNo, out var info))
                {
                    SendJson(ctx, 200, new
                    {
                        found = true,
                        info.TrackingNumber,
                        info.OrderId,
                        info.BuyerMessage,
                        info.SellerMemo,
                        info.ProductInfo,
                        info.TotalItemCount,
                        info.MergedOrderCount,
                        info.ProviderId,
                        info.HasRefund,
                        info.IsPrintedRefund,
                        info.RefundStatus,
                        info.RefundProductInfo
                    });
                    return;
                }
            }

            SendJson(ctx, 200, new { found = false });
        }

        /// <summary>根据快递单号查询已推送的订单信息（供 ViewModel 调用）</summary>
        public OrderInfo GetOrderInfo(string trackingNo)
        {
            if (string.IsNullOrWhiteSpace(trackingNo)) return null;
            string key = trackingNo.Trim().ToUpperInvariant();
            lock (_orderInfoLock)
            {
                if (_orderInfoCache.TryGetValue(key, out var info))
                {
                    if (EnableOrderInfoLog)
                        Log($"GetOrderInfo 命中: {key} => 打印后退款={info.IsPrintedRefund}, 退款状态=[{info.RefundStatus}], 买家留言=[{info.BuyerMessage}], 卖家备注=[{info.SellerMemo}], 商品=[{info.ProductInfo}]");
                    return info;
                }
                if (EnableOrderInfoLog)
                    Log($"GetOrderInfo 未命中: {key}, 缓存总数={_orderInfoCache.Count}");
                return null;
            }
        }

        public bool HasActiveOrderLookupClient
        {
            get
            {
                long ticks = Volatile.Read(ref _lastOrderLookupPollUtcTicks);
                return Volatile.Read(ref _activeOrderLookupPolls) > 0 ||
                    (ticks > 0 && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromSeconds(5));
            }
        }

        public async Task<OrderLookupResult> RequestFreshOrderSnapshotAsync(TimeSpan timeout, IEnumerable<string> trackingNumbers = null)
        {
            if (!HasActiveOrderLookupClient)
                return new OrderLookupResult { Responded = false };

            CleanupExpiredOrderLookups();
            var pending = new PendingOrderLookup
            {
                RequestId = Guid.NewGuid().ToString("N"),
                TrackingNumbers = (trackingNumbers ?? Array.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(50)
                    .ToArray()
            };
            _pendingOrderLookups[pending.RequestId] = pending;
            _orderLookupSignal.Release();

            Task completed = await Task.WhenAny(pending.Completion.Task, Task.Delay(timeout));
            _pendingOrderLookups.TryRemove(pending.RequestId, out _);
            return completed == pending.Completion.Task
                ? await pending.Completion.Task
                : new OrderLookupResult { Responded = false };
        }

        private void HandlePollOrderLookup(HttpListenerContext ctx)
        {
            Interlocked.Increment(ref _activeOrderLookupPolls);
            Interlocked.Exchange(ref _lastOrderLookupPollUtcTicks, DateTime.UtcNow.Ticks);
            try
            {
                CleanupExpiredOrderLookups();
                PendingOrderLookup pending = ClaimNextOrderLookup();
                if (pending == null)
                {
                    try { _orderLookupSignal.Wait(TimeSpan.FromSeconds(20), _cts.Token); }
                    catch (OperationCanceledException) { }
                    CleanupExpiredOrderLookups();
                    pending = ClaimNextOrderLookup();
                }

                if (pending == null)
                {
                    SendJson(ctx, 200, new { pending = false });
                    return;
                }

                SendJson(ctx, 200, new
                {
                    pending = true,
                    requestId = pending.RequestId,
                    trackingNumbers = pending.TrackingNumbers
                });
            }
            finally
            {
                Interlocked.Decrement(ref _activeOrderLookupPolls);
                Interlocked.Exchange(ref _lastOrderLookupPollUtcTicks, DateTime.UtcNow.Ticks);
            }
        }

        private PendingOrderLookup ClaimNextOrderLookup()
        {
            return _pendingOrderLookups.Values
                .OrderBy(x => x.CreatedAtUtc)
                .FirstOrDefault(x => Interlocked.CompareExchange(ref x.Claimed, 1, 0) == 0);
        }

        private void HandleOrderLookupResult(HttpListenerContext ctx)
        {
            try
            {
                string body = ReadRequestBody(ctx, MaxOrderInfoBodyBytes);
                var response = JsonSerializer.Deserialize<OrderLookupResponse>(body, _jsonOptions)
                    ?? throw new InvalidDataException("请求内容无效");
                if (string.IsNullOrWhiteSpace(response.RequestId) ||
                    !_pendingOrderLookups.TryGetValue(response.RequestId, out var pending))
                {
                    SendJson(ctx, 404, new { error = "核验请求已过期" });
                    return;
                }

                if (!response.Success)
                {
                    pending.Completion.TrySetResult(new OrderLookupResult { Responded = false });
                    SendJson(ctx, 200, new { ok = true, responded = false, error = response.Error ?? "打印端查询失败" });
                    return;
                }

                if (response.Orders == null)
                    throw new InvalidDataException("订单快照不能为空");

                foreach (OrderInfo info in response.Orders)
                    info.TrackingNumber = info.TrackingNumber?.Trim().ToUpperInvariant() ?? "";

                ValidateOrderInfoItems(response.Orders);
                StoreOrderInfos(response.Orders, preserveConfirmedRefund: false);
                try { OrderInfoReceived?.Invoke(response.Orders); } catch { }

                pending.Completion.TrySetResult(new OrderLookupResult
                {
                    Responded = true,
                    Orders = response.Orders
                });
                SendJson(ctx, 200, new { ok = true, responded = true, count = response.Orders.Count, error = response.Error ?? "" });
            }
            catch (Exception ex)
            {
                Log($"HandleOrderLookupResult 异常: {ex.Message}");
                SendJson(ctx, 400, new { error = ex.Message });
            }
        }

        private void CleanupExpiredOrderLookups()
        {
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-30);
            foreach (var entry in _pendingOrderLookups)
            {
                if (entry.Value.CreatedAtUtc >= cutoff) continue;
                if (_pendingOrderLookups.TryRemove(entry.Key, out var expired))
                    expired.Completion.TrySetResult(new OrderLookupResult { Responded = false });
            }
        }

        // ───── 从唯一持久化来源 SQLite 恢复运行时缓存 ─────
        private void MigrateLegacyOrderInfoCache()
        {
            string path = AppPaths.OrderInfoCachePath;
            if (!File.Exists(path)) return;

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var items = JsonSerializer.Deserialize<List<OrderInfo>>(json, _jsonOptions) ?? new List<OrderInfo>();
                DateTime cutoff = DateTime.Now.Subtract(VideoDatabase.OrderInfoRetention);
                items = items
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.TrackingNumber) && x.PushTime >= cutoff)
                    .ToList();
                if (items.Count > 0)
                    _db.UpsertOrderInfos(items);
                _db.CleanupExpiredOrderInfos();

                File.Delete(path);
                Debug.WriteLine($"[WebServer] 已迁移并删除旧 JSON 订单缓存，共 {items.Count} 条");
            }
            catch (Exception ex)
            {
                // 迁移失败时保留旧文件，数据库仍可独立工作，避免启动失败或数据丢失。
                Debug.WriteLine($"[WebServer] 迁移旧 JSON 订单缓存失败: {ex.Message}");
            }
        }

        private void LoadOrderInfoCacheFromDatabase()
        {
            try
            {
                List<OrderInfo> items = _db.GetRecentOrderInfos();
                lock (_orderInfoLock)
                {
                    foreach (var item in items)
                    {
                        if (string.IsNullOrWhiteSpace(item.TrackingNumber)) continue;
                        string key = item.TrackingNumber.Trim().ToUpperInvariant();
                        _orderInfoCache[key] = item;
                    }
                }
                Debug.WriteLine($"[WebServer] 从数据库恢复 {_orderInfoCache.Count} 条订单信息缓存");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebServer] 从数据库加载订单缓存失败: {ex.Message}");
            }
        }

        // ───── API: 搜索视频 ─────
        private void HandleStorageOverview(HttpListenerContext ctx)
        {
            try
            {
                byte[] responseBytes = _storageOverviewCache.GetOrCreate(BuildStorageOverviewResponse);
                SendJsonBytes(ctx, 200, responseBytes);
            }
            catch (Exception ex)
            {
                Log($"HandleStorageOverview 异常: {ex.Message}");
                SendJson(ctx, 500, new { errorCode = "storage_unavailable", error = "存储信息暂不可用" });
            }
        }

        private byte[] BuildStorageOverviewResponse()
        {
            var config = LoadAppConfig();
            IReadOnlyList<StorageLocation> locations = GetStorageOverviewLocations(config);

            var configuredPaths = locations.Select(BuildStoragePathInfo).ToList();
            var records = _db.GetActiveStorageVideoFiles()
                .Where(x => !string.IsNullOrWhiteSpace(x.FilePath))
                .ToList();

            var existingRecords = records.Where(x =>
            {
                try { return File.Exists(x.FilePath); }
                catch { return false; }
            }).ToList();

            long usedBytes = existingRecords.Sum(x => GetExistingFileSize(x.FilePath, x.FileSizeBytes));
            long totalBytes = configuredPaths.Sum(x => x.TotalBytes);
            if (totalBytes <= 0 && usedBytes > 0)
                totalBytes = usedBytes;
            long freeBytes = Math.Max(0, totalBytes - usedBytes);

            DateTime? oldest = existingRecords.Count > 0 ? existingRecords.Min(x => x.StartTime) : null;
            DateTime? latest = existingRecords.Count > 0 ? existingRecords.Max(x => x.StartTime) : null;
            int savedDays = CalculateSavedDays(oldest, latest);

            var recentRecords = existingRecords
                .Where(x => x.StartTime.Date >= DateTime.Today.AddDays(-9))
                .ToList();
            int historyDays = recentRecords.Select(x => x.StartTime.Date).Distinct().Count();
            long historyBytes = recentRecords.Sum(x => GetExistingFileSize(x.FilePath, x.FileSizeBytes));

            string estimateBasis = "";
            double avgGBPerDay = 0;
            double? estimatedRetentionDays = null;
            if (historyDays > 0 && historyBytes > 0)
            {
                avgGBPerDay = BytesToGB(historyBytes) / historyDays;
                if (avgGBPerDay > 0 && totalBytes > 0)
                {
                    estimatedRetentionDays = BytesToGB(totalBytes) / avgGBPerDay;
                    estimateBasis = $"基于最近 {historyDays} 天录像占用 {FormatGB(historyBytes)} 估算";
                }
            }
            else if (savedDays > 0 && usedBytes > 0)
            {
                avgGBPerDay = BytesToGB(usedBytes) / savedDays;
                if (avgGBPerDay > 0 && totalBytes > 0)
                {
                    estimatedRetentionDays = BytesToGB(totalBytes) / avgGBPerDay;
                    historyDays = savedDays;
                    historyBytes = usedBytes;
                    estimateBasis = "基于当前已保存录像估算，结果仅供参考";
                }
            }

            var pathDtos = configuredPaths.Select(path =>
            {
                long pathUsed = existingRecords
                    .Where(x => IsPathUnderDirectory(x.FilePath, path.Path))
                    .Sum(x => GetExistingFileSize(x.FilePath, x.FileSizeBytes));
                long pathFree = Math.Max(0, path.TotalBytes - pathUsed);
                return new
                {
                    path = path.DisplayPath,
                    totalGB = Math.Round(BytesToGB(path.TotalBytes), 1),
                    usedGB = Math.Round(BytesToGB(pathUsed), 1),
                    freeGB = Math.Round(BytesToGB(pathFree), 1),
                    available = path.Available
                };
            }).ToList();

            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                totalGB = Math.Round(BytesToGB(totalBytes), 1),
                usedGB = Math.Round(BytesToGB(usedBytes), 1),
                freeGB = Math.Round(BytesToGB(freeBytes), 1),
                oldestVideoTime = oldest?.ToString("yyyy-MM-dd HH:mm:ss"),
                latestVideoTime = latest?.ToString("yyyy-MM-dd HH:mm:ss"),
                savedDays,
                historyDays,
                historyUsedGB = Math.Round(BytesToGB(historyBytes), 1),
                avgGBPerDay = Math.Round(avgGBPerDay, 2),
                estimatedRetentionDays = estimatedRetentionDays.HasValue ? Math.Round(estimatedRetentionDays.Value, 0) : (double?)null,
                estimateBasis,
                pathCount = configuredPaths.Count,
                paths = pathDtos
            }, _jsonOptions);
        }

        internal static IReadOnlyList<StorageLocation> GetStorageOverviewLocations(AppConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            return (config.StorageLocations ?? [])
                .Where(location => !string.IsNullOrWhiteSpace(location.Path))
                .Where(location => !StorageLocationResolver.IsBackupLocation(location))
                .OrderBy(location => location.Priority)
                .ToList();
        }

        private static AppConfig LoadAppConfig()
        {
            try
            {
                string configPath = AppPaths.ConfigPath;
                if (!File.Exists(configPath))
                {
                    var defaultConfig = new AppConfig();
                    AppConfig.NormalizeAfterLoad(defaultConfig);
                    return defaultConfig;
                }

                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(configPath)) ?? new AppConfig();
                AppConfig.NormalizeAfterLoad(config);
                return config;
            }
            catch
            {
                var config = new AppConfig();
                AppConfig.NormalizeAfterLoad(config);
                return config;
            }
        }

        private sealed class StoragePathInfo
        {
            public string Path { get; init; } = "";
            public string DisplayPath { get; init; } = "";
            public long TotalBytes { get; init; }
            public bool Available { get; init; }
        }

        private static StoragePathInfo BuildStoragePathInfo(StorageLocation loc)
        {
            string normalizedPath = NormalizeStoragePath(loc.Path);
            long capacityBytes = 0;
            bool available = false;
            try
            {
                if (StorageVolumeInfo.TryGet(normalizedPath, out StorageVolumeInfo volume))
                {
                    available = true;
                    long reserveBytes = StorageSpacePolicy.GetEffectiveReserveBytes(loc, volume);
                    capacityBytes = Math.Max(0, volume.AvailableFreeSpace - reserveBytes)
                        + GetDirectoryVideoBytes(normalizedPath);
                }
            }
            catch { }

            return new StoragePathInfo
            {
                Path = normalizedPath,
                DisplayPath = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                TotalBytes = Math.Max(0, capacityBytes),
                Available = available
            };
        }

        private static string NormalizeStoragePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            string normalized = Path.IsPathRooted(path) ? path : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            try { return Path.GetFullPath(normalized); }
            catch { return normalized; }
        }

        private static long GetDirectoryVideoBytes(string folderPath)
        {
            try
            {
                var dir = new DirectoryInfo(folderPath);
                if (!dir.Exists) return 0;
                return dir.EnumerateFiles("*.*", SearchOption.AllDirectories)
                    .Where(x => string.Equals(x.Extension, ".mp4", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(x.Extension, ".mkv", StringComparison.OrdinalIgnoreCase))
                    .Sum(x => x.Length);
            }
            catch
            {
                return 0;
            }
        }

        private static long GetExistingFileSize(string filePath, long fallbackBytes)
        {
            try
            {
                if (File.Exists(filePath))
                    return new FileInfo(filePath).Length;
            }
            catch { }
            return Math.Max(0, fallbackBytes);
        }

        private static bool IsPathUnderDirectory(string filePath, string directoryPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(directoryPath))
                    return false;
                string fullFile = Path.GetFullPath(filePath);
                string fullDir = Path.GetFullPath(directoryPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return fullFile.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static int CalculateSavedDays(DateTime? oldest, DateTime? latest)
        {
            if (!oldest.HasValue || !latest.HasValue) return 0;
            int days = (latest.Value.Date - oldest.Value.Date).Days + 1;
            return Math.Max(1, days);
        }

        private static double BytesToGB(long bytes) => bytes / 1073741824.0;

        private static string FormatGB(long bytes)
        {
            double gb = BytesToGB(bytes);
            return gb >= 10 ? $"{gb:F0}GB" : $"{gb:F1}GB";
        }

        private void HandleSearchVideos(HttpListenerContext ctx)
        {
            var qs = ctx.Request.QueryString;
            string keyword = qs["keyword"] ?? qs["q"] ?? "";

            DateTime? startDate = DateTime.TryParse(qs["start"], out var parsedStartDate) ? parsedStartDate : null;
            DateTime? endDate = DateTime.TryParse(qs["end"], out var parsedEndDate) ? parsedEndDate : null;

            int page = int.TryParse(qs["page"], out var p) ? Math.Max(1, p) : 1;
            int pageSize = int.TryParse(qs["size"], out var s) ? Math.Clamp(s, 1, 100) : 50;
            string deviceId = qs["deviceId"] ?? "";
            string sourceDeviceName = qs["sourceName"] ?? "";
            string sourceType = qs["sourceType"] ?? "";

            var result = _db.QueryVideosPaged(
                startDate,
                endDate,
                string.IsNullOrWhiteSpace(keyword) ? null : keyword,
                page,
                pageSize,
                includeDeleted: !string.IsNullOrWhiteSpace(keyword),
                sourceType: sourceType,
                deviceId: deviceId,
                sourceDeviceName: sourceDeviceName);
            int deviceTotal = result.Total;
            string requestingDeviceId = ctx.Request.Headers["X-EPM-Device-Id"]?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(deviceId) && !string.IsNullOrWhiteSpace(requestingDeviceId))
            {
                deviceTotal = _db.QueryVideosPaged(
                    startDate,
                    endDate,
                    string.IsNullOrWhiteSpace(keyword) ? null : keyword,
                    page: 1,
                    pageSize: 1,
                    sourceType: "external",
                    deviceId: requestingDeviceId).Total;
            }
            // SQL 层只取当前页，文件存在性仅对当前页记录检查。
            var paged = result.Records.Select(r => new
            {
                r.Id,
                r.OrderId,
                trackingNumber = r.TrackingNumber ?? "",
                sourceOrderId = r.SourceOrderId ?? "",
                buyerMessage = r.BuyerMessage ?? "",
                sellerMemo = r.SellerMemo ?? "",
                productInfo = r.ProductInfo ?? "",
                orderInfoPushTime = r.OrderInfoPushTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                orderInfo = DeserializeOrderInfoSnapshot(r.OrderInfoJson),
                r.Mode,
                r.FileName,
                filePath = r.FilePath ?? "",
                videoCodec = r.VideoCodec ?? "",
                sourceType = r.SourceType ?? "pc",
                sourceDeviceId = r.SourceDeviceId ?? "",
                sourceDeviceName = ResolveVideoSourceDisplayName(
                    r.SourceType,
                    r.SourceDeviceId,
                    r.SourceDeviceName,
                    r.SourceDeviceKind,
                    _nodeName),
                sourceDeviceKind = r.SourceDeviceKind ?? "",
                sourceSessionId = r.SourceSessionId ?? "",
                contentSha256 = r.ContentSha256 ?? "",
                sizeMB = Math.Round(r.FileSizeBytes / 1048576.0, 1),
                startTime = r.StartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                durationSec = Math.Round(r.DurationSeconds, 0),
                duration = TimeSpan.FromSeconds(r.DurationSeconds).ToString(@"mm\:ss"),
                status = r.IsDeleted ? "deleted" : (string.IsNullOrWhiteSpace(PlaybackFileResolver.ResolvePlaybackPath(r)) ? "missing" : "available"),
                statusReason = r.IsDeleted
                    ? (string.IsNullOrWhiteSpace(r.DeleteReason) ? "已清理" : r.DeleteReason)
                    : (string.IsNullOrWhiteSpace(PlaybackFileResolver.ResolvePlaybackPath(r)) ? "监控端未在记录的文件位置找到录像，可能已被移动、删除或清理" : ""),
                exists = !r.IsDeleted && !string.IsNullOrWhiteSpace(PlaybackFileResolver.ResolvePlaybackPath(r)),
                playUrl = $"/api/videos/{r.Id}/play?compat=0",
                thumbnailUrl = $"/api/videos/{r.Id}/thumbnail",
                remote = true
            });

            SendJson(ctx, 200, new { total = result.Total, deviceTotal, page, pageSize, data = paged });
        }

        private void HandleVideoSources(HttpListenerContext ctx)
        {
            var data = _db.GetVideoSources()
                .Where(source => string.Equals(
                        source.SourceType,
                        "pc",
                        StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(source.DeviceId))
                .Select(source => new
                {
                    sourceType = string.Equals(
                        source.SourceType,
                        "external",
                        StringComparison.OrdinalIgnoreCase)
                            ? "external"
                            : "pc",
                    deviceId = source.DeviceId ?? "",
                    name = string.Equals(
                        source.SourceType,
                        "external",
                        StringComparison.OrdinalIgnoreCase)
                            ? ResolveVideoSourceName(source.DeviceId, source.DeviceName)
                            : ResolveVideoSourceDisplayName(
                                source.SourceType,
                                source.DeviceId,
                                source.DeviceName,
                                "pc",
                                _nodeName),
                    videoCount = source.VideoCount
                })
                .GroupBy(
                    source => source.sourceType == "external"
                        ? $"{source.sourceType}:{source.name}"
                        : "pc:",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    sourceType = group.First().sourceType,
                    deviceId = group.Count() == 1 ? group.First().deviceId : "",
                    name = group.First().name,
                    videoCount = group.Sum(source => source.videoCount)
                });
            SendJson(ctx, 200, new { data });
        }

        private static string ResolveVideoSourceName(string deviceId, string deviceName)
        {
            if (!string.IsNullOrWhiteSpace(deviceName))
                return deviceName.Trim();
            string normalized = new((deviceId ?? "").Where(char.IsLetterOrDigit).ToArray());
            return normalized.Length == 0
                ? "手机设备"
                : $"设备 {normalized[^Math.Min(6, normalized.Length)..].ToUpperInvariant()}";
        }

        internal static string ResolveVideoSourceDisplayName(
            string sourceType,
            string deviceId,
            string deviceName,
            string deviceKind,
            string localNodeName)
        {
            if (!string.Equals(sourceType, "external", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(localNodeName))
                    return localNodeName.Trim();
                if (!string.IsNullOrWhiteSpace(deviceName))
                    return deviceName.Trim();
                return "电脑";
            }

            if (!string.IsNullOrWhiteSpace(deviceName))
                return deviceName.Trim();

            string normalized = new((deviceId ?? "").Where(char.IsLetterOrDigit).ToArray());
            if (normalized.Length > 0)
                return $"设备 {normalized[^Math.Min(6, normalized.Length)..].ToUpperInvariant()}";
            return string.Equals(deviceKind, "pc", StringComparison.OrdinalIgnoreCase)
                ? "电脑设备"
                : "手机设备";
        }

        private void HandleVideoStatuses(HttpListenerContext ctx)
        {
            long[] ids = (ctx.Request.QueryString["ids"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => long.TryParse(value, out long id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .Take(100)
                .ToArray();
            var records = _db.QueryVideoStatuses(ids);
            var data = ids.Select(id =>
            {
                records.TryGetValue(id, out VideoRecord record);
                bool exists = record != null
                    && !string.IsNullOrWhiteSpace(PlaybackFileResolver.ResolvePlaybackPath(record));
                string status = record == null || (!record.IsDeleted && !exists)
                    ? "missing"
                    : record.IsDeleted ? "deleted" : "available";
                string reason = record == null
                    ? "记录不存在"
                    : record.IsDeleted
                        ? (string.IsNullOrWhiteSpace(record.DeleteReason) ? "已清理" : record.DeleteReason)
                        : exists ? "" : "文件缺失";
                return new { id, status, exists, reason };
            });
            SendJson(ctx, 200, new { data });
        }

        // ───── API: 流式播放 (支持 Range) ─────
        private void HandlePlay(HttpListenerContext ctx, string path)
        {
            var record = FindRecordFromPath(path, "/play");
            Log($"HandlePlay: path={path}, record={(record != null ? $"Id={record.Id}, OrderId={record.OrderId}, VideoCodec='{record.VideoCodec}', FilePath='{record.FilePath}'" : "null")}");
            string resolvedPlayPath = record == null
                ? ""
                : PlaybackFileResolver.ResolvePlaybackPath(record);
            if (record == null || string.IsNullOrWhiteSpace(resolvedPlayPath))
            {
                Log($"HandlePlay: 文件不存在 filePath={record?.FilePath}");
                SendJson(ctx, 404, new { errorCode = "file_not_found", error = "文件不存在" });
                return;
            }

            string filePath = EnsureMp4ContainerForPlayback(ctx, record);
            if (string.IsNullOrEmpty(filePath))
                return;

            string codec = MobileBackupService.NormalizeVideoCodec(record.VideoCodec);
            bool compatMode = ctx.Request.QueryString["compat"] != "0";
            bool preflight = ctx.Request.QueryString["preflight"] == "1";
            bool allowTranscodeWhileRecording = ctx.Request.QueryString["allowTranscodeWhileRecording"] == "1";
            bool shouldTranscode = ShouldTranscodeForPlayback(codec, compatMode);
            bool recording = _isRecordingProvider();
            bool hasTranscodeCache = shouldTranscode && HasTranscodeCache(filePath);
            bool appleClient = IsApplePlaybackClientRequest(ctx);
            string userAgent = ctx.Request.UserAgent ?? "";
            if (userAgent.Length > 120) userAgent = userAgent.Substring(0, 120);
            Log($"HandlePlay: codec='{codec}', compat={(compatMode ? "1" : "0")}, 判定={(shouldTranscode ? "转码" : "直传")}, appleClient={appleClient}, ua={userAgent}");

            if (shouldTranscode && recording && !allowTranscodeWhileRecording && !hasTranscodeCache)
            {
                SendJson(ctx, 409, new
                {
                    requiresConfirmation = true,
                    message = "正在录制，视频转码为 H.264 可能影响实时预览和录制稳定性。是否仍要继续转码播放？",
                    url = BuildPlayUrl(record.Id, compatMode, allowTranscodeWhileRecording: true)
                });
                return;
            }

            if (preflight)
            {
                SendJson(ctx, 200, new
                {
                    ok = true,
                    requiresConfirmation = false,
                    url = BuildPlayUrl(record.Id, compatMode, allowTranscodeWhileRecording),
                    videoCodec = codec,
                    playbackMode = shouldTranscode ? "transcode" : "direct"
                });
                return;
            }

            if (shouldTranscode)
            {
                ServeTranscodedStream(ctx, filePath);
            }
            else if (appleClient && IsHevcVideoCodec(codec)
                && filePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                ServeAppleCompatibleMp4(ctx, filePath);
            }
            else
            {
                ServeFileWithRange(ctx, filePath, inline: true);
            }
        }

        internal static bool IsHevcVideoCodec(string codec)
            => codec == "h265" || codec == "hevc";

        internal static bool ShouldTranscodeForPlayback(string codec, bool compatMode)
            => compatMode && MobileBackupService.NormalizeVideoCodec(codec) != "h264";

        private string EnsureMp4ContainerForPlayback(HttpListenerContext ctx, VideoRecord record)
        {
            string filePath = PlaybackFileResolver.ResolvePlaybackPath(record);
            if (string.IsNullOrWhiteSpace(filePath))
                return "";
            if (!filePath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                return filePath;

            if (IsCurrentRecordingFile(filePath))
            {
                Log($"EnsureMp4ContainerForPlayback: 拦截录制中文件点播 Id={record.Id}, OrderId={record.OrderId}, file={Path.GetFileName(filePath)}");
                RuntimeLog.Warn("WebPlayback", $"Blocked current recording MKV playback id={record.Id}, file={Path.GetFileName(filePath)}");
                SendJson(ctx, 409, new
                {
                    recordingInProgress = true,
                    message = "视频正在录制，录制结束后可播放"
                });
                return "";
            }

            // 本地副本已清理时无法转换，只能直传归档 MKV。
            if (!File.Exists(record.FilePath))
                return filePath;

            string mp4Path = Path.ChangeExtension(filePath, ".mp4");
            if (File.Exists(mp4Path) && new FileInfo(mp4Path).Length > 0)
            {
                _db.UpdateVideoFilePath(filePath, mp4Path);
                return mp4Path;
            }

            if (_mkvConverter == null)
            {
                SendJson(ctx, 500, new { errorCode = "transcoder_unavailable", error = "服务器未配置 MKV 转 MP4 转换器" });
                return "";
            }

            Log($"EnsureMp4ContainerForPlayback: 优先转换 {filePath}");
            var result = _mkvConverter(record);
            if (!result.Success || string.IsNullOrWhiteSpace(result.FilePath) || !File.Exists(result.FilePath))
            {
                SendJson(ctx, 500, new { error = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "MKV 转 MP4 失败" : result.ErrorMessage });
                return "";
            }

            return result.FilePath;
        }

        private bool IsCurrentRecordingFile(string filePath)
        {
            if (!_isRecordingProvider())
                return false;

            string currentPath = _currentRecordingFileProvider();
            return IsSamePath(filePath, currentPath);
        }

        private static bool IsSamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            try
            {
                left = Path.GetFullPath(left);
                right = Path.GetFullPath(right);
            }
            catch { }

            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildPlayUrl(long id, bool compatMode, bool allowTranscodeWhileRecording)
        {
            var url = $"/api/videos/{Uri.EscapeDataString(id.ToString())}/play?compat={(compatMode ? "1" : "0")}";
            if (allowTranscodeWhileRecording)
                url += "&allowTranscodeWhileRecording=1";
            return url;
        }

        private bool HasTranscodeCache(string filePath)
        {
            string cachePath = GetTranscodeCachePath(filePath);
            return File.Exists(cachePath) && new FileInfo(cachePath).Length > 0;
        }

        private string GetTranscodeCachePath(string filePath)
        {
            string cacheKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(filePath))).Substring(0, 16);
            return Path.Combine(_transCacheDir, $"{cacheKey}.mp4");
        }

        private static string GetAppleCompatibleCachePath(string filePath)
        {
            string cacheKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(filePath))).Substring(0, 16);
            return Path.Combine(_transCacheDir, $"{cacheKey}.apple.mp4");
        }

        /// <summary>
        /// Apple 客户端直传 HEVC 原片前，把 MP4 的 hev1 采样标签流拷贝为 hvc1 并缓存。
        /// 苹果解码器不识别 NVENC 默认写出的 hev1 标签（白屏或 Cannot Decode），
        /// hvc1 则可原生硬解。纯流拷贝不重新编码，只做一次并复用缓存；
        /// ffmpeg 不可用或拷贝失败时回退直传原片，不影响其他客户端。
        /// </summary>
        private void ServeAppleCompatibleMp4(HttpListenerContext ctx, string filePath)
        {
            string cachePath = GetAppleCompatibleCachePath(filePath);
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
            {
                Log($"ServeAppleCompatibleMp4: 命中 Apple 兼容缓存 {cachePath}");
                ServeFileWithRange(ctx, cachePath, inline: true);
                return;
            }

            string ffmpegPath = AppPaths.FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                Log("ServeAppleCompatibleMp4: 未找到 ffmpeg，直接传输原片");
                ServeFileWithRange(ctx, filePath, inline: true);
                return;
            }

            Directory.CreateDirectory(_transCacheDir);
            string tmpPath = Path.Combine(_transCacheDir, $"{Guid.NewGuid():N}.tmp.mp4");
            string args = $"-loglevel warning -y -i \"{filePath}\" -map 0:v:0 -map 0:a? -c copy -tag:v hvc1 -movflags +faststart -f mp4 \"{tmpPath}\"";

            IDisposable ffmpegSlot = null;
            try
            {
                ffmpegSlot = _ffmpegWorkLimiter.Enter(_cts.Token);
                Log($"ServeAppleCompatibleMp4: 流拷贝 hev1→hvc1 {filePath}");
                if (!TryRunFFmpeg(ffmpegPath, args, tmpPath))
                {
                    Log("ServeAppleCompatibleMp4: 流拷贝失败，直接传输原片");
                    try { File.Delete(tmpPath); } catch { }
                    ServeFileWithRange(ctx, filePath, inline: true);
                    return;
                }
                try { File.Move(tmpPath, cachePath, overwrite: true); } catch { }
                Task.Run(CleanWebCache);
                ServeFileWithRange(ctx, File.Exists(cachePath) ? cachePath : tmpPath, inline: true);
            }
            catch (OperationCanceledException)
            {
                try { File.Delete(tmpPath); } catch { }
                try { ctx.Response.Abort(); } catch { }
            }
            finally
            {
                ffmpegSlot?.Dispose();
            }
        }

        // ───── FFmpeg 转码：命中缓存直接 Range 传输，否则边转码边推流 + 同时写缓存 ─────
        private void ServeTranscodedStream(HttpListenerContext ctx, string filePath)
        {
            string ffmpegPath = AppPaths.FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                SendJson(ctx, 500, new { errorCode = "ffmpeg_not_found", error = "服务器未找到 ffmpeg.exe，无法转码播放" });
                return;
            }

            string cachePath = GetTranscodeCachePath(filePath);

            if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
            {
                // 命中缓存 → 标准 Range 传输（支持进度条拖拽、总时长正确）
                Log($"ServeTranscodedStream: 命中缓存 {cachePath}");
                ServeFileWithRange(ctx, cachePath, inline: true);
                return;
            }

            bool transcodeSlotEntered = false;
            IDisposable ffmpegSlot = null;
            try
            {
                _transcodeSlot.Wait(_cts.Token);
                transcodeSlotEntered = true;

                // 等待期间相同视频可能已经完成转码，复查缓存以避免重复启动 FFmpeg。
                if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
                {
                    _transcodeSlot.Release();
                    transcodeSlotEntered = false;
                    Log($"ServeTranscodedStream: 等待后命中缓存 {cachePath}");
                    ServeFileWithRange(ctx, cachePath, inline: true);
                    return;
                }

                ffmpegSlot = _ffmpegWorkLimiter.Enter(_cts.Token);

                // 首次播放 → 边转码边推流，同时写入缓存文件
                Directory.CreateDirectory(_transCacheDir);
                string tmpPath = cachePath + ".tmp";

                // 流式转码：缩到 480p + 极速设置，确保转码速度 > 实时播放速度
                string scaleFilter = "-vf scale=-2:480";
                string hwArgs = $"-loglevel warning -hwaccel auto -i \"{filePath}\" {scaleFilter} -c:v h264_nvenc -preset p1 -cq 30 -c:a aac -b:a 96k -movflags frag_keyframe+empty_moov+default_base_moof -f mp4 pipe:1";
                string swArgs = $"-loglevel warning -i \"{filePath}\" {scaleFilter} -c:v libx264 -preset ultrafast -tune zerolatency -crf 28 -c:a aac -b:a 96k -movflags frag_keyframe+empty_moov+default_base_moof -f mp4 pipe:1";

                // iOS AVPlayer 依赖 Range/206：边转码边推流的 chunked 响应会被判定为
                // serverIncorrectlyConfigured(-12939)。Apple 客户端先完整转码进缓存，
                // 再按标准 Range 传输；浏览器继续保留边转码边推流的低延迟行为。
                if (IsApplePlaybackClientRequest(ctx))
                {
                    bool transcoded = TranscodeToFile(ffmpegPath, hwArgs, tmpPath);
                    if (!transcoded)
                    {
                        Log("ServeTranscodedStream: NVENC 预转码失败，回退 CPU");
                        transcoded = TranscodeToFile(ffmpegPath, swArgs, tmpPath);
                    }
                    if (!transcoded)
                    {
                        try { File.Delete(tmpPath); } catch { }
                        SendJson(ctx, 500, new { errorCode = "transcode_failed", error = "电脑端转码失败，请稍后重试" });
                        return;
                    }
                    try { File.Move(tmpPath, cachePath, overwrite: true); } catch { }
                    Task.Run(CleanWebCache);
                    ServeFileWithRange(ctx, File.Exists(cachePath) ? cachePath : tmpPath, inline: true);
                    return;
                }

                if (!StreamTranscodeToClient(ctx, ffmpegPath, hwArgs, tmpPath))
                {
                    Log("ServeTranscodedStream: NVENC 流式转码失败，回退 CPU");
                    if (!StreamTranscodeToClient(ctx, ffmpegPath, swArgs, tmpPath))
                    {
                        try { File.Delete(tmpPath); } catch { }
                        return; // 响应已在内部处理
                    }
                }

                // 转码成功，将临时文件提升为正式缓存
                try { File.Move(tmpPath, cachePath, overwrite: true); } catch { }
                Task.Run(CleanWebCache);
            }
            catch (OperationCanceledException)
            {
                try { ctx.Response.Abort(); } catch { }
            }
            finally
            {
                ffmpegSlot?.Dispose();
                if (transcodeSlotEntered)
                    _transcodeSlot.Release();
            }
        }

        private static bool IsApplePlaybackClientRequest(HttpListenerContext ctx)
            => IsApplePlaybackClientUserAgent(ctx.Request.UserAgent);

        /// <summary>
        /// 识别 Apple 媒体播放客户端：iOS/AVFoundation 的媒体请求带 AppleCoreMedia UA；
        /// Apple 各端 Safari 的 video 媒体请求使用普通 Safari UA，不含 AppleCoreMedia，
        /// 因此额外识别“Safari/ 且非 Chrome/Edge/Opera，且设备为 Macintosh/iPhone/iPad/iPod”，
        /// 让 Safari 同样进入 hev1→hvc1 拷贝与 Range 转码分支。
        /// </summary>
        internal static bool IsApplePlaybackClientUserAgent(string userAgent)
        {
            if (userAgent == null)
                return false;

            if (userAgent.Contains("AppleCoreMedia", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!userAgent.Contains("Safari/", StringComparison.OrdinalIgnoreCase)
                || userAgent.Contains("Chrome/", StringComparison.OrdinalIgnoreCase)
                || userAgent.Contains("Edg/", StringComparison.OrdinalIgnoreCase)
                || userAgent.Contains("OPR/", StringComparison.OrdinalIgnoreCase))
                return false;

            return userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)
                || userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
                || userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase)
                || userAgent.Contains("iPod", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 启动 FFmpeg，将 stdout 同时推送给浏览器和写入缓存文件。
        /// 返回 true 表示 FFmpeg 正常退出且数据已发送。
        /// </summary>
        private bool StreamTranscodeToClient(HttpListenerContext ctx, string ffmpegPath, string args, string tmpPath)
        {
            Log($"StreamTranscodeToClient: {args}");
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            Process proc = null;
            FileStream cacheFs = null;
            try
            {
                proc = Process.Start(psi);
                if (proc == null) return false;

                // 异步消费 stderr
                var stderrBuf = new StringBuilder();
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuf.AppendLine(e.Data); };
                proc.BeginErrorReadLine();

                ctx.Response.ContentType = "video/mp4";
                ctx.Response.StatusCode = 200;
                ctx.Response.SendChunked = true;

                cacheFs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[65536];
                using var stdout = proc.StandardOutput.BaseStream;
                int read;
                bool clientOk = true;
                while ((read = stdout.Read(buffer, 0, buffer.Length)) > 0)
                {
                    // 写缓存
                    try { cacheFs.Write(buffer, 0, read); } catch { }
                    // 推给浏览器
                    if (clientOk)
                    {
                        try { ctx.Response.OutputStream.Write(buffer, 0, read); }
                        catch { clientOk = false; } // 客户端断开，继续写缓存
                    }
                }

                cacheFs.Close();
                cacheFs = null;
                try { ctx.Response.OutputStream.Close(); } catch { }
                proc.WaitForExit(5000);
                string stderr = stderrBuf.ToString();
                Log($"StreamTranscodeToClient: 退出码={proc.ExitCode}, stderr={stderr}");

                if (proc.ExitCode != 0)
                {
                    try { File.Delete(tmpPath); } catch { }
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log($"StreamTranscodeToClient 异常: {ex.Message}");
                try { ctx.Response.Abort(); } catch { }
                try { File.Delete(tmpPath); } catch { }
                return false;
            }
            finally
            {
                cacheFs?.Dispose();
                if (proc != null && !proc.HasExited) { try { proc.Kill(); } catch { } }
                proc?.Dispose();
            }
        }

        /// <summary>
        /// 启动 FFmpeg 并将 stdout 完整写入临时文件，不向客户端发送字节。
        /// 返回 true 表示 FFmpeg 正常退出且已生成非空文件。
        /// </summary>
        private bool TranscodeToFile(string ffmpegPath, string args, string tmpPath)
        {
            Log($"TranscodeToFile: {args}");
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            Process proc = null;
            FileStream cacheFs = null;
            try
            {
                proc = Process.Start(psi);
                if (proc == null) return false;

                var stderrBuf = new StringBuilder();
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuf.AppendLine(e.Data); };
                proc.BeginErrorReadLine();

                cacheFs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[65536];
                using var stdout = proc.StandardOutput.BaseStream;
                int read;
                while ((read = stdout.Read(buffer, 0, buffer.Length)) > 0)
                    cacheFs.Write(buffer, 0, read);

                cacheFs.Close();
                cacheFs = null;
                proc.WaitForExit(5000);
                string stderr = stderrBuf.ToString();
                Log($"TranscodeToFile: 退出码={proc.ExitCode}, stderr={stderr}");
                return proc.ExitCode == 0
                    && File.Exists(tmpPath)
                    && new FileInfo(tmpPath).Length > 0;
            }
            catch (Exception ex)
            {
                Log($"TranscodeToFile 异常: {ex.Message}");
                try { File.Delete(tmpPath); } catch { }
                return false;
            }
            finally
            {
                cacheFs?.Dispose();
                if (proc != null && !proc.HasExited) { try { proc.Kill(); } catch { } }
                proc?.Dispose();
            }
        }

        // ───── Web 临时缓存清理：超过上限时按最旧访问时间删除 ─────
        private void CleanWebCache()
        {
            try
            {
                var files = EnumerateWebCacheFiles()
                    .OrderBy(f => GetCacheSortTimeUtc(f))
                    .ToList();

                long totalSize = files.Sum(f => f.Length);
                if (totalSize <= _transCacheMaxBytes) return;

                Log($"CleanWebCache: 当前 {totalSize / 1048576}MB / 上限 {_transCacheMaxBytes / 1048576}MB，开始清理");
                foreach (var f in files)
                {
                    if (totalSize <= _transCacheMaxBytes * 0.8) break; // 清到 80% 水位
                    try
                    {
                        long size = f.Length;
                        f.Delete();
                        totalSize -= size;
                        Log($"CleanWebCache: 删除 {f.FullName} ({size / 1048576}MB)");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log($"CleanWebCache 异常: {ex.Message}");
            }
        }

        private static IEnumerable<FileInfo> EnumerateWebCacheFiles()
        {
            foreach (var file in EnumerateCacheFiles(AppPaths.TranscodeCacheDir, "*.mp4"))
                yield return file;
            foreach (var file in EnumerateCacheFiles(AppPaths.ClipPreviewDir, "*.jpg"))
                yield return file;
            foreach (var file in EnumerateCacheFiles(AppPaths.ClipsDir, "*.mp4"))
                yield return file;
        }

        private static IEnumerable<FileInfo> EnumerateCacheFiles(string directory, string pattern)
        {
            if (!Directory.Exists(directory))
                yield break;

            foreach (var file in new DirectoryInfo(directory).GetFiles(pattern, SearchOption.TopDirectoryOnly))
                yield return file;
        }

        private static DateTime GetCacheSortTimeUtc(FileInfo file)
        {
            return file.LastAccessTimeUtc > DateTime.MinValue ? file.LastAccessTimeUtc : file.LastWriteTimeUtc;
        }

        // ───── 运行 FFmpeg 转码，返回是否成功 ─────
        private bool TryRunFFmpeg(string ffmpegPath, string args, string outputPath)
        {
            Log($"TryRunFFmpeg: {args}");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(120_000);
                Log($"TryRunFFmpeg: 退出码={proc.ExitCode}, stderr={stderr}");
                return proc.ExitCode == 0 && File.Exists(outputPath);
            }
            catch (Exception ex)
            {
                Log($"TryRunFFmpeg 异常: {ex.Message}");
                try { File.Delete(outputPath); } catch { }
                return false;
            }
        }

        // ───── API: 剪辑预览 / 剪辑任务 ─────
        private void HandleClipPreview(HttpListenerContext ctx, string path)
        {
            try
            {
                if (!TryFindVideoId(path, "/clip/preview", out long id))
                {
                    SendJson(ctx, 400, new { success = false, errorCode = "invalid_video_id", error = "视频 ID 无效" });
                    return;
                }

                var request = ReadJsonBody<ClipRangeRequest>(ctx);
                var result = _clipService.CreatePreview(id, request.StartSeconds, request.EndSeconds, request.PreviewSide);
                SendJson(ctx, 200, result);
            }
            catch (Exception ex)
            {
                Log($"HandleClipPreview 异常: {ex.Message}");
                SendJson(ctx, 400, new { success = false, error = ex.Message });
            }
        }

        private void HandleClipPrewarm(HttpListenerContext ctx, string path)
        {
            try
            {
                if (!TryFindVideoId(path, "/clip/prewarm", out long id))
                {
                    SendJson(ctx, 400, new { success = false, errorCode = "invalid_video_id", error = "视频 ID 无效" });
                    return;
                }

                var request = ReadJsonBody<ClipRangeRequest>(ctx);
                _clipService.PrewarmPreviewFrames(id, request.StartSeconds, request.EndSeconds, request.PreviewSide);
                SendJson(ctx, 200, new { success = true });
            }
            catch (Exception ex)
            {
                Log($"HandleClipPrewarm 异常: {ex.Message}");
                SendJson(ctx, 400, new { success = false, error = ex.Message });
            }
        }

        private void HandleClipTimeline(HttpListenerContext ctx, string path)
        {
            try
            {
                if (!TryFindVideoId(path, "/clip/timeline", out long id))
                {
                    SendJson(ctx, 400, new { success = false, errorCode = "invalid_video_id", error = "视频 ID 无效" });
                    return;
                }

                var request = ReadJsonBody<ClipRangeRequest>(ctx);
                var result = request.FrameIndex >= 0
                    ? _clipService.CreateTimelinePreviewFrame(id, request.FrameCount, request.FrameIndex)
                    : _clipService.CreateTimelinePreviews(id, request.FrameCount);
                SendJson(ctx, 200, result);
            }
            catch (Exception ex)
            {
                Log($"HandleClipTimeline 异常: {ex.Message}");
                SendJson(ctx, 400, new { success = false, error = ex.Message });
            }
        }

        private void HandleClipFrame(HttpListenerContext ctx, string path)
        {
            try
            {
                if (!TryFindVideoId(path, "/clip/frame", out long id))
                {
                    SendJson(ctx, 400, new { success = false, errorCode = "invalid_video_id", error = "视频 ID 无效" });
                    return;
                }

                var request = ReadJsonBody<ClipRangeRequest>(ctx);
                var result = _clipService.CreatePreviewFrame(id, request.Seconds);
                SendJson(ctx, 200, result);
            }
            catch (Exception ex)
            {
                Log($"HandleClipFrame 异常: {ex.Message}");
                SendJson(ctx, 400, new { success = false, error = ex.Message });
            }
        }

        private void HandleStartClip(HttpListenerContext ctx, string path)
        {
            try
            {
                if (!TryFindVideoId(path, "/clip", out long id))
                {
                    SendJson(ctx, 400, new { success = false, errorCode = "invalid_video_id", error = "视频 ID 无效" });
                    return;
                }

                var request = ReadJsonBody<ClipRangeRequest>(ctx);
                string taskId = _clipService.StartClip(id, request.StartSeconds, request.EndSeconds);
                SendJson(ctx, 200, new { success = true, taskId });
            }
            catch (Exception ex)
            {
                Log($"HandleStartClip 异常: {ex.Message}");
                SendJson(ctx, 400, new { success = false, error = ex.Message });
            }
        }

        private void HandleGetClipTask(HttpListenerContext ctx, string path)
        {
            string taskId = Path.GetFileName(path);
            var task = _clipService.GetTask(taskId);
            if (task == null)
            {
                SendJson(ctx, 404, new { success = false, errorCode = "clip_task_not_found", status = "not_found", message = "剪辑任务不存在", downloadUrl = "" });
                return;
            }

            SendJson(ctx, 200, task);
        }

        private void HandleCancelClipTask(HttpListenerContext ctx, string path)
        {
            string taskId = path.Replace("/api/clip-tasks/", "").Replace("/cancel", "").Trim('/');
            var task = _clipService.CancelTask(taskId);
            if (task == null)
            {
                SendJson(ctx, 404, new { success = false, errorCode = "clip_task_not_found", status = "not_found", message = "剪辑任务不存在", downloadUrl = "" });
                return;
            }

            SendJson(ctx, 200, task);
        }

        private void HandleServeClip(HttpListenerContext ctx, string path)
        {
            string fileName = Path.GetFileName(path);
            string filePath = _clipService.ResolveClipPath(fileName);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                SendJson(ctx, 404, new { errorCode = "clip_file_not_found", error = "剪辑文件不存在" });
                return;
            }

            ServeFileWithRange(ctx, filePath, inline: ShouldServeClipInline(ctx.Request.QueryString["inline"]));
        }

        internal static bool ShouldServeClipInline(string value)
        {
            return string.Equals(value, "1", StringComparison.Ordinal);
        }

        private void HandleServeClipPreview(HttpListenerContext ctx, string path)
        {
            string fileName = Path.GetFileName(path);
            string filePath = _clipService.ResolvePreviewPath(fileName);
            if (string.IsNullOrWhiteSpace(filePath))
            {
                SendJson(ctx, 404, new { error = "预览图不存在" });
                return;
            }

            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.StatusCode = 200;
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            ctx.Response.ContentLength64 = fs.Length;
            fs.CopyTo(ctx.Response.OutputStream);
            ctx.Response.OutputStream.Close();
        }

        internal static OrderInfo DeserializeOrderInfoSnapshot(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<OrderInfo>(json, _jsonOptions); }
            catch (JsonException) { return null; }
        }

        private void HandleVideoThumbnail(HttpListenerContext ctx, string path)
        {
            try
            {
                if (!TryFindVideoId(path, "/thumbnail", out long id))
                {
                    SendJson(ctx, 400, new { errorCode = "invalid_video_id", error = "视频 ID 无效" });
                    return;
                }

                ClipPreviewFrameResult result = _clipService.CreateThumbnail(id);
                HandleServeClipPreview(ctx, result.Url);
            }
            catch (FileNotFoundException)
            {
                SendJson(ctx, 404, new { errorCode = "file_not_found", error = "录像文件不存在" });
            }
            catch (Exception ex)
            {
                Log($"HandleVideoThumbnail 异常: {ex.Message}");
                SendJson(ctx, 500, new { errorCode = "thumbnail_failed", error = "预览图生成失败" });
            }
        }

        private T ReadJsonBody<T>(HttpListenerContext ctx)
        {
            string body = ReadRequestBody(ctx, MaxJsonBodyBytes);
            return JsonSerializer.Deserialize<T>(body, _jsonOptions) ?? throw new InvalidDataException("请求内容无效");
        }

        private string ReadRequestBody(HttpListenerContext ctx, int maxBytes)
        {
            byte[] bytes = ReadRequestBytes(ctx, maxBytes);
            Encoding encoding = ctx.Request.ContentEncoding ?? Encoding.UTF8;
            return encoding.GetString(bytes);
        }

        private byte[] ReadRequestBytes(HttpListenerContext ctx, int maxBytes)
        {
            if (_authenticatedRequestBodies.TryRemove(ctx, out byte[] authenticatedBody))
                return authenticatedBody;
            return ReadRequestBytesCore(ctx, maxBytes);
        }

        private static byte[] ReadRequestBytesCore(HttpListenerContext ctx, int maxBytes)
        {
            long contentLength = ctx.Request.ContentLength64;
            if (contentLength > maxBytes)
                throw new InvalidDataException($"请求内容过大，最大允许 {maxBytes / 1024} KB");

            int capacity = contentLength > 0 ? (int)Math.Min(contentLength, maxBytes) : 0;
            using var buffer = new MemoryStream(capacity);
            byte[] chunk = new byte[8192];
            int totalBytes = 0;
            while (true)
            {
                int read = ctx.Request.InputStream.Read(chunk, 0, chunk.Length);
                if (read <= 0) break;
                totalBytes += read;
                if (totalBytes > maxBytes)
                    throw new InvalidDataException($"请求内容过大，最大允许 {maxBytes / 1024} KB");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }

        internal static void ValidateOrderInfoItems(List<OrderInfo> items)
        {
            if (items.Count > MaxOrderInfoItems)
                throw new InvalidDataException($"单次最多推送 {MaxOrderInfoItems} 条订单");

            foreach (OrderInfo item in items)
            {
                if (item == null)
                    throw new InvalidDataException("订单数据包含空项");
                ValidateFieldLength(item.TrackingNumber, 128, "快递单号");
                ValidateFieldLength(item.OrderId, 128, "订单号");
                ValidateFieldLength(item.BuyerMessage, 2000, "买家留言");
                ValidateFieldLength(item.SellerMemo, 2000, "卖家备注");
                ValidateFieldLength(item.ProductInfo, 4000, "商品信息");
                if (item.TotalItemCount < 0 || item.TotalItemCount > 100000)
                    throw new InvalidDataException("商品总件数必须在 0 到 100000 之间");
                if (item.MergedOrderCount < 0 || item.MergedOrderCount > MaxOrderInfoItems)
                    throw new InvalidDataException($"聚合订单数量必须在 0 到 {MaxOrderInfoItems} 之间");
                ValidateFieldLength(item.ProviderId, 128, "来源标识");
                ValidateFieldLength(item.RefundStatus, 256, "退款状态");
                ValidateFieldLength(item.RefundProductInfo, 4000, "退款商品信息");
            }
        }

        private static void ValidateFieldLength(string value, int maxLength, string fieldName)
        {
            if ((value?.Length ?? 0) > maxLength)
                throw new InvalidDataException($"{fieldName}过长，最多允许 {maxLength} 个字符");
        }

        private static bool TryFindVideoId(string path, string suffix, out long id)
        {
            id = 0;
            string idStr = path.Replace("/api/videos/", "").Replace(suffix, "").Trim('/');
            return long.TryParse(idStr, out id);
        }

        // ───── API: 下载 ─────
        private void HandleDownload(HttpListenerContext ctx, string path)
        {
            var record = FindRecordFromPath(path, "/download");
            string resolvedDownloadPath = record == null
                ? ""
                : PlaybackFileResolver.ResolvePlaybackPath(record);
            if (record == null || string.IsNullOrWhiteSpace(resolvedDownloadPath))
            {
                SendJson(ctx, 404, new { error = "文件不存在" });
                return;
            }

            ServeFileWithRange(ctx, resolvedDownloadPath, inline: false);
        }

        // ───── 文件传输 (支持 Range 请求实现拖拽播放) ─────
        private static void ServeFileWithRange(HttpListenerContext ctx, string filePath, bool inline)
        {
            var fi = new FileInfo(filePath);
            long fileLength = fi.Length;
            string ext = fi.Extension.ToLowerInvariant();
            string mime = ext switch { ".mp4" => "video/mp4", ".mkv" => "video/x-matroska", _ => "application/octet-stream" };

            ctx.Response.ContentType = mime;
            if (!inline)
            {
                ctx.Response.Headers.Add("Content-Disposition", $"attachment; filename=\"{Uri.EscapeDataString(fi.Name)}\"");
            }
            ctx.Response.Headers.Add("Accept-Ranges", "bytes");

            string rangeHeader = ctx.Request.Headers["Range"];
            long start = 0, end = fileLength - 1;

            if (!string.IsNullOrWhiteSpace(rangeHeader))
            {
                if (!TryResolveByteRange(rangeHeader, fileLength, out start, out end))
                {
                    ctx.Response.StatusCode = 416;
                    ctx.Response.Headers.Add("Content-Range", $"bytes */{fileLength}");
                    ctx.Response.ContentLength64 = 0;
                    ctx.Response.OutputStream.Close();
                    return;
                }

                ctx.Response.StatusCode = 206;
                ctx.Response.Headers.Add("Content-Range", $"bytes {start}-{end}/{fileLength}");
            }
            else
            {
                ctx.Response.StatusCode = 200;
            }

            long length = end - start + 1;
            ctx.Response.ContentLength64 = length;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(start, SeekOrigin.Begin);
            byte[] buffer = new byte[65536];
            long remaining = length;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(buffer.Length, remaining);
                int read = fs.Read(buffer, 0, toRead);
                if (read == 0) break;
                try { ctx.Response.OutputStream.Write(buffer, 0, read); }
                catch { break; } // 客户端断开
                remaining -= read;
            }
            ctx.Response.OutputStream.Close();
        }

        internal static bool TryResolveByteRange(
            string rangeHeader,
            long fileLength,
            out long start,
            out long end)
        {
            start = 0;
            end = fileLength - 1;
            if (fileLength <= 0
                || string.IsNullOrWhiteSpace(rangeHeader)
                || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                return false;

            string rangeValue = rangeHeader[6..].Trim();
            if (rangeValue.Length == 0 || rangeValue.Contains(','))
                return false;

            string[] parts = rangeValue.Split('-', 2);
            if (parts.Length != 2)
                return false;

            if (parts[0].Length == 0)
            {
                if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long suffixLength)
                    || suffixLength <= 0)
                    return false;
                start = Math.Max(0, fileLength - suffixLength);
                end = fileLength - 1;
                return true;
            }

            if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start)
                || start < 0
                || start >= fileLength)
                return false;

            if (parts[1].Length == 0)
            {
                end = fileLength - 1;
                return true;
            }

            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end)
                || end < start)
                return false;
            end = Math.Min(end, fileLength - 1);
            return true;
        }

        // ───── 根据 URL 中的 ID 查找记录 ─────
        private VideoRecord FindRecordFromPath(string path, string suffix)
        {
            string idStr = path.Replace("/api/videos/", "").Replace(suffix, "").Trim('/');
            if (!long.TryParse(idStr, out long id)) return null;
            return _db.GetVideoById(id);
        }

        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

        // ───── JSON 响应 ─────
        private static void SendJson(HttpListenerContext ctx, int statusCode, object data)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, _jsonOptions);
            SendJsonBytes(ctx, statusCode, bytes);
        }

        private static void SendJsonBytes(HttpListenerContext ctx, int statusCode, byte[] bytes)
        {
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        // ───── 内嵌前端页面 ─────
        private static void ServeIndexPage(HttpListenerContext ctx)
        {
            string indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Web", "index.html");
            string html = File.Exists(indexPath)
                ? File.ReadAllText(indexPath, Encoding.UTF8)
                : MissingIndexHtml;

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            // 前端页面每次请求实时读取，禁止浏览器缓存，避免修改后仍显示旧页面。
            ctx.Response.Headers["Cache-Control"] = "no-store";
            byte[] bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private void ServeInstallGuidePage(HttpListenerContext ctx)
        {
            string authority = ctx.Request.Url?.Authority ?? $"127.0.0.1:{ctx.Request.LocalEndPoint?.Port ?? 5280}";
            IReadOnlyList<RecordingDeviceInfo> devices = GetRecordingDevices(authority, includeKnown: true);
            string scheme = ctx.Request.Url?.Scheme ?? "http";
            string scriptUrl = $"{scheme}://{authority}/PackingProof-Order-Integration-KDZS.user.js?scriptId={UserscriptCatalog.OfficialId}";
            var catalog = new UserscriptCatalog();
            string choices = string.Join("", catalog.GetAll(PrintToolInstallGuide.ResolveUserscriptPath())
                .Select(item => BuildUserscriptChoice(item, scheme, authority)));
            string html = PrintToolInstallGuide.RenderForWeb(devices, scriptUrl, choices);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            byte[] bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        internal static string BuildUserscriptChoice(UserscriptDescriptor item, string scheme, string authority)
        {
            string url = $"{scheme}://{authority}/api/userscripts/{Uri.EscapeDataString(item.Id)}/download";
            bool hasWarning = item.Warnings.Count > 0;
            string warning = hasWarning ? $"有提示：{string.Join("；", item.Warnings)}" : "可自动维护";
            string warningClass = hasWarning ? " has-warning" : " is-maintainable";
            return $"<div class=\"script-choice{warningClass}\"><div><strong>{WebUtility.HtmlEncode(item.Name)}</strong><span class=\"hint\">版本 {WebUtility.HtmlEncode(item.Version)} · {WebUtility.HtmlEncode(warning)}</span></div><a class=\"primary\" href=\"{WebUtility.HtmlEncode(url)}\" target=\"_blank\" rel=\"noopener\">安装</a></div>";
        }

        private void ServeUserscript(HttpListenerContext ctx, string scriptId = null)
        {
            string scriptPath = PrintToolInstallGuide.ResolveUserscriptPath();
            string selectedId = string.IsNullOrWhiteSpace(scriptId) ? UserscriptCatalog.OfficialId : Uri.UnescapeDataString(scriptId);
            scriptPath = _userscriptCatalog.GetSourcePath(selectedId, scriptPath);
            if (!File.Exists(scriptPath))
            {
                SendJson(ctx, 404, new { error = "userscript not found" });
                return;
            }

            string script = File.ReadAllText(scriptPath, Encoding.UTF8);
            string authority = ctx.Request.Url?.Authority ?? "";
            IReadOnlyList<RecordingDeviceInfo> devices = GetRecordingDevices(authority, includeKnown: true);
            if (devices.Count == 0)
            {
                SendJson(ctx, 409, new { error = "当前没有发现可接收订单的录像设备" });
                return;
            }

            string primaryAuthority = ResolveUserscriptPrimaryAuthority(authority);
            var host = new PackingProofNodeInfo
            {
                Protocol = PackingProofNodeInfo.ExpectedProtocol,
                ProtocolVersion = PackingProofNodeInfo.SupportedProtocolVersion,
                NodeId = _nodeId,
                NodeName = _nodeName,
                Preset = _deploymentPreset,
                Capabilities = PackingProofCapabilities.ForPreset(_deploymentPreset).ToList(),
                HttpPort = Port,
                Address = $"http://{primaryAuthority}"
            };
            script = PrintToolInstallGuide.AddRecordingDevices(script, devices, host);
            string scriptUrl =
                $"{ctx.Request.Url?.Scheme ?? "http"}://{primaryAuthority}/api/userscripts/{Uri.EscapeDataString(selectedId)}/download";
            script = PrintToolInstallGuide.AddUserscriptUpdateUrls(script, scriptUrl);
            string fingerprint = PrintToolInstallGuide.ComputeConfigFingerprint(devices, host);
            int revision = _userscriptConfigRevision.GetRevision(fingerprint);
            script = PrintToolInstallGuide.RewriteUserscriptVersion(script, revision);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/javascript; charset=utf-8";
            UserscriptDescriptor descriptor = _userscriptCatalog.GetAll(PrintToolInstallGuide.ResolveUserscriptPath()).FirstOrDefault(item => item.Id == selectedId);
            string downloadName = descriptor?.FileName;
            if (string.IsNullOrWhiteSpace(downloadName)) downloadName = PrintToolInstallGuide.ScriptFileName;
            ctx.Response.Headers["Content-Disposition"] = $"inline; filename=\"{Path.GetFileName(downloadName)}\"";
            byte[] bytes = Encoding.UTF8.GetBytes(script);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private List<Uri> GetUserscriptMonitorAddresses(HttpListenerContext ctx, string currentAuthority)
        {
            string primaryAuthority = ResolveUserscriptPrimaryAuthority(currentAuthority);
            var requested = new List<string> { primaryAuthority };
            requested.AddRange(_mobileOrderReceivers.GetAuthorities());
            string[] values = ctx.Request.QueryString.GetValues("connect") ?? Array.Empty<string>();
            requested.AddRange(values);
            List<Uri> addresses = PrintToolInstallGuide.NormalizeMonitorAddresses(requested);
            bool hasLanPrimary = Uri.TryCreate("http://" + primaryAuthority, UriKind.Absolute, out Uri primary)
                && !primary.IsLoopback;
            if (hasLanPrimary)
                addresses.RemoveAll(address => address.IsLoopback);
            return addresses;
        }

        private string ResolveUserscriptPrimaryAuthority(string currentAuthority)
        {
            if (Uri.TryCreate("http://" + currentAuthority, UriKind.Absolute, out Uri current)
                && !current.IsLoopback)
                return current.Authority;

            try
            {
                string accessUrl = _mobileConnectionUrlProvider()?.Trim() ?? "";
                if (Uri.TryCreate(accessUrl, UriKind.Absolute, out Uri lanAddress)
                    && !lanAddress.IsLoopback
                    && string.Equals(lanAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                    return lanAddress.Authority;
            }
            catch { }

            return currentAuthority;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            try { _udpDiscoveryResponder?.Dispose(); } catch { }
            try { _mobileAppUpdateRefreshTimer?.Dispose(); } catch { }
            foreach (var pending in _pendingOrderLookups.Values)
                pending.Completion.TrySetResult(new OrderLookupResult { Responded = false });
            _pendingOrderLookups.Clear();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }

            var shutdownTimer = System.Diagnostics.Stopwatch.StartNew();
            bool listenerStopped = WaitForListenLoop(ShutdownWaitTimeout);
            TimeSpan requestsWait = ShutdownWaitTimeout - shutdownTimer.Elapsed;
            bool requestsStopped = _requestsIdle.Wait(
                requestsWait > TimeSpan.Zero ? requestsWait : TimeSpan.Zero);
            TimeSpan backgroundWait = ShutdownWaitTimeout - shutdownTimer.Elapsed;
            bool backgroundStopped = WaitForVideoCodecBackfill(
                backgroundWait > TimeSpan.Zero ? backgroundWait : TimeSpan.Zero);
            if (listenerStopped && requestsStopped && backgroundStopped)
            {
                DisposeServerResources();
                return;
            }

            RuntimeLog.Warn(
                "WebServer",
                "Web server shutdown is still completing; resources will be released after active requests exit");
            _ = Task.Run(() =>
            {
                try
                {
                    WaitForListenLoop(Timeout.InfiniteTimeSpan);
                    _requestsIdle.Wait();
                    WaitForVideoCodecBackfill(Timeout.InfiniteTimeSpan);
                }
                finally
                {
                    DisposeServerResources();
                }
            });
        }

        private bool WaitForListenLoop(TimeSpan timeout)
        {
            Task listenTask = _listenTask;
            if (listenTask == null)
                return true;
            try
            {
                return listenTask.Wait(timeout);
            }
            catch
            {
                return listenTask.IsCompleted;
            }
        }

        private bool WaitForVideoCodecBackfill(TimeSpan timeout)
        {
            Task backfillTask = _videoCodecBackfillTask;
            if (backfillTask == null)
                return true;
            try
            {
                return backfillTask.Wait(timeout);
            }
            catch
            {
                return backfillTask.IsCompleted;
            }
        }

        private void DisposeServerResources()
        {
            if (Interlocked.Exchange(ref _serverResourcesDisposed, 1) != 0)
                return;
            try { _clipService.Dispose(); } catch { }
            try { _connectedClients.Dispose(); } catch { }
            _requestSlots.Dispose();
            _transcodeSlot.Dispose();
            _requestsIdle.Dispose();
            _cts.Dispose();
        }

        // ═══════════════════════════════════════════════
        //  内嵌 HTML 单页应用
        // ═══════════════════════════════════════════════
        private const string MissingIndexHtml = """
<!DOCTYPE html>
<html lang="zh-CN">
<head><meta charset="UTF-8"><title>页面文件缺失</title></head>
<body style="font-family: Microsoft YaHei UI, sans-serif; padding: 32px; color: #172033;">
  <h1>页面文件缺失</h1>
  <p>未找到 Web/index.html，请检查程序发布目录是否包含该文件。</p>
</body>
</html>
""";
    }
}
