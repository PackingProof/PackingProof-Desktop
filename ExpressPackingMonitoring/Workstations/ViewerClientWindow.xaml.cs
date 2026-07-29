using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;

namespace ExpressPackingMonitoring;

public partial class ViewerClientWindow : Window
{
    private readonly AppConfig _config;
    private readonly string _deploymentPreset;
    private readonly bool _bindingOnly;
    private readonly DispatcherTimer _onlineTimer;
    private CancellationTokenSource? _searchCancellation;
    private PackingProofNodeInfo? _boundHost;
    private IReadOnlyList<RecordingDeviceInfo> _knownRecordingDevices = [];
    private bool _deploymentSetupPersisted;
    private bool _testOrderSending;

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
            Title = "PackingProof 绑定保存主机";
            WindowHeadingText.Text = "绑定保存主机";
            WindowDescriptionText.Text = "录像完成后将自动上传到这台主机，本机只作为临时缓存";
        }
        _onlineTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _onlineTimer.Tick += async (_, _) => await RefreshBoundHostAsync();
        Loaded += ViewerClientWindow_Loaded;
        Closed += (_, _) =>
        {
            _onlineTimer.Stop();
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
        };
    }

    private async void ViewerClientWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshBoundHostAsync();
        if (_boundHost == null)
            await SearchHostsAsync();
        _onlineTimer.Start();
    }

    private async Task RefreshBoundHostAsync()
    {
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
        Task<IReadOnlyList<RecordingDeviceInfo>> activeDevicesTask =
            WorkstationNetwork.GetRecordingDevicesAsync(node.Address);
        Task<IReadOnlyList<RecordingDeviceInfo>> knownDevicesTask =
            WorkstationNetwork.GetRecordingDevicesAsync(node.Address, includeKnown: true);
        await Task.WhenAll(activeDevicesTask, knownDevicesTask);
        IReadOnlyList<RecordingDeviceInfo> devices = await activeDevicesTask;
        _knownRecordingDevices = await knownDevicesTask;
        HostNameText.Text = node.NodeName;
        HostAddressText.Text = node.Address;
        OnlineStatusText.Text = "在线";
        OnlineStatusText.Foreground = TryFindResource("AccentGreen") as Brush ?? Brushes.Green;
        CapabilitiesText.Text = node.CapabilitySummary;
        RecorderCountText.Text = devices.Count.ToString();
        OpenWebButton.IsEnabled = true;
        UserscriptButton.IsEnabled = _knownRecordingDevices.Count > 0;
        SendTestOrderButton.IsEnabled = true;
        RefreshUserscriptStatus();
    }

    private void SetOffline(string status)
    {
        _boundHost = null;
        HostNameText.Text = string.IsNullOrWhiteSpace(_config.LastKnownHostNodeId) ? "尚未绑定" : "已绑定主机";
        HostAddressText.Text = string.IsNullOrWhiteSpace(_config.LastKnownHostAddress)
            ? "—"
            : _config.LastKnownHostAddress;
        OnlineStatusText.Text = status;
        OnlineStatusText.Foreground = TryFindResource("TextSecondary") as Brush ?? Brushes.Gray;
        CapabilitiesText.Text = "—";
        RecorderCountText.Text = "0";
        OpenWebButton.IsEnabled = false;
        UserscriptButton.IsEnabled = false;
        SendTestOrderButton.IsEnabled = false;
        UserscriptStatusText.Text = "主机离线，暂时无法检查订单联动设备";
    }

    private async Task SearchHostsAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        DiscoveryPanel.Visibility = Visibility.Visible;
        HostsList.ItemsSource = null;
        SearchStatusText.Text = "正在搜索局域网中的 PackingProof 主机";
        var progress = new Progress<string>(message => SearchStatusText.Text = message);
        try
        {
            IReadOnlyList<PackingProofNodeInfo> hosts = await WorkstationNetwork.FindHostsAsync(
                _config.LastKnownHostAddress,
                _config.WebServerPort,
                progress,
                _searchCancellation.Token);
            HostsList.ItemsSource = hosts;
            SearchStatusText.Text = hosts.Count == 0
                ? "没有发现主机，可以检查网络后重新搜索或手动输入地址"
                : $"找到 {hosts.Count} 台 PackingProof 主机，请选择要绑定的主机";
        }
        catch (OperationCanceledException)
        {
            SearchStatusText.Text = "搜索已取消";
        }
    }

    private async Task BindHostAsync(PackingProofNodeInfo node, string? accessKey = null)
    {
        if (!node.IsValidHost)
        {
            SearchStatusText.Text = "该地址不是有效的 PackingProof 主机";
            return;
        }
        if (_bindingOnly
            && !node.Capabilities.Contains(
                PackingProofCapabilities.MobileBackup,
                StringComparer.OrdinalIgnoreCase))
        {
            SearchStatusText.Text = "该主机未启用录像接收能力";
            return;
        }

        string resolvedAccessKey = accessKey?.Trim() ?? "";
        if (_bindingOnly && resolvedAccessKey.Length < 16)
        {
            SearchStatusText.Text = "请粘贴主机“手机/电脑连接”的完整链接以完成安全配对";
            ManualAddressTextBox.Text = node.Address;
            return;
        }

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
                        config.LastKnownHostAccessKey = resolvedAccessKey;
                    AppConfig.MarkDeploymentSetupCompleted(config);
                },
                out AppConfig saved,
                out string error))
        {
            SearchStatusText.Text = $"保存主机绑定失败：{error}";
            return;
        }

        _config.LastKnownHostNodeId = saved.LastKnownHostNodeId;
        _config.LastKnownHostNodeName = saved.LastKnownHostNodeName;
        _config.LastKnownHostAddress = saved.LastKnownHostAddress;
        _config.LastKnownHostAccessKey = saved.LastKnownHostAccessKey;
        _config.FirstUseWizardCompleted = saved.FirstUseWizardCompleted;
        _config.DeploymentSetupVersion = saved.DeploymentSetupVersion;
        _config.RecordingSetupVersion = saved.RecordingSetupVersion;
        _deploymentSetupPersisted = true;
        _boundHost = node;
        DiscoveryPanel.Visibility = Visibility.Collapsed;
        await RefreshBoundHostAsync();
        if (_bindingOnly)
        {
            DialogResult = true;
            Close();
        }
    }

    private void OpenWeb_Click(object sender, RoutedEventArgs e)
    {
        string address = _boundHost?.Address ?? _config.LastKnownHostAddress;
        if (!WorkstationNetwork.TryOpenUrl(address, out string error))
            AppDialog.ShowMessage(this, error, "打开录像网页失败", AppDialogSeverity.Error);
    }

    private async void SearchHosts_Click(object sender, RoutedEventArgs e) => await SearchHostsAsync();

    private async void ChangeHost_Click(object sender, RoutedEventArgs e) => await SearchHostsAsync();

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
        UserscriptButtonText.Text = status.ButtonText;
    }

    private void SwitchPurpose_Click(object sender, RoutedEventArgs e)
    {
        var selector = new WorkstationSelectionWindow { Owner = this };
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
                        config.RecordingWorkstationActivatedAtUtc = DateTime.UtcNow;
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
        if (HostsList.SelectedItem is PackingProofNodeInfo node)
            await BindHostAsync(
                node,
                string.Equals(node.NodeId, _config.LastKnownHostNodeId, StringComparison.OrdinalIgnoreCase)
                    ? _config.LastKnownHostAccessKey
                    : "");
    }

    private async void HostsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HostsList.SelectedItem is PackingProofNodeInfo node)
            await BindHostAsync(
                node,
                string.Equals(node.NodeId, _config.LastKnownHostNodeId, StringComparison.OrdinalIgnoreCase)
                    ? _config.LastKnownHostAccessKey
                    : "");
    }

    private async void BindManual_Click(object sender, RoutedEventArgs e)
    {
        WorkstationNetwork.ParseHostConnectionInput(
            ManualAddressTextBox.Text,
            out string address,
            out string accessKey);
        SearchStatusText.Text = "正在验证手动输入的主机地址";
        PackingProofNodeInfo? node = await WorkstationNetwork.GetNodeInfoAsync(address);
        if (node == null)
        {
            SearchStatusText.Text = "该地址未返回合法的 PackingProof 主机身份";
            return;
        }

        await BindHostAsync(node, accessKey);
    }
}
