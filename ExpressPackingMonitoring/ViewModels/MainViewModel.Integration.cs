#nullable disable
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Input;
using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Localization;
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
using System.Drawing;
using System.Drawing.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        private async Task<bool> RestartWebServerAsync(
            bool allowAccessSetup,
            bool showFailureToast = true)
        {
            await _webServerLifecycleLock.WaitAsync();
            WebServer newServer = null;
            ExtensionRuntime newExtensionRuntime = null;
            try
            {
                bool orderReceiverOnly = IsRecordingWorkstation;
                Interlocked.Increment(ref _workstationAddressRefreshVersion);
                WebServer previousServer = _webServer;
                _webServer = null;
                try { previousServer?.Dispose(); } catch { }
                ExtensionRuntime previousExtensionRuntime = _extensionRuntime;
                _extensionRuntime = null;
                try { previousExtensionRuntime?.Dispose(); } catch { }

                if ((!Config.EnableWebServer && !orderReceiverOnly) || _db == null || _isDisposed)
                {
                    MonitorAccessAddress = "";
                    WorkstationPrintStatusText = "未连接";
                    WorkstationStatusToolTip = "开启局域网查看后，可点击手机/电脑连接查看二维码或复制网址";
                    SetConnectedDeviceUnavailable(AppLanguage.Get("Main.ConnectionServiceDisabled"), AppLanguage.Get("Main.ConnectionEmptyTip"));
                    return true;
                }

                WorkstationPrintStatusText = orderReceiverOnly
                    ? "订单联动接收：等待启动"
                    : "启动中";
                SetConnectedDeviceUnavailable(AppLanguage.Get("Main.ConnectionServiceStarting"), AppLanguage.Get("Main.ConnectionEmptyTip"));
                int port = Config.WebServerPort;
                int cacheMaxMb = Config.TranscodeCacheMaxMB;
                bool enableOrderInfoLog = Config.EnableOrderInfoLog;
                bool requireAccessKey = !orderReceiverOnly && Config.RequireWebAccessKey;
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
                        mobileBackupRecordingRootResolver: ResolveBestStoragePath,
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
                        extensionApiEnabled: Config.EnableExtensionApi)
                    {
                        EnableOrderInfoLog = enableOrderInfoLog
                    };
                    if (Config.EnableExtensionApi)
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
                            order => OnOrderInfoReceived([order]));
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
                    $"LAN service started port={port}, cacheMaxMB={cacheMaxMb}, orderReceiverOnly={orderReceiverOnly}, extensionApiEnabled={Config.EnableExtensionApi}");
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
            Application application = Application.Current;
            if (application == null || application.Dispatcher.CheckAccess())
            {
                RefreshMobileBackupStatuses();
                return;
            }
            _ = application.Dispatcher.InvokeAsync(RefreshMobileBackupStatuses);
        }

        private void RefreshMobileBackupStatuses()
        {
            if (_isDisposed)
                return;

            _mobileBackupStatusDate = DateTime.Today;
            IReadOnlyList<MobileBackupDailyCount> counts =
                _db?.GetMobileBackupDailyCounts(_mobileBackupStatusDate) ?? [];
            var statusByDevice = counts
                .Where(item => BackupDeviceIdentity.IsRemote(item.DeviceId, Config.NodeId))
                .ToDictionary(
                item => item.DeviceId,
                item => new
                {
                    Name = string.IsNullOrWhiteSpace(item.DeviceName)
                        ? GetFallbackDeviceName(item.DeviceId, item.DeviceKind)
                        : item.DeviceName,
                    Kind = item.DeviceKind,
                    Count = item.VideoCount,
                    Online = false
                },
                StringComparer.OrdinalIgnoreCase);

            foreach (ConnectedClientInfo client in _connectedClientSnapshot
                .Where(client => ShouldIncludeBackupDeviceClient(client, Config.NodeId)))
            {
                string deviceId = string.IsNullOrWhiteSpace(client.NodeId)
                    ? client.ClientId
                    : client.NodeId;
                if (!BackupDeviceIdentity.IsRemote(deviceId, Config.NodeId))
                    continue;

                statusByDevice.TryGetValue(deviceId, out var existing);
                bool isComputer = string.Equals(
                    client.ClientType,
                    "recording-workstation",
                    StringComparison.OrdinalIgnoreCase);
                statusByDevice[deviceId] = new
                {
                    Name = string.IsNullOrWhiteSpace(client.DisplayName)
                        ? existing?.Name ?? (isComputer ? "电脑设备" : "手机设备")
                        : client.DisplayName,
                    Kind = isComputer ? "pc" : "mobile",
                    Count = existing?.Count ?? 0,
                    Online = true
                };
            }

            MobileBackupDeviceStatuses.Clear();
            foreach (var item in statusByDevice
                .OrderBy(pair => pair.Value.Name, StringComparer.CurrentCulture)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal))
            {
                MobileBackupDeviceStatuses.Add(new MobileBackupDeviceStatus
                {
                    DeviceId = item.Key,
                    DisplayText = $"{item.Value.Name} · 今日备份 {item.Value.Count} 个",
                    IsOnline = item.Value.Online
                });
            }

            if (MobileBackupDeviceStatuses.Count == 0)
            {
                MobileBackupDeviceStatuses.Add(new MobileBackupDeviceStatus
                {
                    DisplayText = "暂无手机/电脑设备",
                    IsOnline = false
                });
            }
            RefreshUserscriptStatus();
        }

        private void RefreshUserscriptStatus()
        {
            _lastUserscriptStatusRefreshAt = DateTime.Now;
            IReadOnlyList<RecordingDeviceInfo> devices = _webServer?.GetRecordingDevices(
                MonitorAccessAddress,
                includeKnown: true) ?? [];
            UserscriptTargetStatus status = UserscriptTargetState.GetStatus(Config, devices);
            (string shortStatus, string detailText) =
                UserscriptStatusCardModel.GetCardTexts(status);
            UserscriptSetupStatusText = AppLanguage.Get(detailText);
            UserscriptSetupShortStatusText = AppLanguage.Get(shortStatus);
            UserscriptButtonText = AppLanguage.Get(status.ButtonText);
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

        public void CopyMonitorAddress()
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
                    Thread.Sleep(80);
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
            if (ShouldEnableWebServerForMobileConnection(Config))
            {
                Config.EnableWebServer = true;
                if (!SaveConfig(notifyUser: false))
                {
                    Config.EnableWebServer = false;
                    ShowToast("设备连接服务启用失败，请检查配置文件权限", ToastSeverity.Error);
                }
                else
                {
                    ShowToast("正在启动设备连接服务...", ToastSeverity.Information);
                    await RestartWebServerAsync(allowAccessSetup: true);
                }
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

            MainWindow mainWindow = Application.Current?.MainWindow as MainWindow;
            mainWindow?.SuspendCapsLockForModalWindow();
            try
            {
                dialog.ShowDialog();
            }
            finally
            {
                mainWindow?.ResumeCapsLockAfterModalWindow();
            }

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

        internal static bool ShouldEnableWebServerForMobileConnection(AppConfig config) =>
            config != null
            && !config.EnableWebServer
            && string.Equals(
                config.DeploymentPreset,
                DeploymentPresets.RecordingHost,
                StringComparison.OrdinalIgnoreCase);

        public void CopyMobileConnectionUrl()
        {
            if (!TryGetMobileConnectionUrl(out string url))
            {
                ShowToast(GetMobileConnectionUnavailableMessage(), ToastSeverity.Warning);
                return;
            }

            if (!ClipboardHelper.TrySetDataObject(url, out Exception error))
            {
                ShowToast($"复制网址失败: {error.Message}", ToastSeverity.Error);
                return;
            }

            ShowToast(MobileConnectionService.ContainsAccessKey(url)
                ? "连接网址已复制，包含访问密钥，请勿发送给无关人员"
                : "连接网址已复制");
        }

        private void ShowMobileConnectionWindow(System.Windows.Window owner, string url)
        {
            var dialog = new MobileConnectionWindow(url, Config.RequireWebAccessKey) { Owner = owner };
            MainWindow mainWindow = Application.Current?.MainWindow as MainWindow;
            mainWindow?.SuspendCapsLockForModalWindow();
            try
            {
                dialog.ShowDialog();
            }
            finally
            {
                mainWindow?.ResumeCapsLockAfterModalWindow();
            }
        }

        private bool TryGetMobileConnectionUrl(out string url)
        {
            url = "";
            return Config.EnableWebServer
                && _webServer != null
                && MobileConnectionService.TryBuildUsableAccessUrl(
                    MonitorAccessAddress,
                    Config.RequireWebAccessKey,
                    Config.WebAccessKey,
                    out url);
        }

        private string GetMobileConnectionUnavailableMessage()
        {
            if (!Config.EnableWebServer)
                return "局域网查看尚未开启，请先在设置中启用";
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

        public async void SwitchWorkstation()
        {
            if (!CanSwitchWorkstation)
                return;

            var selector = new WorkstationSelectionWindow(Config.DeploymentPreset)
            {
                Owner = Application.Current?.MainWindow
            };
            if (selector.ShowDialog() == true && !string.IsNullOrWhiteSpace(selector.SelectedPreset))
            {
                if (string.Equals(
                        Config.DeploymentPreset,
                        selector.SelectedPreset,
                        StringComparison.OrdinalIgnoreCase))
                {
                    ShowToast($"当前已经是{DeploymentPresets.GetDisplayName(Config.DeploymentPreset)}", ToastSeverity.Information);
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
                nextConfig.WorkstationRole = DeploymentCapabilities
                    .ForPreset(selector.SelectedPreset)
                    .IsRecordingDevice
                        ? WorkstationRoles.CameraMonitor
                        : selector.SelectedPreset == DeploymentPresets.MobileBackupHost
                            ? WorkstationRoles.PrintStation
                            : "";
                nextConfig.EnableWebServer = DeploymentCapabilities
                    .ForPreset(selector.SelectedPreset)
                    .CanRunWebServer;
                await RunPurposeSwitchAsync(nextConfig);
            }
        }

        private async Task<bool> RunPurposeSwitchAsync(AppConfig nextConfig)
        {
            if (_purposeSwitchPending || IsRecording)
                return false;

            _purposeSwitchPending = true;
            OnPropertyChanged(nameof(CanSwitchWorkstation));
            SwitchWorkstationButtonText = "正在切换";
            try
            {
                if (_webServer?.HasActiveMobileBackups == true)
                {
                    SwitchWorkstationButtonText = "等待备份完成";
                    ShowToast("设备录像正在备份，完成后将自动重启", ToastSeverity.Warning);
                    await _webServer.WaitForMobileBackupsAsync(_purposeSwitchCts.Token);
                }

                while (IsRecording)
                {
                    SwitchWorkstationButtonText = "等待录像完成";
                    await Task.Delay(250, _purposeSwitchCts.Token);
                }

                _purposeSwitchCts.Token.ThrowIfCancellationRequested();
                if (!SaveConfig(nextConfig, notifyUser: true))
                    return false;

                Config = nextConfig;
                return WorkstationNetwork.RestartAfterPurposeChange(Application.Current?.MainWindow);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            finally
            {
                if (!WorkstationNetwork.IsRestartPending)
                {
                    _purposeSwitchPending = false;
                    SwitchWorkstationButtonText = "切换用途";
                    OnPropertyChanged(nameof(CanSwitchWorkstation));
                }
            }
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
                        && string.Equals(info.TrackingNumber?.Trim(), activeOrderId?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (IsRecording && activeOrder != null)
                        SetPreviewOrderNotice(activeOrder);

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
                    && info.TotalItemCount > 0)
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreateOrderTotalCountAnnouncement(info.TotalItemCount));
                if (Config.AnnounceBuyerMessage && !string.IsNullOrWhiteSpace(info.BuyerMessage))
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreateBuyerMessageAnnouncement(info.BuyerMessage));
                if (Config.AnnounceSellerMemo && !string.IsNullOrWhiteSpace(info.SellerMemo))
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreateSellerMemoAnnouncement(info.SellerMemo));
                if (Config.AnnounceProductInfo && !string.IsNullOrWhiteSpace(info.ProductInfo))
                    _alertService.PreGenerate(DefaultSpeechCatalog.CreateProductAnnouncement(info.ProductInfo));
            }
        }

        public void OpenUserscriptGuide()
        {
            if (_webServer == null || string.IsNullOrWhiteSpace(MonitorAccessAddress))
            {
                ShowToast("局域网服务尚未就绪，暂时无法生成快递助手脚本", ToastSeverity.Warning);
                return;
            }

            if (!UserscriptGuideNavigation.TryOpen($"http://{MonitorAccessAddress}", out string error))
            {
                ShowToast($"打开快递助手联动安装向导失败：{error}", ToastSeverity.Error);
                return;
            }

            UserscriptTargetState.MarkGuideOpened(
                Config,
                _webServer.GetRecordingDevices(MonitorAccessAddress, includeKnown: true));
            RefreshUserscriptStatus();
        }

        public void ImportUserscript()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "油猴脚本 (*.user.js)|*.user.js|所有文件 (*.*)|*.*",
                Multiselect = false,
                CheckFileExists = true,
                Title = "导入自定义油猴脚本"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                var catalog = new UserscriptCatalog();
                UserscriptDescriptor descriptor = catalog.Import(dialog.FileName);
                string warning = descriptor.Warnings.Count == 0
                    ? ""
                    : $"\n\n检查提示：\n· {string.Join("\n· ", descriptor.Warnings)}";
                if (descriptor.Warnings.Count > 0 && !AppDialog.Confirm(
                        null,
                        $"脚本“{descriptor.Name}”已读取，但存在维护或安全提示：{warning}\n\n是否仍然导入？",
                        "导入自定义脚本",
                        AppDialogSeverity.Warning,
                        "确认导入"))
                {
                    catalog.Remove(descriptor.Id);
                    return;
                }
                ShowToast("自定义脚本已导入，可在安装订单联动中选择", ToastSeverity.Success);
            }
            catch (Exception ex)
            {
                AppDialog.Error(null, $"导入自定义脚本失败：{ex.Message}", "导入失败");
            }
        }

        private void PublishExtensionScanTaskIfRecordingStarted(string trackingNumber)
        {
            ExtensionRuntime runtime = _extensionRuntime;
            if (!Config.EnableExtensionApi
                || runtime == null
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
                .Select(value => new ExtensionAuthorizationDisplayItem(
                    value.ExtensionInstanceId,
                    value.DisplayName,
                    string.IsNullOrWhiteSpace(value.RuntimeVersion) ? value.Version : value.RuntimeVersion,
                    value.Source,
                    string.Join("、", value.Permissions),
                    value.RoutingScope == ExtensionRoutingScope.AllLocalRecordingNodes
                        ? "所有本机录像工位"
                        : string.Join("、", value.BoundOriginNodeIds),
                    value.CredentialGeneration,
                    value.UpdatedAtUtc,
                    value.LastSeenUtc.HasValue
                        && DateTimeOffset.UtcNow - value.LastSeenUtc.Value <= TimeSpan.FromSeconds(45),
                    value.LastBusinessActivityUtc.HasValue
                        ? $"{value.LastBusinessActivityUtc.Value.ToLocalTime():HH:mm} 收到 {value.LastBusinessDataCount} 条数据"
                        : "暂未收到数据"))
                .ToArray();
        }

        internal IReadOnlyList<OrderIntegrationDeviceDisplayItem> GetOrderIntegrationDevices() =>
            (_webServer?.GetOrderIntegrationDeviceStatuses() ?? [])
                .Select(device => new OrderIntegrationDeviceDisplayItem(
                    device.NodeId,
                    device.DisplayName,
                    string.Equals(device.DeviceType, "mobile", StringComparison.OrdinalIgnoreCase)
                        ? "手机录像设备"
                        : "电脑录像设备",
                    device.Online,
                    FormatOrderIntegrationDeviceActivity(device.LastActivityUtc, device.ReceivedCount)))
                .ToArray();

        internal static string FormatOrderIntegrationDeviceActivity(
            DateTimeOffset? lastActivityUtc,
            int processedCount)
        {
            return lastActivityUtc.HasValue
                ? $"{lastActivityUtc.Value.ToLocalTime():HH:mm} 已处理 {processedCount} 条联动数据"
                : "暂无联动数据";
        }

        internal bool RevokeExtensionAuthorization(string extensionInstanceId) =>
            _extensionAuthorizationStore?.Revoke(extensionInstanceId) == true;

    }
}
