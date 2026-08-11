using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Themes;
using ExpressPackingMonitoring.UI;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ExpressPackingMonitoring;

public partial class PrintWorkstationWindow : Window
{
    public sealed class MobileBackupStatusItem
    {
        public string DeviceId { get; init; } = "";
        public string DisplayText { get; init; } = "";
        public bool IsOnline { get; init; }
    }

    public sealed class MobileBackupToastState : INotifyPropertyChanged
    {
        private string _toastMessage = "";
        private bool _isToastVisible;

        public string ToastMessage
        {
            get => _toastMessage;
            set => SetField(ref _toastMessage, value);
        }

        public bool IsToastVisible
        {
            get => _isToastVisible;
            set => SetField(ref _isToastVisible, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private enum StatusVisual
    {
        Neutral,
        Success,
        Warning,
        Error
    }

    private AppConfig _config;
    private readonly bool _openPlaybackOnStartup;
    private readonly bool _requestLanAccessOnStartup;
    private readonly NoCameraWorkstationHost _host;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly WindowCloseBehaviorController _closeBehaviorController;
    private readonly DispatcherTimer _deviceRefreshTimer;
    private readonly DispatcherTimer _toastTimer;
    private StatisticsWindow? _statisticsWindow;
    private PlaybackWindow? _playbackWindow;
    private bool _loaded;
    private bool _exitRequestedFromTray;
    private bool _deploymentSetupPersisted;
    private bool _testOrderSending;
    private bool _purposeSwitchPending;
    public ObservableCollection<MobileBackupStatusItem> MobileBackupDeviceStatuses { get; } = [];
    public MobileBackupToastState ToastState { get; } = new();

    public PrintWorkstationWindow(
        AppConfig config,
        bool openPlaybackOnStartup = true,
        bool requestLanAccessOnStartup = true,
        bool enableCloseBehaviorPrompt = true)
    {
        InitializeComponent();
        Title = $"{Title} {AppVersion.Current}";
        MaxHeight = CalculateWindowMaxHeight(SystemParameters.WorkArea.Height);
        _config = config;
        _openPlaybackOnStartup = openPlaybackOnStartup;
        _requestLanAccessOnStartup = requestLanAccessOnStartup;
        _host = new NoCameraWorkstationHost(config);
        _host.MobileAppUpdateAvailable += OnMobileAppUpdateAvailable;
        _host.MobileBackupStatusChanged += OnMobileBackupStatusChanged;
        _host.BackupDeviceEnrollmentRequested += ApproveBackupDeviceEnrollment;
        _closeBehaviorController = new WindowCloseBehaviorController(
            this,
            RequestExitFromTray,
            enableCloseBehaviorPrompt);
        _deviceRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _deviceRefreshTimer.Tick += (_, _) =>
        {
            RefreshDeviceSummary();
            RefreshArchiveBackupSummary();
            UpdateHeaderIdentity();
        };
        _toastTimer = new DispatcherTimer();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastState.IsToastVisible = false;
        };
        Loaded += Window_Loaded;
        Closing += Window_Closing;
        Closed += (_, _) =>
        {
            _closeBehaviorController.Dispose();
            _deviceRefreshTimer.Stop();
            _toastTimer.Stop();
            ToastState.IsToastVisible = false;
            _lifetimeCts.Cancel();
            _host.MobileBackupStatusChanged -= OnMobileBackupStatusChanged;
            _host.BackupDeviceEnrollmentRequested -= ApproveBackupDeviceEnrollment;
            _host.Dispose();
            _lifetimeCts.Dispose();
        };
    }

    internal static double CalculateWindowMaxHeight(double workAreaHeight)
    {
        if (!double.IsFinite(workAreaHeight))
            return 720;

        return Math.Min(720, Math.Max(1, workAreaHeight - 32));
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        WindowCloseChoice closeChoice = _closeBehaviorController.HandleClose(
            _config,
            bypassPreference: WorkstationNetwork.IsRestartPending || _exitRequestedFromTray);
        _exitRequestedFromTray = false;
        if (closeChoice != WindowCloseChoice.Exit)
        {
            e.Cancel = true;
            return;
        }

        CloseChildWindows();
    }

    private void RequestExitFromTray()
    {
        _exitRequestedFromTray = true;
        Close();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await StartServiceAsync();
    }

    private async Task StartServiceAsync()
    {
        SetControlsEnabled(false);
        SetStatus("正在启动设备备份服务", "正在打开录像数据库和本机回放服务");
        try
        {
            await _host.StartAsync(
                requestLanAccess: _requestLanAccessOnStartup,
                cancellationToken: _lifetimeCts.Token);
            RefreshServiceDisplay();
            _deviceRefreshTimer.Start();
            if (_host.IsLanAvailable)
                CompleteDeploymentSetup();
            if (_openPlaybackOnStartup)
                OpenLocalPlayback();
        }
        catch (Exception ex)
        {
            SetStatus("服务启动失败", ex.Message, StatusVisual.Error);
        }
        finally
        {
            SetControlsEnabled(_host.IsRunning);
            RepairLanButton.IsEnabled = true;
        }
    }

    private void RefreshServiceDisplay()
    {
        RefreshDeviceSummary();
        RefreshArchiveBackupSummary();
        UpdateHeaderIdentity();
        if (_host.IsLanAvailable)
        {
            SetStatus("设备备份服务已启动", "手机或其他录制电脑可将录像备份到本机，本机和局域网设备均可回放", StatusVisual.Success);
        }
        else
        {
            SetStatus("设备备份服务已启动 · 仅本机可用",
                "本机回放不受影响；需要设备备份或局域网回放时，请点击“修复局域网”",
                StatusVisual.Error);
        }

        ConnectPhoneButton.IsEnabled = _host.IsLanAvailable;
        SendTestOrderButton.IsEnabled = _host.IsLanAvailable && !_testOrderSending;
        RepairLanButton.Visibility = _host.IsLanAvailable ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetControlsEnabled(bool enabled)
    {
        OpenWebButton.IsEnabled = enabled;
        StatisticsButton.IsEnabled = _host.HasDatabase;
        PlaybackButton.IsEnabled = _host.HasDatabase;
        SettingsButton.IsEnabled = true;
        ConnectPhoneButton.IsEnabled = enabled && _host.IsLanAvailable;
        SendTestOrderButton.IsEnabled = enabled && _host.IsLanAvailable && !_testOrderSending;
    }

    private void SetStatus(string title, string _, StatusVisual visual = StatusVisual.Neutral)
    {
        MobileBackupStatusCard.ShortStatusText = visual switch
        {
            StatusVisual.Success => "已就绪",
            StatusVisual.Error
                when title.Contains("仅本机可用", StringComparison.Ordinal) => "仅本机",
            StatusVisual.Error => "启动失败",
            _ => "启动中"
        };
        MobileBackupStatusCard.ShortStatusToolTip = title;
    }

    private void ShowToast(string message, ToastSeverity severity = ToastSeverity.Success)
    {
        _toastTimer.Stop();
        ToastState.ToastMessage = AppLanguage.Translate(message);
        ToastState.IsToastVisible = true;
        _toastTimer.Interval = severity is ToastSeverity.Warning or ToastSeverity.Error
            ? TimeSpan.FromSeconds(4)
            : TimeSpan.FromMilliseconds(2500);
        string iconKey = NotificationVisuals.GetIconKey(severity);
        string brushKey = NotificationVisuals.GetBrushKey(severity);
        if (TryFindResource(iconKey) is Geometry icon)
            ToastIconPath.Data = icon;
        if (TryFindResource(brushKey) is Brush brush)
            ToastIconPath.Fill = brush;
        _toastTimer.Start();
    }

    private void OpenLocalPlayback()
    {
        if (!WorkstationNetwork.TryOpenUrl(_host.LocalPlaybackUrl, out string error))
            ShowToast($"打开本机回放失败：{error}", ToastSeverity.Error);
    }

    private void OpenWeb_Click(object sender, RoutedEventArgs e) => OpenLocalPlayback();

    private void OpenStatistics_Click(object sender, RoutedEventArgs e)
    {
        if (_statisticsWindow is { IsLoaded: true })
        {
            _statisticsWindow.Activate();
            return;
        }

        _statisticsWindow = new StatisticsWindow(_host.Database) { Owner = this };
        _statisticsWindow.Closed += (_, _) => _statisticsWindow = null;
        _statisticsWindow.Show();
    }

    private void OpenPlayback_Click(object sender, RoutedEventArgs e)
    {
        if (_playbackWindow is { IsLoaded: true })
        {
            _playbackWindow.Activate();
            return;
        }

        VideoFolderImportService? importService = _host.HasDatabase && !string.IsNullOrWhiteSpace(_host.StoragePath)
            ? new VideoFolderImportService(
                _host.Database,
                [_host.StoragePath],
                _config.NodeId,
                _config.NodeName)
            : null;
        _playbackWindow = new PlaybackWindow(
            _host.StoragePath,
            _host.Database,
            showDeletedVideos: true,
            importService,
            lastImportFolder: _config.LastVideoImportFolder,
            saveImportFolder: path =>
            {
                _config.LastVideoImportFolder = path;
                WorkstationConfigStore.TryUpdate(
                    config => config.LastVideoImportFolder = path,
                    out _,
                    out _);
            })
        {
            Owner = this
        };
        _playbackWindow.Closed += (_, _) => _playbackWindow = null;
        _playbackWindow.Show();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        AppConfig clonedConfig =
            JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(_config)) ?? new AppConfig();
        var context = new SettingsContext
        {
            Capabilities = SettingsCapabilities.ForPreset(DeploymentPresets.MobileBackupHost),
            ApplyAsync = ApplySettingsAsync,
            ConnectionAddressProvider = () => _host.IsLanAvailable ? _host.LanAccessUrl : _host.LocalPlaybackUrl,
            ShowMobileConnection = ShowMobileConnection,
            CopyMobileConnectionUrl = CopyMobileConnectionUrl,
            OpenUserscriptGuide = OpenUserscriptGuide,
            ShowToast = (message, severity) => ShowToast(message, severity),
            ToastSource = ToastState
        };
        (double diskUsagePercent, string diskUsageText) = GetDiskUsage(_host.StoragePath);
        var window = new SettingsWindow(
            context,
            clonedConfig,
            diskUsagePercent,
            diskUsageText)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void ConnectPhone_Click(object sender, RoutedEventArgs e)
    {
        ShowMobileConnection(this);
    }

    private void ShowMobileConnection(Window owner)
    {
        var dialog = new MobileConnectionWindow(
            _host.LanAccessUrl,
            _config.RequireWebAccessKey,
            _host.IsLanAvailable ? "" : "当前仅本机可用，请先修复局域网",
            canOpenSettings: false)
        {
            Owner = owner
        };
        dialog.ShowDialog();
    }

    private void CopyMobileConnectionUrl()
    {
        string address = _host.IsLanAvailable ? _host.LanAccessUrl : _host.LocalPlaybackUrl;
        if (string.IsNullOrWhiteSpace(address)) return;
        try
        {
            Clipboard.SetDataObject(address, true);
            ShowToast(_host.IsLanAvailable
                ? "已复制局域网地址，请勿转发给无关人员"
                : "已复制本机回放地址，请勿转发给无关人员");
        }
        catch (Exception ex)
        {
            ShowToast($"复制连接地址失败：{ex.Message}", ToastSeverity.Error);
        }
    }

    private void RefreshDeviceSummary()
    {
        UserscriptTargetStatus userscriptStatus = UserscriptTargetState.GetStatus(
            _config,
            _host.GetRecordingDevices(includeKnown: true));
        (string shortStatus, string detailText) =
            UserscriptStatusCardModel.GetCardTexts(userscriptStatus);
        UserscriptStatusCard.ShortStatusText = AppLanguage.Get(shortStatus);
        UserscriptStatusCard.DetailText = AppLanguage.Get(detailText);
        InstallUserscriptButtonText.Text = AppLanguage.Get(userscriptStatus.ButtonText);
        InstallUserscriptButton.IsEnabled = userscriptStatus.CurrentSignature.Length > 0;

        MobileBackupDeviceStatuses.Clear();
        if (!_host.HasDatabase)
        {
            TodayBackupCountTextBlock.Text = "0";
            TotalBackupCountTextBlock.Text = "0";
            AddEmptyDeviceStatus();
            return;
        }

        MobileBackupOverview overview = _host.Database.GetMobileBackupOverview(DateTime.Today);
        foreach (MobileBackupStatusItem status in BuildMobileBackupStatuses(
            overview.DeviceCounts,
            _host.GetRecordingDevices(),
            _config.NodeId))
        {
            MobileBackupDeviceStatuses.Add(status);
        }

        TodayBackupCountTextBlock.Text = overview.TodayCount.ToString();
        TotalBackupCountTextBlock.Text = overview.TotalCount.ToString();
    }

    private void UpdateHeaderIdentity()
    {
        HeaderComputerNameText.Text = GetHostName();
        string address = _host.IsLanAvailable
            ? _host.LanAccessUrl
            : _host.LocalPlaybackUrl;
        HeaderComputerIpText.Text = ExtractHostFromUrl(address);
    }

    private static string ExtractHostFromUrl(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return "";
        if (Uri.TryCreate(address, UriKind.Absolute, out Uri? uri))
            return uri?.Host ?? "";
        int separator = address.LastIndexOf(':');
        return separator > 0 ? address[..separator] : address;
    }

    private void RefreshArchiveBackupSummary()
    {
        (bool isVisible, ArchiveBackupCardState state) = _host.GetArchiveBackupCardState();
        ArchiveBackupStatusCard.Visibility = isVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!isVisible)
            return;
        ArchiveBackupStatusCard.ShortStatusText = state.ShortStatusText;
        ArchiveBackupStatusCard.DetailText = state.DetailText;
    }

    internal static IReadOnlyList<MobileBackupStatusItem> BuildMobileBackupStatuses(
        IEnumerable<MobileBackupDailyCount> counts,
        IEnumerable<RecordingDeviceInfo> devices,
        string localNodeId)
    {
        var statuses = counts
            .Where(item => BackupDeviceIdentity.IsRemote(item.DeviceId, localNodeId))
            .ToDictionary(
            item => item.DeviceId,
            item => (
                Name: string.IsNullOrWhiteSpace(item.DeviceName)
                    ? GetFallbackDeviceName(item.DeviceId, item.DeviceKind)
                    : item.DeviceName,
                Count: item.VideoCount,
                Online: false),
            StringComparer.OrdinalIgnoreCase);

        foreach (RecordingDeviceInfo device in devices
            .Where(device => device.Online
                && (string.Equals(device.DeviceType, "mobile", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(device.DeviceType, "pc", StringComparison.OrdinalIgnoreCase))
                && BackupDeviceIdentity.IsRemote(device.NodeId, localNodeId)))
        {
            statuses.TryGetValue(device.NodeId, out var existing);
            statuses[device.NodeId] = (
                string.IsNullOrWhiteSpace(device.NodeName)
                    ? existing.Name ?? GetFallbackDeviceName(device.NodeId, device.DeviceType)
                    : device.NodeName,
                existing.Count,
                true);
        }

        List<MobileBackupStatusItem> result = statuses
            .OrderBy(item => item.Value.Name, StringComparer.CurrentCulture)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(status => new MobileBackupStatusItem
            {
                DeviceId = status.Key,
                DisplayText = $"{status.Value.Name} · 今日备份 {status.Value.Count} 个",
                IsOnline = status.Value.Online
            })
            .ToList();
        if (result.Count == 0)
        {
            result.Add(new MobileBackupStatusItem
            {
                DisplayText = "暂无手机/电脑设备",
                IsOnline = false
            });
        }
        return result;
    }

    private void AddEmptyDeviceStatus()
    {
        MobileBackupDeviceStatuses.Add(new MobileBackupStatusItem
        {
            DisplayText = "暂无手机/电脑设备",
            IsOnline = false
        });
    }

    private string GetHostName() =>
        string.IsNullOrWhiteSpace(_config.NodeName) ? Environment.MachineName : _config.NodeName;

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

    private void OnMobileBackupStatusChanged()
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (IsLoaded)
                RefreshDeviceSummary();
        });
    }

