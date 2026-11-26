#nullable disable
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Input;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.UpdateCore;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;
using AForge.Video;
using AForge.Video.DirectShow;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Services.Extensions;
using System.Drawing;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        private async Task<bool> RestartWebServerAsync(
            bool allowAccessSetup,
            bool showFailureToast = true)
        {
            if (_purposeSwitchPending)
                return false;
            await _webServerLifecycleLock.WaitAsync();
            if (_purposeSwitchPending)
            {
                _webServerLifecycleLock.Release();
                return false;
            }
            WebServer newServer = null;
            ExtensionRuntime newExtensionRuntime = null;
            try
            {
                Interlocked.Increment(ref _workstationAddressRefreshVersion);
                try { _udpDiscoveryResponder?.Dispose(); } catch { }
                _udpDiscoveryResponder = null;
                EffectiveProductCapabilities effectiveCapabilities =
                    ProductCapabilityRuntime.ForPreset(
                        Config.DeploymentPreset,
                        RuntimeCapabilityEnvironment.FromConfig(Config));
                bool providesWebPlayback =
                    effectiveCapabilities.WebHost.EnabledGroups.Any(group =>
                        string.Equals(
                            group.Name,
                            "media-library",
                            StringComparison.Ordinal));
                bool orderReceiverOnly = !providesWebPlayback;
                Interlocked.Increment(ref _workstationAddressRefreshVersion);
                WebServer previousServer = _webServer;
                _webServer = null;
                try { previousServer?.Dispose(); } catch { }
                ExtensionRuntime previousExtensionRuntime = _extensionRuntime;
                _extensionRuntime = null;
                try { previousExtensionRuntime?.Dispose(); } catch { }

                if (!effectiveCapabilities.WebHostEnabled || _db == null || _isDisposed)
                {
                    MonitorAccessAddress = "";
                    WorkstationPrintStatusText = "未连接";
                    WorkstationStatusToolTip = "开启局域网查看后，可点击手机/电脑连接查看二维码或复制网址";
                    SetConnectedDeviceUnavailable(AppLanguage.Get("Main.ConnectionServiceDisabled"), AppLanguage.Get("Main.ConnectionEmptyTip"));
                    if (effectiveCapabilities.Features.LanCollaboration
                        && effectiveCapabilities.IsHost
                        && _db != null
                        && !_isDisposed)
                    {
                        _udpDiscoveryResponder = new UdpDiscoveryResponder(
                            Config.NodeId,
                            httpPortProvider: () => 0,
                            isHostProvider: () => true,
                            webHostEnabledProvider: () => false,
                            routeGroupsProvider: () => []);
                        _udpDiscoveryResponder.Start();
                        RuntimeLog.Info(
                            "UdpDiscovery",
                            "LAN collaboration active without WebHost; announce webPort=0");
                    }
                    return true;
                }

                WorkstationPrintStatusText = orderReceiverOnly
                    ? "订单联动接收：等待启动"
                    : "启动中";
                SetConnectedDeviceUnavailable(AppLanguage.Get("Main.ConnectionServiceStarting"), AppLanguage.Get("Main.ConnectionEmptyTip"));
                int port = Config.WebServerPort;
                int cacheMaxMb = Config.TranscodeCacheMaxMB;
                bool enableOrderInfoLog = Config.EnableOrderInfoLog;
                bool requireAccessKey =
                    providesWebPlayback && Config.RequireWebAccessKey;
                string accessKey = Config.WebAccessKey;

                newServer = await Task.Run(() =>
                {
                    var server = new WebServer(
                        _db,
                        port,
                        cacheMaxMb,
                        () => IsRecording,
                        ConvertRecordMkvToMp4,
                        () => _currentVideoFilePath,
                        requireAccessKey,
                        accessKey,
                        mobileConnectionUrlProvider: BuildMonitorAccessUrl,
                        mobileBackupComputerId: Config.MobileBackupComputerId,
                        mobileBackupComputerName: Config.NodeName,
                        mobileBackupStateDirectory: AppPaths.MobileBackupStateDir,
                        mobileBackupRecordingRootResolver: ResolveBackupStoragePath,
                        mobileBackupRecordingRootsResolver: ResolveBackupStoragePaths,
                        mobileBackupArchiveTargetResolver: () =>
                        {
                            RecordingStoragePlan plan = StorageLocationResolver.ResolveRecordingPlan(
                                Config,
                                allowDefaultFallback: false);
                            return plan.RequiresNetworkArchive ? plan.ArchiveTarget : null;
                        },
                        mobileBackupArchivePendingCallback: () => _archiveService?.Wake(),
                        nodeId: Config.NodeId,
                        nodeName: Config.NodeName,
                        deploymentPreset: Config.DeploymentPreset,
                        orderReceiverOnly: orderReceiverOnly,
                        nodeNameCustomized: Config.NodeNameCustomized,
                        backupDeviceEnrollmentApprover: ApproveBackupDeviceEnrollment,
                        validatedEncodersProvider: () => Config.ValidatedEncodersCache,
                        effectiveCapabilities: effectiveCapabilities,
                        photoStore: effectiveCapabilities.Features.PhotoCapture
                            ? PhotoStore
                            : null,
                        photoDeleteHandler: effectiveCapabilities.Features.PhotoCapture
                            ? DeletePhotoAsync
                            : null,
                        photoArchiveRetryAsync: effectiveCapabilities.Features.PhotoCapture
                            ? RetryPhotoArchiveAsync
                            : null)
                    {
                        EnableOrderInfoLog = enableOrderInfoLog
                    };
                    if (effectiveCapabilities.Features.ExtensionApi)
                    {
                        _extensionAuthorizationStore ??= new ExtensionAuthorizationStore(
                            AppPaths.MobileBackupStateDir);
                        string extensionNodeId = Config.NodeId;
                        string extensionNodeName = Config.NodeName;
                        server.ConfigureExtensionEnrollment(
                            _extensionAuthorizationStore,
                            request => ExtensionEnrollmentApprovalPrompt.Show(
                                null,
                                request,
                                extensionNodeId,
                                extensionNodeName));
                        newExtensionRuntime = new ExtensionRuntime(
                            _db,
                            _dbFilePath,
                            Config.NodeId,
                            _extensionAuthorizationStore,
                            OnRecordingExtensionDataChanged,
                            OnExtensionOrderResolved);
                        server.ConfigureExtensionTaskApi(
                            newExtensionRuntime.Broker,
                            newExtensionRuntime.Coordinator,
                            newExtensionRuntime.ProcessAvailableResults);
                    }
                    try
                    {
                        server.OrderInfoReceived += OnOrderInfoReceived;
                        server.RecordingExtensionDataChanged += OnRecordingExtensionDataChanged;
                        server.ConnectedClientsChanged += OnConnectedClientsChanged;
                        server.MobileAppUpdateAvailable += OnMobileAppUpdateAvailable;
                        server.MobileBackupCompleted += OnMobileBackupCompleted;
                        server.Start(allowAccessSetup);
                        return server;
                    }
                    catch
                    {
                        server.Dispose();
                        throw;
                    }
                });

                if (_isDisposed)
                {
                    newServer.Dispose();
                    return false;
                }

                _webServer = newServer;
                newServer = null;
                _extensionRuntime = newExtensionRuntime;
                newExtensionRuntime = null;
                await RefreshWorkstationStatusAsync();
                QueueRecordingWorkstationHeartbeat(force: true);
                RuntimeLog.Info(
                    "Web",
                    $"LAN service started port={port}, cacheMaxMB={cacheMaxMb}, orderReceiverOnly={orderReceiverOnly}");
                return true;
            }
            catch (Exception ex)
            {
                try { newServer?.Dispose(); } catch { }
                try { newExtensionRuntime?.Dispose(); } catch { }
                RuntimeLog.Error("Web", "LAN service start failed", ex);
                string userMessage = WebServer.GetLanAccessFailureUserMessage(
                    repairAttempted: false,
                    exception: ex);
                MonitorAccessAddress = "";
                WorkstationPrintStatusText = IsRecordingWorkstation
                    ? "订单联动接收：启动失败"
                    : "启动失败";
                WorkstationStatusToolTip = userMessage;
                SetConnectedDeviceUnavailable(AppLanguage.Get("Main.ConnectionServiceUnavailable"), userMessage);
                if (showFailureToast)
                    ShowToast(userMessage, ToastSeverity.Error);
                return false;
            }
            finally
            {
                _webServerLifecycleLock.Release();
            }
        }

        private void OnRecordingExtensionDataChanged(string recordingSessionId, IReadOnlyList<RecordingExtensionField> fields)
        {
            if (string.IsNullOrWhiteSpace(recordingSessionId)
                || !string.Equals(recordingSessionId, _recordingSessionId, StringComparison.Ordinal))
                return;

            var lines = (fields ?? Array.Empty<RecordingExtensionField>())
                .Where(field => field != null && !string.IsNullOrWhiteSpace(field.FieldName))
                .OrderBy(field => field.Namespace, StringComparer.Ordinal)
                .ThenBy(field => field.FieldName, StringComparer.Ordinal)
                .Take(4)
                .Select(field =>
                {
                    string value = field.Value ?? "";
                    if (value.Length > 96) value = value[..96] + "…";
                    string prefix = string.IsNullOrWhiteSpace(field.Namespace)
                        ? field.FieldName
                        : $"{field.Namespace}.{field.FieldName}";
                    return $"{prefix}: {value}";
                })
                .ToArray();
            _recordingWatermarkSnapshot = new WatermarkSnapshot(recordingSessionId, lines);
        }

        private async Task RefreshWorkstationStatusAsync()
        {
            int version = Interlocked.Increment(ref _workstationAddressRefreshVersion);
            if (_webServer == null)
            {
                MonitorAccessAddress = "";
                WorkstationPrintStatusText = IsRecordingWorkstation
                    ? "订单联动接收：未连接"
                    : "未连接";
                WorkstationStatusToolTip = "其他设备暂时无法连接这台电脑";
                SetConnectedDeviceUnavailable(AppLanguage.Get("Main.ConnectionServiceDisabled"), AppLanguage.Get("Main.ConnectionEmptyTip"));
                return;
            }

            MonitorAccessAddress = "";
            WorkstationPrintStatusText = IsRecordingWorkstation
                ? "订单联动接收：等待连接"
                : "启动中";
            WorkstationStatusToolTip = "正在准备给其他电脑浏览器使用的网址。两台电脑需要在同一局域网内";
            SetConnectedDeviceUnavailable(AppLanguage.Get("Main.ConnectionServiceStarting"), AppLanguage.Get("Main.ConnectionEmptyTip"));

            string verifiedAddress;
            try
            {
                verifiedAddress = await WorkstationNetwork.GetVerifiedLocalAccessAddressAsync(Config.WebServerPort);
            }
            catch
            {
                verifiedAddress = WorkstationNetwork.GetBestLocalAccessAddress(Config.WebServerPort);
            }

            if (version != _workstationAddressRefreshVersion || _webServer == null)
                return;

            MonitorAccessAddress = verifiedAddress;
            if (IsRecordingWorkstation)
            {
                WorkstationPrintStatusText = $"订单联动接收 · {verifiedAddress}";
                WorkstationStatusToolTip = "此地址仅用于接收订单联动，不提供本机录像浏览或备份主机服务";
            }
            else
            {
                WorkstationPrintStatusText = "已就绪";
                WorkstationStatusToolTip = Config.RequireWebAccessKey
                    ? "访问保护已开启。请点击手机/电脑连接查看二维码或复制完整访问链接，再发送到需要查看录像的设备"
                    : $"其他电脑在浏览器输入 http://{MonitorAccessAddress}，即可搜索、下载和播放视频。若打不开，请确认两台电脑在同一局域网，并检查防火墙";
            }
            UpdateConnectedClients(_webServer.GetConnectedClients());
            RefreshMobileBackupStatuses();
        }

        private void OnConnectedClientsChanged(IReadOnlyList<ConnectedClientInfo> clients)
        {
            Application application = Application.Current;
            if (application == null || application.Dispatcher.CheckAccess())
            {
                UpdateConnectedClients(clients);
                return;
            }

            _ = application.Dispatcher.InvokeAsync(() =>
            {
                if (!_isDisposed) UpdateConnectedClients(clients);
            });
        }

        private void OnMobileAppUpdateAvailable(MobileAppUpdateAvailableInfo update)
        {
            Application application = Application.Current;
            if (application == null)
                return;

            _ = application.Dispatcher.InvokeAsync(() =>
            {
                if (_isDisposed)
                    return;
                ShowMobileAppUpdate(update);
            });
        }

        private VideoFolderImportService CreateVideoImportService(string activeFolderPath)
        {
            string preset = DeploymentPresets.Normalize(Config.DeploymentPreset);
            if (preset is not (DeploymentPresets.RecordingHost or DeploymentPresets.RecordingWorkstation)
                || _db == null)
                return null;

            IEnumerable<string> managedRoots = preset == DeploymentPresets.RecordingWorkstation
                ? [activeFolderPath]
                : Config.StorageLocations
                    .Where(location => StorageVolumeInfo.IsConfirmedLocal(location.Path))
                    .Select(location =>
                    {
                        try { return StorageLocationResolver.Resolve(location); }
                        catch { return ""; }
                    });
            return new VideoFolderImportService(
                _db,
                managedRoots,
                Config.NodeId,
                Config.NodeName);
        }

        private BackupDeviceEnrollmentApprovalDecision ApproveBackupDeviceEnrollment(
            BackupDeviceEnrollmentRequest request) =>
            BackupDeviceEnrollmentApprovalPrompt.Show(null, request);

        private void ShowMobileAppUpdate(MobileAppUpdateAvailableInfo update)
        {
            System.Windows.Window owner = Application.Current?.MainWindow;
            if (owner != null && owner.IsLoaded)
                MobileAppUpdatePrompt.Show(owner, update);
        }

        private void UpdateConnectedClients(IReadOnlyList<ConnectedClientInfo> clients)
        {
            if (_isDisposed) return;
            _connectedClientSnapshot = clients ?? [];
            int count = ConnectedClientRegistry.CountDistinctAddresses(clients);
            HasConnectedDevices = count > 0;
            ConnectedDeviceText = count > 0
                ? AppLanguage.Format("Main.ConnectedDevices", count)
                : AppLanguage.Get("Main.NoConnectedDevices");
            if (count == 0)
            {
                ConnectedDeviceToolTip = AppLanguage.Get("Main.ConnectionEmptyTip");
                RefreshMobileBackupStatuses();
                return;
            }

            string[] details = clients
                .GroupBy(client => GetConnectedClientTypeLabel(client.ClientType))
                .OrderBy(group => group.Key, StringComparer.CurrentCulture)
                .Select(group => $"{group.Key} {group.Count()}")
                .ToArray();
            ConnectedDeviceToolTip = string.Join("\n", details);
            RefreshMobileBackupStatuses();
        }

        private void OnMobileBackupCompleted(string deviceId, string deviceName)
        {
            RefreshMobileBackupStatuses();
        }

        private static string GetFallbackDeviceName(string deviceId, string deviceKind)
        {
            string normalized = new((deviceId ?? "")
                .Where(char.IsLetterOrDigit)
                .ToArray());
            string fallback = string.Equals(deviceKind, "pc", StringComparison.OrdinalIgnoreCase)
                ? "电脑设备"
                : "手机设备";
            return normalized.Length == 0
                ? fallback
                : $"{fallback} {normalized[^Math.Min(6, normalized.Length)..].ToUpperInvariant()}";
        }

        internal static bool ShouldIncludeBackupDeviceClient(
            ConnectedClientInfo client,
            string localNodeId)
        {
            if (client == null)
                return false;

            bool supportedType =
                string.Equals(client.ClientType, "mobile-app", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(client.ClientType, "recording-workstation", StringComparison.OrdinalIgnoreCase);
            if (!supportedType)
                return false;

            string deviceId = string.IsNullOrWhiteSpace(client.NodeId)
                ? client.ClientId
                : client.NodeId;
            return BackupDeviceIdentity.IsRemote(deviceId, localNodeId);
        }

        private void SetConnectedDeviceUnavailable(string text, string tooltip)
        {
            HasConnectedDevices = false;
            ConnectedDeviceText = text;
            ConnectedDeviceToolTip = tooltip;
        }

        private static string GetConnectedClientTypeLabel(string clientType) => clientType switch
        {
            "web-desktop" => AppLanguage.Get("Main.ClientWebDesktop"),
            "web-mobile" => AppLanguage.Get("Main.ClientWebMobile"),
            "userscript" => AppLanguage.Get("Main.ClientUserscript"),
            "print-station" => AppLanguage.Get("Main.ClientPrintStation"),
            "mobile-app" => AppLanguage.Get("Main.ClientMobileApp"),
            "recording-workstation" => AppLanguage.Get("录制电脑"),
            _ => AppLanguage.Get("Main.ClientOther")
        };

        public async Task CopyMonitorAddressAsync()
        {
            if (!TryGetMobileConnectionUrl(out string url))
            {
                ShowToast(GetMobileConnectionUnavailableMessage(), ToastSeverity.Warning);
                return;
            }

            bool copied = false;
            for (int i = 0; i < 3 && !copied; i++)
            {
                try
                {
                    Clipboard.SetDataObject(url, true);
                    copied = true;
                }
                catch
                {
                    await Task.Delay(80);
                }
            }

            bool opened = WorkstationNetwork.TryOpenUrl(url, out string openError);
            if (copied && opened)
                ShowToast("已复制并打开监控网页");
            else if (copied)
                ShowToast($"已复制地址，打开网页失败: {openError}", ToastSeverity.Error);
            else if (opened)
                ShowToast("已打开监控网页，复制失败请重试", ToastSeverity.Warning);
            else
                ShowToast($"复制和打开都失败: {openError}", ToastSeverity.Error);
        }

        private string BuildMonitorAccessUrl()
        {
            return MobileConnectionService.TryBuildUsableAccessUrl(
                MonitorAccessAddress,
                Config.RequireWebAccessKey,
                Config.WebAccessKey,
                out string url)
                ? url
                : "";
        }

        public async void ShowMobileConnection(System.Windows.Window owner = null)
        {
            if (_webServer == null
                && Capabilities.CanRunWebServer)
            {
                ShowToast("正在启动设备连接服务...", ToastSeverity.Information);
                await RestartWebServerAsync(allowAccessSetup: true);
            }

            string unavailableMessage = GetMobileConnectionUnavailableMessage();
            string url = "";
            if (string.IsNullOrEmpty(unavailableMessage))
                TryGetMobileConnectionUrl(out url);

            var dialogOwner = owner ?? Application.Current?.MainWindow;
            var dialog = new MobileConnectionWindow(
                url,
                Config.RequireWebAccessKey,
                unavailableMessage,
                canOpenSettings: owner is not SettingsWindow,
                repairLanAccessAsync: RepairLanAccessForMobileConnectionAsync)
            {
                Owner = dialogOwner
            };

            dialog.ShowDialog();

            if (dialog.OpenSettingsRequested && owner is not SettingsWindow)
                OpenSettings();
        }

        private async Task<MobileConnectionRepairResult> RepairLanAccessForMobileConnectionAsync()
        {
            Exception repairException = null;
            try
            {
                RuntimeLog.Info("Web", $"Repairing LAN access from connection dialog port={Config.WebServerPort}");
                await Task.Run(() => WebServer.RepairLanAccess(Config.WebServerPort));
                bool started = await RestartWebServerAsync(
                    allowAccessSetup: false,
                    showFailureToast: false);
                if (started && TryGetMobileConnectionUrl(out string url))
                {
                    ShowToast("局域网连接已修复");
                    return new MobileConnectionRepairResult(
                        true,
                        url,
                        Config.RequireWebAccessKey,
                        "");
                }
            }
            catch (Exception ex)
            {
                repairException = ex;
                RuntimeLog.Error("Web", "LAN access repair from connection dialog failed", ex);
            }

            string message = WebServer.GetLanAccessFailureUserMessage(
                repairAttempted: true,
                exception: repairException);
            ShowToast(message, ToastSeverity.Error);
            return new MobileConnectionRepairResult(
                false,
                "",
                Config.RequireWebAccessKey,
                message);
        }

        public void CopyMobileConnectionUrl()
        {
            if (!TryGetMobileConnectionUrl(out string url))
            {
                ShowToast(GetMobileConnectionUnavailableMessage(), ToastSeverity.Warning);
                return;
            }

            // 剪贴板被别的程序占着时不在界面线程上死等（那会整页卡住），转后台重试
            bool copiedNow = ClipboardHelper.TryCopyText(
                url,
                onCopied: () => ShowToastFromAnyThread(MobileConnectionService.ContainsAccessKey(url)
                    ? "连接网址已复制，包含访问密钥，请勿发送给无关人员"
                    : "连接网址已复制"),
                onFailed: error => ShowToastFromAnyThread($"复制网址失败: {error.Message}", ToastSeverity.Error));
            if (!copiedNow)
                ShowToast(AppLanguage.Get("剪贴板被占用，正在重试复制"), ToastSeverity.Information);
        }

        /// <summary>复制结果可能在后台线程回调，提示统一切回 UI 线程。</summary>
        private void ShowToastFromAnyThread(string message, ToastSeverity severity = ToastSeverity.Success)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                ShowToast(message, severity);
                return;
            }

            if (dispatcher.HasShutdownStarted)
                return;
            dispatcher.BeginInvoke(() => ShowToast(message, severity));
        }

        private void ShowMobileConnectionWindow(System.Windows.Window owner, string url)
        {
            var dialog = new MobileConnectionWindow(url, Config.RequireWebAccessKey) { Owner = owner };
            dialog.ShowDialog();
        }

        private bool TryGetMobileConnectionUrl(out string url)
        {
            url = "";
            return Capabilities.CanRunWebServer
                && _webServer != null
                && MobileConnectionService.TryBuildUsableAccessUrl(
                    MonitorAccessAddress,
                    Config.RequireWebAccessKey,
                    Config.WebAccessKey,
                    out url);
        }

        private string GetMobileConnectionUnavailableMessage()
        {
            if (!Capabilities.CanRunWebServer)
            {
                return "当前工位用途不提供网页回放";
            }
            if (_webServer == null)
                return "局域网服务暂时不可用，请检查端口、权限或防火墙设置";
            if (!MobileConnectionService.TryBuildUsableAccessUrl(
                    MonitorAccessAddress,
                    Config.RequireWebAccessKey,
                    Config.WebAccessKey,
                    out _))
            {
                return "尚未取得可供手机访问的局域网地址，请确认电脑已连接局域网";
            }

            return "";
        }

        public async void SwitchWorkstation() =>
            await SwitchWorkstationAsync(Application.Current?.MainWindow);

        internal async Task SwitchWorkstationAsync(System.Windows.Window owner)
        {
            if (_purposeSwitchPending)
            {
                CancelPurposeSwitch();
                ShowToast("正在取消用途切换", ToastSeverity.Information);
                return;
            }
            if (!CanSwitchWorkstation)
                return;

            string currentPreset = DeploymentPresets.Normalize(
                Config.DeploymentPreset);
            var selector = new WorkstationSelectionWindow(
                currentPreset,
                runtimeEnvironment:
                    RuntimeCapabilityEnvironment.FromConfig(Config))
            {
                Owner = owner
            };
            if (selector.ShowDialog() == true && !string.IsNullOrWhiteSpace(selector.SelectedPreset))
            {
                if (string.Equals(
                        currentPreset,
                        selector.SelectedPreset,
                        StringComparison.OrdinalIgnoreCase))
                {
                    ShowToast(
                        $"当前已经是{DeploymentPresets.GetDisplayName(currentPreset)}",
                        ToastSeverity.Information);
                    return;
                }

                AppConfig nextConfig =
                    JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(Config)) ?? new AppConfig();
                nextConfig.DeploymentPreset = selector.SelectedPreset;
                if (selector.SelectedPreset == DeploymentPresets.RecordingWorkstation)
                {
                    nextConfig.RecordingWorkstationActivatedAtUtc = DateTime.UtcNow;
                    RecordingWorkstationCachePolicy.ConfigureInitialLocation(
                        nextConfig,
                        preserveExistingLocation: true);
                }
                nextConfig.WorkstationRole = ProductCapabilityRuntime
                    .ForPreset(selector.SelectedPreset)
                    .Features.VideoRecording
                        ? WorkstationRoles.CameraMonitor
                        : selector.SelectedPreset == DeploymentPresets.MobileBackupHost
                            ? WorkstationRoles.PrintStation
                            : "";
                await RunPurposeSwitchAsync(nextConfig);
            }
        }

        internal async Task RequestHostStorageAsync(System.Windows.Window owner)
        {
            if (!CanSwitchWorkstation)
            {
                ShowToast("录像进行中，停止录像后才能切换用途", ToastSeverity.Information);
                return;
            }
            if (AppDialog.Confirm(
                    owner,
                    AppLanguage.Get("主机就是负责长期保存录像的电脑，支持 NAS／网络共享备份。要在这台电脑配置，请在用途选择第 2 问选“要”。是否现在打开用途选择？"),
                    AppLanguage.Get("切换用途"),
                    AppDialogSeverity.Information))
            {
                await SwitchWorkstationAsync(owner);
            }
        }

        private async Task<bool> RunPurposeSwitchAsync(AppConfig nextConfig)
        {
            if (_purposeSwitchPending || !_modeTransitionGate.TryEnter())
                return false;

            _purposeSwitchPending = true;
            using var transitionCts = CancellationTokenSource.CreateLinkedTokenSource(
                _purposeSwitchCts.Token);
            _activePurposeSwitchCts = transitionCts;
            CancellationToken transitionToken = transitionCts.Token;
            OnPropertyChanged(nameof(CanSwitchWorkstation));
            OnPropertyChanged(nameof(CanInvokePurposeSwitch));
            OnPropertyChanged(nameof(IsPurposeSwitchPending));
            SwitchWorkstationButtonText = AppLanguage.Get("正在切换");
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            AppConfig originalConfig = Config;
            string originalPreset = Config.DeploymentPreset;
            var blockersSeen = new List<string>();
            string outcome = "canceled";
            bool admissionPaused = false;
            bool lifecycleLockHeld = false;
            int restartCancellationHandled = 0;
            try
            {
                await _webServerLifecycleLock.WaitAsync(transitionToken);
                lifecycleLockHeld = true;
                _webServer?.PauseRequestAdmission();
                admissionPaused = _webServer != null;
                if (_webServer != null
                    && !await _webServer.WaitForAdmittedRequestsIdleAsync(
                        TimeSpan.FromSeconds(30),
                        transitionToken))
                {
                    throw new TimeoutException("等待在途 Web 请求结束超时");
                }
                DateTimeOffset externalWorkDeadline =
                    startedAt.AddMinutes(30);

                while (true)
                {
                    ModeTransitionActivity activity =
                        await CaptureModeTransitionActivityAsync(
                            transitionToken);
                    IReadOnlyList<string> blockers =
                        ModeTransitionPolicy.FindBlockers(activity);
                    foreach (string blocker in blockers)
                    {
                        if (!blockersSeen.Contains(blocker, StringComparer.Ordinal))
                            blockersSeen.Add(blocker);
                    }
                    if (blockers.Count == 0)
                        break;
                    bool recordingHandoff = blockers.Contains(
                            "recording",
                            StringComparer.Ordinal)
                        || blockers.Contains(
                            "recording-start",
                            StringComparer.Ordinal)
                        || blockers.Contains(
                            "recording-stop",
                            StringComparer.Ordinal);
                    if (!recordingHandoff
                        && DateTimeOffset.UtcNow >= externalWorkDeadline)
                    {
                        throw new TimeoutException(
                            "等待备份、上传、剪辑或文件收尾超时");
                    }

                    SwitchWorkstationButtonText =
                        AppLanguage.Get(
                            ModeTransitionPolicy.StatusResourceKey(blockers));
                    if (activity.ActiveMobileBackups && _webServer != null)
                    {
                        await _webServer.WaitForMobileBackupsAsync(
                        transitionToken);
                    }
                    else
                    {
                        await Task.Delay(250, transitionToken);
                    }
                }

                transitionToken.ThrowIfCancellationRequested();
                IReadOnlyList<string> finalBlockers = ModeTransitionPolicy.FindBlockers(
                    await CaptureModeTransitionActivityAsync(
                        transitionToken));
                if (finalBlockers.Count > 0)
                {
                    throw new InvalidOperationException(
                        "切换用途前仍有未完成任务：" + string.Join("、", finalBlockers));
                }
                if (!SaveConfig(nextConfig, notifyUser: true))
                {
                    outcome = "save-failed";
                    ResumeModeTransitionAdmission(admissionPaused);
                    return false;
                }

                Config = nextConfig;
                outcome = "restart-requested";
                WorkstationNetwork.SetRestartCancellationCallback(() =>
                {
                    if (Interlocked.Exchange(ref restartCancellationHandled, 1) != 0)
                        return;
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Config = originalConfig;
                        SaveConfig(originalConfig, notifyUser: false);
                        ResumeModeTransitionAdmission(admissionPaused);
                        _purposeSwitchPending = false;
                        SwitchWorkstationButtonText = AppLanguage.Get("切换用途");
                        OnPropertyChanged(nameof(CanSwitchWorkstation));
                        _modeTransitionGate.Exit();
                        ShowToast(
                            AppLanguage.Get("重启已取消，用途未切换"),
                            ToastSeverity.Warning);
                    }));
                });
                bool restarted = ApplicationRestart.RestartAfterPurposeChange(
                    Application.Current?.MainWindow);
                if (!restarted)
                {
                    outcome = "restart-failed";
                    WorkstationNetwork.SetRestartCancellationCallback(null);
                    Config = originalConfig;
                    SaveConfig(originalConfig, notifyUser: false);
                    ResumeModeTransitionAdmission(admissionPaused);
                }
                return restarted;
            }
            catch (OperationCanceledException)
            {
                outcome = "canceled";
                ResumeModeTransitionAdmission(admissionPaused);
                return false;
            }
            catch (Exception ex)
            {
                outcome = "failed";
                ResumeModeTransitionAdmission(admissionPaused);
                RuntimeLog.Error("ModeTransition", "Purpose transition failed", ex);
                ShowToast(
                    AppLanguage.Format(
                        "切换用途失败：{0}",
                        AppLanguage.Translate(ex.Message)),
                    ToastSeverity.Error);
                return false;
            }
            finally
            {
                RuntimeLog.Info(
                    "ModeTransition",
                    $"from={originalPreset} to={nextConfig.DeploymentPreset} outcome={outcome} blockers={string.Join(",", blockersSeen)} durationMs={DateTimeOffset.UtcNow.Subtract(startedAt).TotalMilliseconds:F0}");
                if (!WorkstationNetwork.IsRestartPending)
                {
                    WorkstationNetwork.SetRestartCancellationCallback(null);
                    _purposeSwitchPending = false;
                    SwitchWorkstationButtonText = AppLanguage.Get("切换用途");
                    OnPropertyChanged(nameof(CanSwitchWorkstation));
                    OnPropertyChanged(nameof(CanInvokePurposeSwitch));
                    OnPropertyChanged(nameof(IsPurposeSwitchPending));
                    _modeTransitionGate.Exit();
                }
                if (lifecycleLockHeld)
                    _webServerLifecycleLock.Release();
                if (ReferenceEquals(_activePurposeSwitchCts, transitionCts))
                    _activePurposeSwitchCts = null;
            }
        }

        internal void CancelPurposeSwitch()
        {
            try { _activePurposeSwitchCts?.Cancel(); } catch { }
        }

        private async Task<ModeTransitionActivity> CaptureModeTransitionActivityAsync(
            CancellationToken cancellationToken)
        {
            int pendingUploads = Math.Max(0, ActiveRecordingTransferCount);
            if (_recordingTransferService != null)
            {
                pendingUploads = await Task.Run(
                    () => _recordingTransferService.GetActiveWorkCount(),
                    cancellationToken);
            }
            return new ModeTransitionActivity(
                Recording: IsRecording,
                ActiveMobileBackups: _webServer?.HasActiveMobileBackups == true,
                PendingRecordingUploads: pendingUploads,
                RecordingUploadPaused:
                    IsRecordingWorkstation && IsRecordingUploadPaused,
                ActiveClipTasks: _webServer?.HasActiveClipTasks == true,
                ActiveMediaFinalization:
                    IsFinalizationTaskActive(_lastFinalizeTask)
                    || IsFinalizationTaskActive(_postStopMuxTask)
                    || IsFinalizationTaskActive(_mkvRecoveryTask),
                PhotoCaptureInProgress: PhotoCaptureInProgress,
                PhotoArchiveInProgress:
                    Capabilities.CanOperatePhoto &&
                    await PhotoCaptureService.HasOutstandingArchiveWorkAsync(
                        cancellationToken),
                RecordingStartInProgress:
                    Volatile.Read(ref _recordingStartInProgress) != 0,
                RecordingStopInProgress:
                    Volatile.Read(ref _recordingStopInProgress) != 0);
        }

        private static bool IsFinalizationTaskActive(Task task) =>
            task is not null && (!task.IsCompleted || task.IsFaulted);

        private void ResumeModeTransitionAdmission(bool admissionPaused)
        {
            if (!admissionPaused)
                return;
            try { _webServer?.ResumeRequestAdmission(); } catch { }
        }

        /// <summary>收到油猴脚本推送的订单信息时，提前生成 TTS 缓存</summary>
        private void OnOrderInfoReceived(List<OrderInfo> orders)
        {
            if (orders == null) return;

            bool hasTestOrder = orders.Any(x => x.IsTest);
            string printStatusText = hasTestOrder
                ? AppLanguage.Format("Main.PrintTestOrder", DateTime.Now.ToString("HH:mm"))
                : orders.Count == 0
                    ? AppLanguage.Format("Main.PrintNoRefund", DateTime.Now.ToString("HH:mm"))
                    : AppLanguage.Format("Main.PrintOrders", DateTime.Now.ToString("HH:mm"), orders.Count);
            Application application = Application.Current;
            if (application != null)
            {
                _ = application.Dispatcher.InvokeAsync(() =>
                {
                    if (_isDisposed) return;
                    if (_webServer != null)
                    {
                        OrderIntegrationStatusText = printStatusText;
                    }

                    string activeOrderId = IsRecording ? _recordingOrderId : CurrentOrderId;
                    OrderInfo activeOrder = orders.FirstOrDefault(info =>
                        !info.IsTest
                        && string.Equals(info.TrackingNumber?.Trim(), JdBarcodePolicy.Waybill(activeOrderId), StringComparison.OrdinalIgnoreCase));
                    if (IsRecording && activeOrder != null)
                    {
                        SetPreviewOrderNotice(activeOrder);
                        // 退款可能是在开始录制之后才发生的：订单信息一到就判一次，去重按"录像会话 + 运单号"。
                        MaybeAlertPrintedRefundForActiveRecording(activeOrder);
                        if (Config.EnableOrderInfoAnnounce)
                        {
                            foreach (AlertSpeechFollowup announcement in BuildOrderInfoSpeechFollowups(
                                         activeOrder,
                                         Config.EnableOrderInfoAnnounce,
                                         Config.AnnounceBuyerMessage,
                                         Config.AnnounceSellerMemo,
                                         Config.AnnounceProductInfo,
                                         Config.AnnounceTotalItemCount))
                            {
                                PublishVoice(
                                    announcement.Text,
                                    announcement.VoiceStyle,
                                    announcement.Sound,
                                    repeatCount: 1,
                                    interruptCurrent: false);
                            }
                        }
                    }

                    if (hasTestOrder)
                    {
                        ShowToast("已收到测试订单");
                        SpeakWithRemarkTone(DefaultSpeechCatalog.TestOrderReceived, cancelPrevious: false);
                    }
                });
            }

            if (orders.Count == 0) return;

            var realOrders = orders.Where(x => !x.IsTest).ToList();
            if (realOrders.Count == 0)
                return;

            if (_alertService == null) return;
            if (Config.EnablePrintedRefundAlert)
            {
                foreach (string statusText in realOrders
                    .Where(info => info.IsPrintedRefund)
                    .Select(GetRefundStatusDisplayText)
                    .Distinct())
                {
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreatePrintedRefundAnnouncement(statusText), AlertVoiceStyle.Warning);
                }
            }
            if (!Config.EnableOrderInfoAnnounce) return;
            foreach (var info in realOrders)
            {
                if (Config.AnnounceTotalItemCount
                    && !info.HasRefund
                    && !info.IsPrintedRefund
                    && info.TotalItemCount > 1)
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreateOrderTotalCountAnnouncement(info.TotalItemCount));
                if (Config.AnnounceBuyerMessage && !string.IsNullOrWhiteSpace(info.BuyerMessage))
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreateBuyerMessageAnnouncement(info.BuyerMessage));
                if (Config.AnnounceSellerMemo && !string.IsNullOrWhiteSpace(info.SellerMemo))
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreateSellerMemoAnnouncement(info.SellerMemo));
                if (Config.AnnounceProductInfo && !string.IsNullOrWhiteSpace(info.ProductInfo))
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreateProductAnnouncement(info.ProductInfo));
            }
        }

        /// <summary>
        /// 扩展扫码任务查到的订单：先落库、回填录像记录，再走和推送一样的界面提示与播报。
        /// 以前这里只调 <see cref="OnOrderInfoReceived"/>，插件明明查到了商品，
        /// 录像库里却始终没有订单信息。
        /// </summary>
        private void OnExtensionOrderResolved(OrderInfo order)
        {
            if (order == null) return;
            try
            {
                _webServer?.StoreResolvedOrderInfos([order]);
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("ExtensionResult", "Persisting resolved order failed", ex);
            }
            OnOrderInfoReceived([order]);
        }

        /// <summary>
        /// 后台把"扩展市场里登记过"的已安装扩展更新到最新版：一天最多一次，失败静默并写日志。
        /// 目录和安装包沿用市场那套签名与 SHA-256 校验；市场里查不到的扩展不动。
        /// </summary>
        private void QueueExtensionAutoUpdate(bool force = false)
        {
            if (!Config.AutoUpdateExtensions) return;
            if (!force && !ExtensionAutoUpdateService.ShouldCheckNow(Config.LastExtensionAutoUpdateCheck, DateTime.Now)) return;

            // 先把检查时间落盘：离线时也按"今天查过了"处理，避免每次启动都去请求市场。
            RecordExtensionAutoUpdateCheck();
            _ = Task.Run(async () =>
            {
                try
                {
                    var service = new ExtensionAutoUpdateService(
                        new ExtensionMarketClient(),
                        new ExtensionInstallationService(),
                        AppVersion.Current);
                    ExtensionAutoUpdateResult result = await service.RunAsync(
                        _cts?.Token ?? CancellationToken.None);
                    if (result.Skipped) return;

                    if (result.Updated.Count > 0)
                        RuntimeLog.Info("ExtensionUpdate", $"自动更新扩展：{string.Join("；", result.Updated)}");
                    if (result.SkippedItems.Count > 0 || result.Failures.Count > 0)
                        RuntimeLog.Warn(
                            "ExtensionUpdate",
                            $"自动更新扩展跳过=[{string.Join("；", result.SkippedItems)}] 失败=[{string.Join("；", result.Failures)}]");
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("ExtensionUpdate", $"自动更新扩展失败：{ex.Message}");
                }
            });
        }

        /// <summary>
        /// 默认脚本清单在启动后自动补齐；已安装时直接返回，不访问扩展市场。
        /// 失败只写日志，安装向导入口还会再试一次，不阻塞主程序启动。
        /// </summary>
        private void QueueDefaultScriptExtensionsBootstrap()
        {
            CancellationToken appToken = _cts?.Token ?? CancellationToken.None;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(appToken);
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    DefaultScriptExtensionBootstrapResult result =
                        await DefaultScriptExtensionBootstrapService.Shared.EnsureInstalledAsync(timeout.Token);
                    if (result.Installed.Count > 0)
                    {
                        RuntimeLog.Info(
                            "Extensions",
                            $"默认脚本已自动安装：{string.Join("；", result.Installed)}");
                    }
                    if (result.Failures.Count > 0)
                    {
                        RuntimeLog.Warn(
                            "Extensions",
                            $"默认脚本自动安装失败：{string.Join("；", result.Failures)}");
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("Extensions", $"默认脚本自动安装失败：{ex.Message}");
                }
            });
        }

        private void RecordExtensionAutoUpdateCheck()
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (WorkstationConfigStore.TryUpdate(
                    saved => saved.LastExtensionAutoUpdateCheck = stamp,
                    out AppConfig savedConfig,
                    out string error))
            {
                Config.LastExtensionAutoUpdateCheck = savedConfig.LastExtensionAutoUpdateCheck;
                return;
            }

            RuntimeLog.Warn("ExtensionUpdate", $"记录扩展自动更新检查时间失败：{error}");
        }

        public void OpenUserscriptGuide()
        {
            System.Windows.Window owner = Application.Current?.Windows
                .OfType<System.Windows.Window>()
                .FirstOrDefault(window => window.IsActive);
            if (Interlocked.Exchange(ref _userscriptGuidePreparing, 1) != 0)
                return;
            _ = OpenUserscriptGuideAsync(owner);
        }

        private async Task OpenUserscriptGuideAsync(System.Windows.Window owner)
        {
            try
            {
                ShowToast(AppLanguage.Get("正在下载扩展包"), ToastSeverity.Information);
                GuideScriptPreparation preparation = await Task.Run(async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    DefaultScriptExtensionBootstrapResult bootstrap =
                        await DefaultScriptExtensionBootstrapService.Shared.EnsureInstalledAsync(timeout.Token);
                    IReadOnlyList<InstalledExtensionRecord> installed =
                        new ExtensionInstallationService().GetInstalled();
                    bool officialInstalled = installed.Any(record =>
                        string.Equals(
                            record.Id,
                            ExtensionMarketOfficialUserscriptPackageSource.ExtensionId,
                            StringComparison.Ordinal));
                    bool hasUserscript = new UserscriptCatalog().GetAll().Count > 0;
                    return new GuideScriptPreparation(bootstrap, officialInstalled, hasUserscript);
                });

                if (preparation.Bootstrap.Failures.Count > 0)
                {
                    RuntimeLog.Warn(
                        "Extensions",
                        $"安装向导准备默认脚本失败：{string.Join("；", preparation.Bootstrap.Failures)}");
                }
                if (!preparation.HasUserscript)
                {
                    AppDialog.Error(
                        owner,
                        AppLanguage.Format(
                            "安装扩展失败：{0}",
                            AppLanguage.Get("扩展市场不可用")),
                        AppLanguage.Get("安装扩展失败"));
                    return;
                }

                IReadOnlyList<ConnectedClientInfo> connectedClients = _webServer?.GetConnectedClients() ?? [];
                bool earlyOfficialScript = connectedClients.Any(OfficialUserscriptMigrationService.IsEarlyOfficialHeartbeat);
                bool duplicateOfficialScripts = OfficialUserscriptMigrationService.HasConcurrentLegacyAndCurrentScripts(connectedClients);
                if (duplicateOfficialScripts)
                {
                    AppDialog.Warning(
                        owner,
                        "检测到同一电脑正在同时运行新版和旧版订单联动脚本。Desktop 已自动忽略短时间内的重复订单，请在油猴中删除“快递助手 → 打包监控联动”或“订单备注播报插件”",
                        "检测到重复脚本");
                }
                else if (earlyOfficialScript && !preparation.OfficialInstalled)
                {
                    AppDialog.Warning(
                        owner,
                        "检测到正在运行的早期订单联动脚本。安装新版扩展后，请在油猴中删除“快递助手 → 打包监控联动”或“订单备注播报插件”，避免重复推送订单",
                        "需要迁移旧脚本");
                }

                if (_webServer == null || string.IsNullOrWhiteSpace(MonitorAccessAddress))
                {
                    ShowToast("局域网服务尚未就绪，暂时无法生成快递助手脚本", ToastSeverity.Warning);
                    return;
                }

                if (!UserscriptGuideNavigation.TryOpen($"http://{MonitorAccessAddress}", out string navigationError))
                {
                    ShowToast($"打开快递助手联动安装向导失败：{navigationError}", ToastSeverity.Error);
                    return;
                }

                UserscriptTargetState.MarkGuideOpened(
                    Config,
                    _webServer.GetRecordingDevices(MonitorAccessAddress, includeKnown: true));
                RefreshUserscriptStatus();
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("Extensions", $"安装向导自动准备脚本失败：{ex.Message}");
                AppDialog.Error(
                    owner,
                    AppLanguage.Format(
                        "安装扩展失败：{0}",
                        AppLanguage.TranslateForDisplay(
                            ex.Message,
                            "扩展安装失败，请查看日志了解详情")),
                    AppLanguage.Get("安装扩展失败"));
            }
            finally
            {
                Interlocked.Exchange(ref _userscriptGuidePreparing, 0);
            }
        }

        private sealed record GuideScriptPreparation(
            DefaultScriptExtensionBootstrapResult Bootstrap,
            bool OfficialInstalled,
            bool HasUserscript);

        public void OpenExtensionMarket(System.Windows.Window owner)
        {
            var window = new ExtensionMarketWindow { Owner = owner };
            window.ShowDialog();
        }

        /// <summary>
        /// 把单号发给已授权的扩展做按需查询（订单、退款）。
        /// 开始录制时发一次，同码停录时再发一次，兜住"打包过程中才退款"的情况。
        /// </summary>
        private void PublishExtensionScanTask(string trackingNumber)
        {
            ExtensionRuntime runtime = _extensionRuntime;
            if (runtime == null
                || !IsRecording
                || _currentRecordId <= 0
                || string.IsNullOrWhiteSpace(_recordingSessionId))
                return;
            try
            {
                ExtensionScanPublishResult result = runtime.Publish(
                    Config.NodeId,
                    _recordingSessionId,
                    trackingNumber,
                    _recordingMode);
                if (result.Deliveries.Count > 0 || result.SkippedTargets.Count > 0)
                {
                    RuntimeLog.Info(
                        "ExtensionTask",
                        $"Published scan task session={_recordingSessionId}, deliveries={result.Deliveries.Count}, skipped={result.SkippedTargets.Count}");
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("ExtensionTask", "Failed to publish scan task", ex);
            }
        }

        internal IReadOnlyList<ExtensionAuthorizationDisplayItem> GetExtensionAuthorizations()
        {
            if (_extensionAuthorizationStore == null)
            {
                string extensionDirectory = Path.Combine(AppPaths.MobileBackupStateDir, "extensions");
                if (!Directory.Exists(extensionDirectory))
                    return [];
                _extensionAuthorizationStore = new ExtensionAuthorizationStore(AppPaths.MobileBackupStateDir);
            }
            return _extensionAuthorizationStore.GetAll(includeRevoked: false)
                .Select(ExtensionAuthorizationDisplayFactory.Create)
                .ToArray();
        }

        internal IReadOnlyList<OrderIntegrationDeviceDisplayItem> GetOrderIntegrationDevices() =>
            (_webServer?.GetOrderIntegrationDeviceStatuses() ?? [])
                .Select(device => new OrderIntegrationDeviceDisplayItem(
                    device.NodeId,
                    device.DisplayName,
                    FormatOrderIntegrationDeviceType(device.DeviceType),
                    device.Online,
                    FormatOrderIntegrationDeviceActivity(device.LastActivityUtc, device.ReceivedCount)))
                .ToArray();

        internal static string FormatOrderIntegrationDeviceType(string deviceType) =>
            string.Equals(deviceType, "mobile", StringComparison.OrdinalIgnoreCase)
                ? AppLanguage.Get("手机录像设备")
                : AppLanguage.Get("电脑录像设备");

        internal static string FormatOrderIntegrationDeviceActivity(
            DateTimeOffset? lastActivityUtc,
            int processedCount)
        {
            return lastActivityUtc.HasValue
                ? AppLanguage.Format(
                    "{0} 已处理 {1} 条联动数据",
                    lastActivityUtc.Value.ToLocalTime().ToString("HH:mm"),
                    processedCount)
                : AppLanguage.Get("暂无联动数据");
        }

        internal bool RevokeExtensionAuthorization(string extensionInstanceId) =>
            _extensionAuthorizationStore?.Revoke(extensionInstanceId) == true;

    }
}
