using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring;

public partial class ViewerClientWindow : Window
{
    private enum ConnectionViewState
    {
        Searching,
        Ready,
        Connecting,
        Connected,
        Offline,
        Error
    }

    private readonly AppConfig _config;
    private readonly string _deploymentPreset;
    private readonly bool _bindingOnly;
    private readonly DispatcherTimer _onlineTimer;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _qrScanCancellation;
    private PackingProofNodeInfo? _boundHost;
    private ManualHostConnectionWindow? _manualConnectionWindow;
    private IReadOnlyList<RecordingDeviceInfo> _knownRecordingDevices = [];
    private bool _deploymentSetupPersisted;
    private bool _isSearching;
    private bool _isConnecting;
    private bool _isChoosingHost;
    private bool _testOrderSending;
    private ConnectionViewState _connectionViewState = ConnectionViewState.Searching;

    public ViewerClientWindow(AppConfig config, string? deploymentPreset = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _deploymentPreset = string.Equals(
            deploymentPreset,
            DeploymentPresets.RecordingWorkstation,
            StringComparison.OrdinalIgnoreCase)
                ? DeploymentPresets.RecordingWorkstation
                : DeploymentPresets.ViewerClient;
        _bindingOnly = _deploymentPreset == DeploymentPresets.RecordingWorkstation;
        InitializeComponent();
        if (_bindingOnly)
        {
            Title = "PackingProof 保存主机";
            WindowHeadingText.Text = "保存主机";
            WindowDescriptionText.Text = "选择一台电脑保存录像；暂时不设置也可以先录像";
            SwitchPurposeButton.Visibility = Visibility.Collapsed;
            ViewerActionPanel.Visibility = Visibility.Collapsed;
            UserscriptStatusText.Visibility = Visibility.Collapsed;
            DiscoveryHeadingText.Text = "选择保存主机";
            SearchStatusText.Text = "正在查找同一局域网中可用的保存主机";
            DeferBindingButton.Visibility = Visibility.Visible;
            ScanPhonePairingButton.Visibility = Visibility.Visible;
            ViewerDetailsPanel.Visibility = Visibility.Collapsed;
        }
        ApplyConnectionViewState(
            ConnectionViewState.Searching,
            _bindingOnly
                ? "正在查找同一局域网中可用的保存主机"
                : "正在搜索同一网络中的主机");
        _onlineTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _onlineTimer.Tick += async (_, _) => await RefreshBoundHostAsync();
        Loaded += ViewerClientWindow_Loaded;
        Closed += (_, _) =>
        {
            if (!_deploymentSetupPersisted)
                PersistPurposeWithoutCompletion();
            _onlineTimer.Stop();
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _qrScanCancellation?.Cancel();
            _qrScanCancellation?.Dispose();
            CloseManualConnectionWindow();
        };
    }

    private void PersistPurposeWithoutCompletion()
    {
        try
        {
            WorkstationConfigStore.TryUpdate(
                config =>
                {
                    config.DeploymentPreset = _deploymentPreset;
                    config.DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion;
                    config.WorkstationRole = _bindingOnly
                        ? WorkstationRoles.CameraMonitor
                        : "";
                    config.EnableWebServer = false;
                },
                out _,
                out _);
        }
        catch
        {
            // 保存失败不阻塞关闭；下次启动仍会回到用途选择
        }
    }