    private BackupDeviceEnrollmentApprovalDecision ApproveBackupDeviceEnrollment(
        BackupDeviceEnrollmentRequest request) =>
        BackupDeviceEnrollmentApprovalPrompt.Show(this, request);

    private void OnMobileAppUpdateAvailable(MobileAppUpdateAvailableInfo update)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (IsLoaded)
                MobileAppUpdatePrompt.Show(this, update);
        });
    }

    private async void RepairLan_Click(object sender, RoutedEventArgs e)
    {
        RepairLanButton.IsEnabled = false;
        SetStatus("正在修复局域网", "Windows 可能会请求管理员授权");
        bool repaired = await _host.RepairLanAccessAsync(_lifetimeCts.Token);
        RefreshServiceDisplay();
        if (repaired && _host.IsLanAvailable)
            CompleteDeploymentSetup();
        RepairLanButton.IsEnabled = true;
        if (!repaired)
        {
            string message = string.IsNullOrWhiteSpace(_host.ErrorMessage)
                ? "局域网修复失败，请检查防火墙和网络设置"
                : _host.ErrorMessage;
            StatusVisual visual = _host.IsRunning ? StatusVisual.Warning : StatusVisual.Error;
            SetStatus(message, "", visual);
            ShowToast(message, visual == StatusVisual.Error ? ToastSeverity.Error : ToastSeverity.Warning);
        }
    }

    private async Task<bool> ApplySettingsAsync(AppConfig nextConfig)
    {
        AppConfig previousConfig = _config;
        AppConfig.NormalizeAfterLoad(nextConfig);

        if (!string.Equals(
                previousConfig.DeploymentPreset,
                nextConfig.DeploymentPreset,
                StringComparison.OrdinalIgnoreCase))
        {
            return await RunPurposeSwitchAsync(() =>
            {
                if (TrySaveAndActivateConfig(previousConfig, nextConfig, out string error))
                    return true;

                AppDialog.Error(
                    this,
                    $"配置保存失败：{error}",
                    "设置");
                return false;
            });
        }

        _host.UpdateConfig(nextConfig);
        SetControlsEnabled(false);
        SetStatus("正在应用设置", "正在重启本机回放和设备备份服务");
        try
        {
            _playbackWindow?.Close();
            await _host.StartAsync(_requestLanAccessOnStartup, _lifetimeCts.Token);
            if (!TrySaveAndActivateConfig(previousConfig, nextConfig, out string error))
                throw new InvalidOperationException($"配置保存失败：{error}");

            RefreshServiceDisplay();
            SetControlsEnabled(true);
            return true;
        }
        catch (Exception ex)
        {
            _host.UpdateConfig(previousConfig);
            bool recovered = false;
            try
            {
                await _host.StartAsync(_requestLanAccessOnStartup, _lifetimeCts.Token);
                RefreshServiceDisplay();
                SetControlsEnabled(true);
                recovered = true;
            }
            catch
            {
                SetControlsEnabled(false);
            }

            RepairLanButton.IsEnabled = true;
            if (recovered)
            {
                AppDialog.Error(
                    this,
                    ex.Message,
                    "服务重启失败，已恢复原设置");
            }
            else
            {
                SetStatus("服务重启失败", ex.Message, StatusVisual.Error);
            }
            return false;
        }
    }

    private bool TrySaveAndActivateConfig(
        AppConfig previousConfig,
        AppConfig nextConfig,
        out string error)
    {
        if (!WorkstationConfigStore.TrySave(nextConfig, out error))
            return false;

        _config = nextConfig;
        _host.UpdateConfig(nextConfig);
        AutoStartService.Apply(nextConfig.AutoStartOnBoot);
        if (!string.Equals(previousConfig.Theme, nextConfig.Theme, StringComparison.Ordinal) &&
            Enum.TryParse(nextConfig.Theme, out AppTheme theme))
        {
            ThemeManager.ApplyTheme(theme);
        }

        return true;
    }

    private static (double Percent, string Text) GetDiskUsage(string storagePath)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(storagePath));
            if (string.IsNullOrWhiteSpace(root)) return (0, "暂不可用");
            var drive = new DriveInfo(root);
            if (drive.TotalSize <= 0) return (0, "暂不可用");
            double percent = Math.Clamp(
                (drive.TotalSize - drive.AvailableFreeSpace) * 100d / drive.TotalSize,
                0,
                100);
            double usedGB = (drive.TotalSize - drive.AvailableFreeSpace) / 1024d / 1024d / 1024d;
            double totalGB = drive.TotalSize / 1024d / 1024d / 1024d;
            return (percent, $"{usedGB:F1} / {totalGB:F1} GB");
        }
        catch
        {
            return (0, "暂不可用");
        }
    }

    private void CloseChildWindows()
    {
        try { _statisticsWindow?.Close(); } catch { }
        try { _playbackWindow?.Close(); } catch { }
        _statisticsWindow = null;
        _playbackWindow = null;
    }

    private void InstallTool_Click(object sender, RoutedEventArgs e)
    {
        OpenUserscriptGuide();
    }

    private void OpenUserscriptGuide()
    {
        if (!_host.IsLanAvailable)
        {
            ShowToast("局域网服务尚未就绪，请先修复后再安装订单联动", ToastSeverity.Warning);
            return;
        }

        if (!UserscriptGuideNavigation.TryOpen(_host.LanAccessUrl, out string error))
        {
            ShowToast($"打开订单联动安装向导失败：{error}", ToastSeverity.Error);
            return;
        }
        UserscriptTargetState.MarkGuideOpened(
            _config,
            _host.GetRecordingDevices(includeKnown: true));
        RefreshDeviceSummary();
        ShowToast("已打开订单联动安装向导");
    }

    private async void SendTestOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_testOrderSending)
            return;

        _testOrderSending = true;
        SendTestOrderButton.IsEnabled = false;
        SendTestOrderButtonText.Text = "正在发送";
        try
        {
            WorkstationNetwork.TestOrderBroadcastResult result =
                await WorkstationNetwork.SendTestOrderToRecordingDevicesAsync(_host.LanAccessUrl);
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
            SendTestOrderButton.IsEnabled = _host.IsLanAvailable;
            SendTestOrderButtonText.Text = "发送测试订单";
        }
    }

    private async void SwitchWorkstation_Click(object sender, RoutedEventArgs e)
    {
        if (_purposeSwitchPending)
            return;

        var window = new WorkstationSelectionWindow(DeploymentPresets.MobileBackupHost)
        {
            Owner = this
        };
        if (window.ShowDialog() != true || string.IsNullOrWhiteSpace(window.SelectedPreset))
            return;

        if (string.Equals(
                DeploymentPresets.MobileBackupHost,
                window.SelectedPreset,
                StringComparison.OrdinalIgnoreCase))
        {
            ShowToast("当前已经是录像文件备份主机用途");
            return;
        }

        await RunPurposeSwitchAsync(() =>
        {
            if (!WorkstationConfigStore.TryUpdate(
                    config =>
                    {
                        config.DeploymentPreset = window.SelectedPreset;
                        if (window.SelectedPreset == DeploymentPresets.RecordingWorkstation)
                        {
                            config.RecordingWorkstationActivatedAtUtc = DateTime.UtcNow;
                            RecordingWorkstationCachePolicy.ConfigureInitialLocation(
                                config,
                                preserveExistingLocation: true);
                        }
                        config.WorkstationRole = DeploymentCapabilities
                            .ForPreset(window.SelectedPreset)
                            .IsRecordingDevice
                                ? WorkstationRoles.CameraMonitor
                                : "";
                        config.EnableWebServer = DeploymentCapabilities
                            .ForPreset(window.SelectedPreset)
                            .CanRunWebServer;
                    },
                    out AppConfig savedConfig,
                    out string error))
            {
                AppDialog.Error(
                    this,
                    $"用途保存失败：{error}",
                    "切换用途");
                return false;
            }

            _config.DeploymentPreset = savedConfig.DeploymentPreset;
            _config.WorkstationRole = savedConfig.WorkstationRole;
            _config.EnableWebServer = savedConfig.EnableWebServer;
            return true;
        });
    }

    private async Task<bool> RunPurposeSwitchAsync(Func<bool> savePurpose)
    {
        if (_purposeSwitchPending)
            return false;

        _purposeSwitchPending = true;
        SwitchPurposeButton.IsEnabled = false;
        SwitchPurposeButtonText.Text = "正在切换";
        try
        {
            if (_host.HasActiveMobileBackups)
            {
                SwitchPurposeButtonText.Text = "等待备份完成";
                ShowToast("设备录像正在备份，完成后将自动重启", ToastSeverity.Warning);
            }

            await _host.WaitForMobileBackupsAsync(_lifetimeCts.Token);
            _lifetimeCts.Token.ThrowIfCancellationRequested();
            if (!savePurpose())
                return false;

            return WorkstationNetwork.RestartAfterPurposeChange(this);
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
                SwitchPurposeButton.IsEnabled = true;
                SwitchPurposeButtonText.Text = "切换用途";
            }
        }
    }

    private void CompleteDeploymentSetup()
    {
        if (_deploymentSetupPersisted)
            return;

        if (!WorkstationConfigStore.TryUpdate(
                config =>
                {
                    config.DeploymentPreset = DeploymentPresets.MobileBackupHost;
                    config.DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion;
                    config.WorkstationRole = WorkstationRoles.PrintStation;
                    config.EnableWebServer = true;
                    AppConfig.MarkDeploymentSetupCompleted(config);
                },
                out AppConfig savedConfig,
                out _))
        {
            return;
        }

        _config.DeploymentPreset = savedConfig.DeploymentPreset;
        _config.DeploymentSchemaVersion = savedConfig.DeploymentSchemaVersion;
        _config.WorkstationRole = savedConfig.WorkstationRole;
        _config.EnableWebServer = savedConfig.EnableWebServer;
        _config.FirstUseWizardCompleted = savedConfig.FirstUseWizardCompleted;
        _config.DeploymentSetupVersion = savedConfig.DeploymentSetupVersion;
        _config.RecordingSetupVersion = savedConfig.RecordingSetupVersion;
        _deploymentSetupPersisted = true;
    }
}