    private async void ViewerClientWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshBoundHostAsync();
        if (_boundHost == null && (!_bindingOnly || !HasSavedHost()))
            await SearchHostsAsync();
        _onlineTimer.Start();
    }

    private async Task RefreshBoundHostAsync(bool allowWhileConnecting = false)
    {
        if (_isSearching || (_isConnecting && !allowWhileConnecting))
            return;

        string address = _config.LastKnownHostAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            SetOffline("尚未绑定主机");
            return;
        }

        PackingProofNodeInfo? node = await WorkstationNetwork.GetNodeInfoAsync(address);
        if (node == null
            || (!string.IsNullOrWhiteSpace(_config.LastKnownHostNodeId)
                && !string.Equals(node.NodeId, _config.LastKnownHostNodeId, StringComparison.OrdinalIgnoreCase)))
        {
            SetOffline("主机离线或身份已变化");
            return;
        }

        _boundHost = node;
        CompleteDeploymentSetup(node);
        HostNameText.Text = node.NodeName;
        HostAddressText.Text = node.Address;
        OnlineStatusText.Text = "在线";
        Brush onlineStatusBrush = (Brush)FindResource("AccentGreen");
        OnlineStatusText.Foreground = onlineStatusBrush;
        OnlineStatusIndicator.Fill = onlineStatusBrush;
        OpenWebButton.IsEnabled = true;
        if (_bindingOnly)
        {
            if (!_isChoosingHost)
                ApplyConnectionViewState(ConnectionViewState.Connected, "");
            return;
        }

        Task<IReadOnlyList<RecordingDeviceInfo>> activeDevicesTask =
            WorkstationNetwork.GetRecordingDevicesAsync(node.Address);
        Task<IReadOnlyList<RecordingDeviceInfo>> knownDevicesTask =
            WorkstationNetwork.GetRecordingDevicesAsync(node.Address, includeKnown: true);
        await Task.WhenAll(activeDevicesTask, knownDevicesTask);
        IReadOnlyList<RecordingDeviceInfo> devices = await activeDevicesTask;
        _knownRecordingDevices = await knownDevicesTask;
        CapabilitiesText.Text = node.CapabilitySummary;
        RecorderCountText.Text = devices.Count.ToString();
        OpenWebButton.IsEnabled = true;
        UserscriptButton.IsEnabled = _knownRecordingDevices.Count > 0;
        SendTestOrderButton.IsEnabled = true;
        RefreshUserscriptStatus();
        if (!_isChoosingHost)
            ApplyConnectionViewState(ConnectionViewState.Connected, "");
    }

    private void SetOffline(string status)
    {
        _boundHost = null;
        bool hasSavedHost = HasSavedHost();
        HostNameText.Text = hasSavedHost
            ? FirstNotEmpty(_config.LastKnownHostNodeName, "已绑定的保存主机")
            : "尚未绑定";
        HostAddressText.Text = string.IsNullOrWhiteSpace(_config.LastKnownHostAddress)
            ? "—"
            : _config.LastKnownHostAddress;
        OnlineStatusText.Text = _bindingOnly && hasSavedHost
            ? "暂时离线，稍后会自动重试"
            : status;
        Brush offlineStatusBrush = (Brush)FindResource("TextSecondary");
        OnlineStatusText.Foreground = offlineStatusBrush;
        OnlineStatusIndicator.Fill = offlineStatusBrush;
        CapabilitiesText.Text = "—";
        RecorderCountText.Text = "0";
        OpenWebButton.IsEnabled = false;
        UserscriptButton.IsEnabled = false;
        SendTestOrderButton.IsEnabled = false;
        UserscriptStatusText.Text = "主机离线，暂时无法检查订单联动设备";
        bool showBoundRecordingHost =
            _bindingOnly && hasSavedHost && !_isChoosingHost;
        if (!showBoundRecordingHost)
        {
            OfflineHostNotice.Visibility = hasSavedHost
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (hasSavedHost)
            {
                OfflineHostText.Text =
                    $"{HostNameText.Text} · {HostAddressText.Text} · {status}";
            }
        }
        ApplyConnectionViewState(
            showBoundRecordingHost
                ? ConnectionViewState.Offline
                : ConnectionViewState.Ready,
            showBoundRecordingHost
                ? ""
                : hasSavedHost
                    ? "正在查找其他可用主机"
                    : status);
    }

    private async Task SearchHostsAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        var searchCancellation = new CancellationTokenSource();
        _searchCancellation = searchCancellation;
        _isSearching = true;
        HostsList.ItemsSource = null;
        ApplyConnectionViewState(
            ConnectionViewState.Searching,
            _bindingOnly
                ? "正在查找同一局域网中可用的保存主机"
                : "正在搜索同一网络中的主机");
        await Dispatcher.Yield(DispatcherPriority.Render);
        try
        {
            IReadOnlyList<PackingProofNodeInfo> hosts = await WorkstationNetwork.FindHostsAsync(
                _config.LastKnownHostAddress,
                _config.WebServerPort,
                progress: null,
                token: searchCancellation.Token);
            if (!ReferenceEquals(searchCancellation, _searchCancellation))
                return;

            IReadOnlyList<PackingProofNodeInfo> compatibleHosts =
                FilterDiscoveredHosts(hosts, _bindingOnly);
            HostsList.ItemsSource = compatibleHosts;
            if (compatibleHosts.Count == 1)
                HostsList.SelectedIndex = 0;

            string message = compatibleHosts.Count switch
            {
                0 when _bindingOnly && hosts.Any(IsRecordingReceiverHost) =>
                    "找到了保存主机，但版本过旧，请更新保存主机电脑",
                0 when _bindingOnly && hosts.Count > 0 =>
                    "找到了主机，但没有可接收录像的保存主机",
                0 => "没有找到主机，请检查两台电脑是否连接同一网络",
                1 => "找到 1 台主机，确认后即可连接",
                _ => $"找到 {compatibleHosts.Count} 台主机，请选择要连接的主机"
            };
            ApplyConnectionViewState(ConnectionViewState.Ready, message);
            if (_bindingOnly)
            {
                PackingProofNodeInfo? preferred = compatibleHosts.FirstOrDefault(host =>
                    !string.IsNullOrWhiteSpace(_config.LastKnownHostNodeId)
                    && string.Equals(host.NodeId, _config.LastKnownHostNodeId, StringComparison.OrdinalIgnoreCase));
                PackingProofNodeInfo? automatic = preferred ?? (compatibleHosts.Count == 1 ? compatibleHosts[0] : null);
                bool differentHostWithPending = automatic != null
                    && !string.IsNullOrWhiteSpace(_config.LastKnownHostNodeId)
                    && !string.Equals(automatic.NodeId, _config.LastKnownHostNodeId, StringComparison.OrdinalIgnoreCase)
                    && Owner?.DataContext is MainViewModel viewModel
                    && viewModel.PendingRecordingTransferCount > 0;
                if (automatic != null && !differentHostWithPending)
                    await BindHostAsync(automatic);
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(searchCancellation, _searchCancellation))
                ApplyConnectionViewState(ConnectionViewState.Ready, "搜索已取消");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(searchCancellation, _searchCancellation))
            {
                ApplyConnectionViewState(
                    ConnectionViewState.Error,
                    $"搜索主机失败：{ex.Message}");
            }
        }
        finally
        {
            if (ReferenceEquals(searchCancellation, _searchCancellation))
            {
                _isSearching = false;
                UpdateConnectionControls();
            }
        }
    }

    private async Task BindHostAsync(PackingProofNodeInfo node, string? accessKey = null)
    {
        if (!node.IsValidHost)
        {
            ShowConnectionError(_bindingOnly
                ? "这台电脑暂时不能作为保存主机"
                : "该地址不是有效的 PackingProof 主机");
            return;
        }
        if (_bindingOnly && !IsRecordingReceiverHost(node))
        {
            ShowConnectionError("该主机未启用录像接收能力");
            return;
        }
        if (_bindingOnly && !BackupCompatibilityPolicy.IsCompatibleHost(node.BackupCompatibility))
        {
            ShowConnectionError("保存主机版本过旧，请更新保存主机电脑");
            return;
        }

        _isConnecting = true;
        string backupCredential = "";
        if (_bindingOnly)
        {
            ApplyConnectionViewState(
                ConnectionViewState.Connecting,
                $"已找到“{node.NodeName}”，等待保存主机允许连接");
            try
            {
                BackupDeviceEnrollmentResult enrollment = await WorkstationNetwork.EnrollBackupDeviceAsync(
                    node.Address,
                    _config.NodeId,
                    string.IsNullOrWhiteSpace(_config.NodeName)
                        ? Environment.MachineName
                        : _config.NodeName,
                    "pc");
                if (!string.Equals(enrollment.ComputerId, node.NodeId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("保存主机身份在连接过程中发生变化，请重新搜索");
                backupCredential = enrollment.DeviceToken;
            }
            catch (Exception ex)
            {
                _isConnecting = false;
                ShowConnectionError(ex.Message);
                return;
            }
        }
        ApplyConnectionViewState(
            ConnectionViewState.Connecting,
            $"正在连接“{node.NodeName}”");
        if (!WorkstationConfigStore.TryUpdate(
                config =>
                {
                    config.DeploymentPreset = _deploymentPreset;
                    config.DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion;
                    config.WorkstationRole = _bindingOnly
                        ? WorkstationRoles.CameraMonitor
                        : "";
                    config.EnableWebServer = false;
                    config.LastKnownHostNodeId = node.NodeId;
                    config.LastKnownHostNodeName = node.NodeName;
                    config.LastKnownHostAddress = node.Address;
                    if (_bindingOnly)
                    {
                        config.LastKnownHostAccessKey = backupCredential;
                        config.LastKnownHostBackupAuthVersion =
                            BackupRequestAuthentication.CurrentVersion;
                        config.BackupConnectionSchemaVersion =
                            AppConfig.CurrentBackupConnectionSchemaVersion;
                    }
                    AppConfig.MarkDeploymentSetupCompleted(config);
                },
                out AppConfig saved,
                out string error))
        {
            _isConnecting = false;
            ShowConnectionError($"连接保存主机失败：{error}");
            return;
        }

        _config.LastKnownHostNodeId = saved.LastKnownHostNodeId;
        _config.LastKnownHostNodeName = saved.LastKnownHostNodeName;
        _config.LastKnownHostAddress = saved.LastKnownHostAddress;
        _config.LastKnownHostAccessKey = saved.LastKnownHostAccessKey;
        _config.LastKnownHostBackupAuthVersion = saved.LastKnownHostBackupAuthVersion;
        _config.BackupConnectionSchemaVersion = saved.BackupConnectionSchemaVersion;
        _config.FirstUseWizardCompleted = saved.FirstUseWizardCompleted;
        _config.DeploymentSetupVersion = saved.DeploymentSetupVersion;
        _config.RecordingSetupVersion = saved.RecordingSetupVersion;
        _deploymentSetupPersisted = true;
        _boundHost = node;
        _isChoosingHost = false;
        try
        {
            await RefreshBoundHostAsync(allowWhileConnecting: true);
        }
        finally
        {
            _isConnecting = false;
            UpdateConnectionControls();
        }
        if (_boundHost != null)
        {
            CloseManualConnectionWindow();
            if (_bindingOnly)
            {
                DialogResult = true;
                Close();
            }
        }
    }

    private void OpenWeb_Click(object sender, RoutedEventArgs e)
    {
        string address = _boundHost?.Address ?? _config.LastKnownHostAddress;
        if (!WorkstationNetwork.TryOpenUrl(address, out string error))
            AppDialog.ShowMessage(this, error, "打开录像网页失败", AppDialogSeverity.Error);
    }

    private async void SearchHosts_Click(object sender, RoutedEventArgs e) => await SearchHostsAsync();

    private async void ChangeHost_Click(object sender, RoutedEventArgs e)
    {
        bool hasSavedHost = HasSavedHost();
        if (_bindingOnly
            && hasSavedHost
            && !AppDialog.Confirm(
                this,
                "更换后，新的录像会发送到新主机。尚未上传完成的录像仍会保留在本机",
                "更换保存主机",
                confirmText: "继续选择",
                cancelText: "保留当前",
                severity: AppDialogSeverity.Information))
        {
            return;
        }

        _isChoosingHost = true;
        await SearchHostsAsync();
    }

    private void DeferBinding_Click(object sender, RoutedEventArgs e) => Close();

    private void InstallUserscript_Click(object sender, RoutedEventArgs e)
    {
        string address = _boundHost?.Address ?? _config.LastKnownHostAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            AppDialog.ShowMessage(this, "请先搜索并绑定 PackingProof 主机", "安装快递助手联动",
                AppDialogSeverity.Information);
            return;
        }

        if (!UserscriptGuideNavigation.TryOpen(address, out string error))
        {
            AppDialog.ShowMessage(this, error, "安装快递助手联动失败", AppDialogSeverity.Error);
            return;
        }

        UserscriptTargetState.MarkGuideOpened(_config, _knownRecordingDevices);
        RefreshUserscriptStatus();
    }

    private async void SendTestOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_testOrderSending || _boundHost == null)
            return;

        _testOrderSending = true;
        SendTestOrderButton.IsEnabled = false;
        SendTestOrderButtonText.Text = "正在发送";
        try
        {
            WorkstationNetwork.TestOrderBroadcastResult result =
                await WorkstationNetwork.SendTestOrderToRecordingDevicesAsync(_boundHost.Address);
            AppDialogSeverity severity = result.HasTargets && result.FailureCount == 0
                ? AppDialogSeverity.Information
                : AppDialogSeverity.Warning;
            AppDialog.ShowMessage(
                this,
                WorkstationNetwork.FormatTestOrderBroadcastResult(result),
                "发送测试订单",
                severity);
        }
        finally
        {
            _testOrderSending = false;
            SendTestOrderButton.IsEnabled = _boundHost != null;
            SendTestOrderButtonText.Text = "发送测试订单";
        }
    }

    private void RefreshUserscriptStatus()
    {
        UserscriptTargetStatus status = UserscriptTargetState.GetStatus(
            _config,
            _knownRecordingDevices);
        UserscriptStatusText.Text = status.StatusText;
        UserscriptButtonText.Text = AppLanguage.Get(status.ButtonText);
    }

    private void SwitchPurpose_Click(object sender, RoutedEventArgs e)
    {
        var selector = new WorkstationSelectionWindow(DeploymentPresets.ViewerClient)
        {
            Owner = this
        };
        if (selector.ShowDialog() != true || string.IsNullOrWhiteSpace(selector.SelectedPreset))
            return;

        if (string.Equals(
                DeploymentPresets.ViewerClient,
                selector.SelectedPreset,
                StringComparison.OrdinalIgnoreCase))
        {
            OnlineStatusText.Text = "当前已经是连接已有主机用途";
            return;
        }

        if (!WorkstationConfigStore.TryUpdate(
                config =>
                {
                    config.DeploymentPreset = selector.SelectedPreset;
                    if (selector.SelectedPreset == DeploymentPresets.RecordingWorkstation)
                    {
                        config.RecordingWorkstationActivatedAtUtc = DateTime.UtcNow;
                        RecordingWorkstationCachePolicy.ConfigureInitialLocation(
                            config,
                            preserveExistingLocation: true);
                    }
                    config.DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion;
                    config.WorkstationRole = DeploymentCapabilities
                        .ForPreset(selector.SelectedPreset)
                        .IsRecordingDevice
                            ? WorkstationRoles.CameraMonitor
                            : selector.SelectedPreset == DeploymentPresets.MobileBackupHost
                                ? WorkstationRoles.PrintStation
                                : "";
                    config.EnableWebServer = DeploymentCapabilities
                        .ForPreset(selector.SelectedPreset)
                        .CanRunWebServer;
                },
                out AppConfig savedConfig,
                out string error))
        {
            AppDialog.ShowMessage(this, $"用途保存失败：{error}", "切换用途",
                AppDialogSeverity.Error);
            return;
        }

        // 新用途已保存到磁盘，关闭窗口时不要再覆盖回当前查看器用途
        _deploymentSetupPersisted = true;
        _config.DeploymentPreset = savedConfig.DeploymentPreset;
        _config.DeploymentSchemaVersion = savedConfig.DeploymentSchemaVersion;
        _config.WorkstationRole = savedConfig.WorkstationRole;
        _config.EnableWebServer = savedConfig.EnableWebServer;
        WorkstationNetwork.RestartAfterPurposeChange(this);
    }

    private void CompleteDeploymentSetup(PackingProofNodeInfo node)
    {
        if (_deploymentSetupPersisted)
            return;

        if (!WorkstationConfigStore.TryUpdate(
                config =>
                {
                    config.DeploymentPreset = _deploymentPreset;
                    config.DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion;
                    config.WorkstationRole = _bindingOnly
                        ? WorkstationRoles.CameraMonitor
                        : "";
                    config.EnableWebServer = false;
                    config.LastKnownHostNodeId = node.NodeId;
                    config.LastKnownHostNodeName = node.NodeName;
                    config.LastKnownHostAddress = node.Address;
                    AppConfig.MarkDeploymentSetupCompleted(config);
                },
                out AppConfig saved,
                out _))
        {
            return;
        }

        _config.DeploymentPreset = saved.DeploymentPreset;
        _config.DeploymentSchemaVersion = saved.DeploymentSchemaVersion;
        _config.WorkstationRole = saved.WorkstationRole;
        _config.EnableWebServer = saved.EnableWebServer;
        _config.LastKnownHostNodeId = saved.LastKnownHostNodeId;
        _config.LastKnownHostNodeName = saved.LastKnownHostNodeName;
        _config.LastKnownHostAddress = saved.LastKnownHostAddress;
        _config.FirstUseWizardCompleted = saved.FirstUseWizardCompleted;
        _config.DeploymentSetupVersion = saved.DeploymentSetupVersion;
        _config.RecordingSetupVersion = saved.RecordingSetupVersion;
        _deploymentSetupPersisted = true;
    }

    private async void BindSelected_Click(object sender, RoutedEventArgs e)
    {
        await BindSelectedHostAsync();
    }

    private async void HostsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await BindSelectedHostAsync();
    }

    private void HostsList_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateConnectionControls();
    }

    private async Task BindSelectedHostAsync()
    {
        if (_isSearching
            || _isConnecting
            || HostsList.SelectedItem is not PackingProofNodeInfo node)
        {
            return;
        }

        if (_bindingOnly
            && !string.IsNullOrWhiteSpace(_config.LastKnownHostNodeId)
            && !string.Equals(node.NodeId, _config.LastKnownHostNodeId, StringComparison.OrdinalIgnoreCase)
            && Owner?.DataContext is MainViewModel viewModel
            && viewModel.PendingRecordingTransferCount > 0
            && !AppDialog.Confirm(
                this,
                $"本机还有 {viewModel.PendingRecordingTransferCount} 个录像等待原保存主机。继续后，这些录像将改为上传到“{node.NodeName}”",
                "更换保存主机",
                confirmText: "继续连接",
                cancelText: "保留原主机",
                severity: AppDialogSeverity.Warning))
        {
            return;
        }
        await BindHostAsync(node);
    }

    private void OpenManualConnection_Click(object sender, RoutedEventArgs e)
    {
        OpenManualConnectionWindow();
    }

    private async void ScanPhonePairing_Click(object sender, RoutedEventArgs e)
    {
        if (!_bindingOnly || Owner?.DataContext is not MainViewModel viewModel)
        {
            ShowConnectionError("未找到正在运行的电脑录像界面，请使用手动连接");
            return;
        }

        _qrScanCancellation?.Cancel();
        _qrScanCancellation?.Dispose();
        _qrScanCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        ScanPhonePairingButton.IsEnabled = false;
        ApplyConnectionViewState(
            ConnectionViewState.Connecting,
            "请将保存主机的连接二维码对准电脑摄像头");
        try
        {
            string input = await viewModel.ScanHostPairingQrAsync(_qrScanCancellation.Token);
            WorkstationNetwork.ParseHostConnectionInput(input, out string address, out _);
            if (string.IsNullOrWhiteSpace(address))
                throw new InvalidOperationException("这不是有效的保存主机二维码");

            PackingProofNodeInfo? node = await WorkstationNetwork.GetNodeInfoAsync(
                address,
                _qrScanCancellation.Token);
            if (node == null)
                throw new InvalidOperationException("无法连接二维码中的保存主机，请检查两台设备是否在同一网络");
            await BindHostAsync(node);
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded)
                ApplyConnectionViewState(ConnectionViewState.Ready, "未识别到连接码，可重新扫码或手动连接");
        }
        catch (Exception ex)
        {
            ShowConnectionError(ex.Message);
        }
        finally
        {
            if (IsLoaded)
                ScanPhonePairingButton.IsEnabled = true;
        }
    }

    private void OpenManualConnectionWindow(string? error = null)
    {
        if (_manualConnectionWindow != null)
        {
            if (!string.IsNullOrWhiteSpace(error))
                _manualConnectionWindow.ShowError(error);
            _manualConnectionWindow.Activate();
            return;
        }

        var window = new ManualHostConnectionWindow(_bindingOnly) { Owner = this };
        _manualConnectionWindow = window;
        window.ConnectionSubmitted += ManualConnectionWindow_ConnectionSubmitted;
        window.Closed += ManualConnectionWindow_Closed;
        window.Show();
        if (!string.IsNullOrWhiteSpace(error))
            window.ShowError(error);
    }

    private void CloseManualConnectionWindow()
    {
        ManualHostConnectionWindow? window = _manualConnectionWindow;
        if (window == null)
            return;

        window.ConnectionSubmitted -= ManualConnectionWindow_ConnectionSubmitted;
        window.Closed -= ManualConnectionWindow_Closed;
        _manualConnectionWindow = null;
        window.Close();
    }

    private void ManualConnectionWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not ManualHostConnectionWindow window)
            return;

        window.ConnectionSubmitted -= ManualConnectionWindow_ConnectionSubmitted;
        window.Closed -= ManualConnectionWindow_Closed;
        if (ReferenceEquals(window, _manualConnectionWindow))
            _manualConnectionWindow = null;
    }

    private async void ManualConnectionWindow_ConnectionSubmitted(string input)
    {
        WorkstationNetwork.ParseHostConnectionInput(
            input,
            out string address,
            out string accessKey);
        ManualHostConnectionWindow? window = _manualConnectionWindow;
        if (window == null)
            return;
        if (string.IsNullOrWhiteSpace(address))
        {
            window.ShowError("请输入主机地址或完整连接链接");
            return;
        }
        if (_bindingOnly && accessKey.Length < 16)
        {
            window.ShowError("录制工位需要粘贴保存主机提供的完整连接链接");
            return;
        }

        _isConnecting = true;
        ApplyConnectionViewState(ConnectionViewState.Connecting, "正在验证主机");
        PackingProofNodeInfo? node;
        try
        {
            node = await WorkstationNetwork.GetNodeInfoAsync(address);
        }
        catch (Exception ex)
        {
            _isConnecting = false;
            ShowConnectionError($"验证主机失败：{ex.Message}");
            return;
        }
        finally
        {
            _isConnecting = false;
            UpdateConnectionControls();
        }
        if (!ReferenceEquals(window, _manualConnectionWindow))
        {
            if (IsLoaded)
            {
                ApplyConnectionViewState(
                    ConnectionViewState.Ready,
                    "已取消手动连接");
            }
            return;
        }
        if (node == null)
        {
            ShowConnectionError("无法连接该主机，请检查地址和网络后重试");
            return;
        }

        await BindHostAsync(node, accessKey);
        if (_boundHost == null
            && ReferenceEquals(window, _manualConnectionWindow))
        {
            window.ShowError(SearchStatusText.Text);
        }
    }

    private static bool IsRecordingReceiverHost(PackingProofNodeInfo node) =>
        node.Capabilities.Contains(
            PackingProofCapabilities.MobileBackup,
            StringComparer.OrdinalIgnoreCase);

    private static bool IsCompatibleRecordingReceiverHost(PackingProofNodeInfo node) =>
        IsRecordingReceiverHost(node)
        && BackupCompatibilityPolicy.IsCompatibleHost(node.BackupCompatibility);

    internal static IReadOnlyList<PackingProofNodeInfo> FilterDiscoveredHosts(
        IEnumerable<PackingProofNodeInfo> hosts,
        bool recordingWorkstation) =>
        recordingWorkstation
            ? hosts.Where(IsCompatibleRecordingReceiverHost).ToArray()
            : hosts.ToArray();

    private void ShowConnectionError(string message)
    {
        _isConnecting = false;
        ApplyConnectionViewState(ConnectionViewState.Error, message);
        _manualConnectionWindow?.ShowError(message);
    }

    private void ApplyConnectionViewState(ConnectionViewState state, string message)
    {
        _connectionViewState = state;
        bool showBoundHost = state == ConnectionViewState.Connected
            || (_bindingOnly
                && state == ConnectionViewState.Offline
                && HasSavedHost());
        HostSummaryBorder.Visibility = showBoundHost
            ? Visibility.Visible
            : Visibility.Collapsed;
        DiscoveryPanel.Visibility = showBoundHost
            ? Visibility.Collapsed
            : Visibility.Visible;
        ViewerActionPanel.Visibility = !_bindingOnly && state == ConnectionViewState.Connected
            ? Visibility.Visible
            : Visibility.Collapsed;
        UserscriptStatusText.Visibility = !_bindingOnly && state == ConnectionViewState.Connected
            ? Visibility.Visible
            : Visibility.Collapsed;
        BindingBoundActionsPanel.Visibility = _bindingOnly && showBoundHost
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateSearchProgress(state == ConnectionViewState.Searching);
        if (!string.IsNullOrWhiteSpace(message))
            SearchStatusText.Text = message;
        if (state == ConnectionViewState.Connected)
            OfflineHostNotice.Visibility = Visibility.Collapsed;
        UpdateConnectionControls();
    }

    private void UpdateSearchProgress(bool searching)
    {
        SearchProgressTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SearchProgressTransform.X = -140;
        SearchProgressBar.Visibility = searching
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!searching)
            return;

        var animation = new DoubleAnimation
        {
            From = -140,
            To = 760,
            Duration = TimeSpan.FromSeconds(1.35),
            RepeatBehavior = RepeatBehavior.Forever
        };
        SearchProgressTransform.BeginAnimation(
            TranslateTransform.XProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void UpdateConnectionControls()
    {
        bool busy = _isSearching
            || _isConnecting
            || _connectionViewState is ConnectionViewState.Searching
                or ConnectionViewState.Connecting;
        SearchHostsButton.IsEnabled = !busy;
        HostsList.IsEnabled = !busy;
        BindSelectedButton.IsEnabled =
            !busy && HostsList.SelectedItem is PackingProofNodeInfo;
        ManualConnectionButton.IsEnabled = !busy;
        BindSelectedButtonText.Text = _isConnecting
            ? "正在连接"
            : "连接保存主机";
    }

    private static string FirstNotEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";

    private bool HasSavedHost() =>
        !string.IsNullOrWhiteSpace(_config.LastKnownHostNodeId)
        && !string.IsNullOrWhiteSpace(_config.LastKnownHostAddress);
}
