using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;
using System.Text.RegularExpressions;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class DeploymentStartupTests
{
    [Fact]
    public void UserscriptGuideNavigationAlwaysBuildsHostedGuideUrl()
    {
        string url = UserscriptGuideNavigation.BuildUrl("http://192.168.1.20:5280/");

        Assert.StartsWith(
            "http://192.168.1.20:5280/kuaidizs-install-guide?refresh=",
            url,
            StringComparison.Ordinal);
        Assert.DoesNotContain("/?refresh=", url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://192.168.1.20:5280")]
    [InlineData("http://192.168.1.20:5280/")]
    [InlineData("http://192.168.1.20:5280/?key=secret")]
    [InlineData("http://192.168.1.20:5280/videos?page=2")]
    [InlineData("http://192.168.1.20:5280/kuaidizs-install-guide?key=secret")]
    public void UserscriptGuideNavigationRebuildsPathWithoutPlaybackQuery(string hostAddress)
    {
        string url = UserscriptGuideNavigation.BuildUrl(hostAddress);
        var uri = new Uri(url);

        Assert.Equal("http://192.168.1.20:5280", uri.GetLeftPart(UriPartial.Authority));
        Assert.Equal("/kuaidizs-install-guide", uri.AbsolutePath);
        Assert.StartsWith("?refresh=", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("key=", uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("videos", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MobileConnectionSecurityNoticeUsesWarningTextWithoutCard()
    {
        string xaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "MobileConnectionWindow.xaml");
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "MobileConnectionWindow.xaml.cs");

        Assert.Contains("<TextBlock x:Name=\"SecurityNotice\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{DynamicResource AccentOrange}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Border x:Name=\"SecurityNotice\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"QrCodeImage\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MobileAppQrCodeImage\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TestFlightQrCodeImage\"", xaml, StringComparison.Ordinal);
        Assert.Contains("StaticResource FluentPhoneIcon", xaml, StringComparison.Ordinal);
        Assert.Contains("StaticResource AndroidIcon", xaml, StringComparison.Ordinal);
        Assert.Contains("StaticResource AppleIcon", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("StaticResource FluentLinkIcon", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"CloseButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OpenSettingsButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RepairLanButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"修复局域网\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource PrimaryButtonStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"RepairLan_Click\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"手机/电脑连接\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("连接录像网页，或先下载 PackingProof 手机录像 App", xaml, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(xaml, "<ColumnDefinition Width=\"\\*\"/>").Count);
        Assert.Equal(3, Regex.Matches(xaml, "Width=\"232\"[\\s\\S]*?Height=\"232\"").Count);
        Assert.Equal(6, Regex.Matches(xaml, "Stretch=\"Uniform\"").Count);
        Assert.Contains("Grid.IsSharedSizeScope=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(xaml, "SharedSizeGroup=\"ConnectionCardQr\"").Count);
        Assert.Equal(3, Regex.Matches(xaml, "SharedSizeGroup=\"ConnectionCardActions\"").Count);
        Assert.Contains("Text=\"下载手机 App\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"iOS 内测\"", xaml, StringComparison.Ordinal);
        Assert.Contains("使用 Android 手机扫码", xaml, StringComparison.Ordinal);
        Assert.Contains("下载完成后按手机提示安装", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OpenMobileAppDownloadButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OpenTestFlight_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CopyMobileAppUrlButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CopyTestFlightUrlButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"CopyMobileAppUrl_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"CopyTestFlightUrl_Click\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"电脑打开下载页\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"电脑打开 TestFlight 加入页\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseButton", source, StringComparison.Ordinal);
        Assert.Contains("Key.Escape", source, StringComparison.Ordinal);
        Assert.Contains("RepairLanButton.IsEnabled = false", source, StringComparison.Ordinal);
        Assert.Contains("RepairLanButton.Content = \"正在修复…\"", source, StringComparison.Ordinal);
        Assert.Contains("RepairLanButton.Content = \"重新修复\"", source, StringComparison.Ordinal);
        Assert.Contains("ApplyConnectionState(", source, StringComparison.Ordinal);
        Assert.Contains("CopyMobileAppUrl_Click", source, StringComparison.Ordinal);
        Assert.Contains("CopyTestFlightUrl_Click", source, StringComparison.Ordinal);
        Assert.Contains(
            "UpdateMobileAppDownload(MobileAppUpdatePolicyProvider.Shared.LatestRelease);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "await MobileAppUpdatePolicyProvider.Shared.CheckLatestAsync();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "MobileAppUpdatePolicyProvider.ReleasesUrl",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "MobileAppUpdatePolicyProvider.TestFlightJoinUrl",
            source,
            StringComparison.Ordinal);
        string iconResources = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Themes",
            "FluentIcons.xaml");
        Assert.Contains("x:Key=\"AppleIcon\"", iconResources, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"AndroidIcon\"", iconResources, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RecordingHost", DeploymentPresets.RecordingHost)]
    [InlineData("CameraMonitor", DeploymentPresets.RecordingHost)]
    [InlineData("monitor", DeploymentPresets.RecordingHost)]
    [InlineData("MobileBackupHost", DeploymentPresets.MobileBackupHost)]
    [InlineData("PrintStation", DeploymentPresets.MobileBackupHost)]
    [InlineData("order", DeploymentPresets.MobileBackupHost)]
    [InlineData("ViewerClient", DeploymentPresets.ViewerClient)]
    [InlineData("viewer", DeploymentPresets.ViewerClient)]
    public void DeploymentCommandNamesMapToCurrentPresets(string input, string expected)
    {
        Assert.Equal(expected, App.NormalizePresetName(input));
    }

    [Theory]
    [InlineData("--monitor", DeploymentPresets.RecordingHost)]
    [InlineData("--print-station", DeploymentPresets.MobileBackupHost)]
    [InlineData("--order-workstation", DeploymentPresets.MobileBackupHost)]
    [InlineData("--viewer", DeploymentPresets.ViewerClient)]
    public void LegacyCommandLineFlagsMapToCurrentPresets(string flag, string expected)
    {
        Assert.Equal(expected, App.ResolveLegacyRequestedPreset([flag]));
    }

    [Fact]
    public void SingleInstanceCoordinatorIsGlobalAcrossDeploymentPresets()
    {
        string? previousScope = Environment.GetEnvironmentVariable("EPM_INSTANCE_SCOPE");
        Environment.SetEnvironmentVariable("EPM_INSTANCE_SCOPE", $"deployment{Guid.NewGuid():N}");
        try
        {
            Assert.True(WorkstationInstanceCoordinator.TryCreate(out WorkstationInstanceCoordinator? first));
            using (first)
            {
                Assert.True(WorkstationInstanceCoordinator.IsRunning());
                Assert.False(WorkstationInstanceCoordinator.TryCreate(out WorkstationInstanceCoordinator? second));
                Assert.Null(second);
            }
            Assert.False(WorkstationInstanceCoordinator.IsRunning());
        }
        finally
        {
            Environment.SetEnvironmentVariable("EPM_INSTANCE_SCOPE", previousScope);
        }
    }

    [Fact]
    public void ViewerClientWindowDoesNotReferenceLocalRecordingOrHostServices()
    {
        string source = ReadRepositoryFile("ExpressPackingMonitoring", "Workstations", "ViewerClientWindow.xaml.cs");

        Assert.DoesNotContain("VideoDatabase", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new WebServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NoCameraWorkstationHost", source, StringComparison.Ordinal);
        Assert.DoesNotContain("VideoCaptureDevice", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AudioProbe", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GlobalKeyboardHook", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CameraIdleWatchdogUsesTrackedCancelableTask()
    {
        string cameraSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Camera.cs");
        string mediaSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Media.cs");
        string mainSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.cs");

        Assert.Contains(
            "private async Task CameraIdleWatchdogAsync(CancellationToken cancellationToken)",
            cameraSource,
            StringComparison.Ordinal);
        Assert.Contains("Task.Delay(10_000, cancellationToken)", cameraSource, StringComparison.Ordinal);
        Assert.Contains("_cameraIdleWatchdogTask = Task.Run(", mediaSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run(CameraIdleWatchdog)", mediaSource, StringComparison.Ordinal);
        Assert.Contains("if (_isDisposed", cameraSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_cameraIdleWatchdogTask?.Wait", mainSource, StringComparison.Ordinal);
    }

    [Fact]
    public void EncoderDetectionCommandUsesTaskReturningHandler()
    {
        string encoderSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Encoder.cs");
        string scannerSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Scanner.cs");

        Assert.Contains("public async Task ResetEncoderDetectAsync()", encoderSource, StringComparison.Ordinal);
        Assert.DoesNotContain("async void ResetEncoderDetect", encoderSource, StringComparison.Ordinal);
        Assert.Contains(
            "ResetEncoderDetectCommand = new AsyncRelayCommand(ResetEncoderDetectAsync);",
            scannerSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CameraReconnectUsesTaskReturningEntryPoint()
    {
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Camera.cs");

        Assert.Contains(
            "private async Task RestartCameraWithRecordingStopAsync(string trigger)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("async void RestartCameraWithRecordingStop", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeLog.Error(\"Camera\", $\"Camera restart failed", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingToggleUsesAsyncCommandAndAwaitedScanPath()
    {
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Scanner.cs");

        Assert.Contains("private async Task ToggleRecordingAsync()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("async void ToggleRecording", source, StringComparison.Ordinal);
        Assert.Contains(
            "ToggleRecordingCommand = new AsyncRelayCommand(ToggleRecordingAsync);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("await ToggleRecordingAsync();", source, StringComparison.Ordinal);
        Assert.Contains(
            "RuntimeLog.Error(\"Recording\", \"Manual recording toggle failed\", ex);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ScanEntryPointsUseTaskReturningExceptionBoundary()
    {
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Scanner.cs");

        Assert.Contains(
            "ScanCommand = new AsyncRelayCommand<string>(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("AsyncRelayCommandOptions.AllowConcurrentExecutions", source, StringComparison.Ordinal);
        Assert.Contains(
            "private async Task HandleScanAsync(string scanResult, bool fromCamera = false)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private async Task HandleScanCoreAsync(string scanResult, bool fromCamera)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("async void HandleScan", source, StringComparison.Ordinal);
        Assert.Contains("Unhandled scan failure source=", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerClientUsesSingleDynamicUserscriptButton()
    {
        string xaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml");

        Assert.Equal(1, xaml.Split("Click=\"InstallUserscript_Click\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("x:Name=\"UserscriptButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UserscriptStatusText\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("重新生成快递助手脚本", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingWorkstationCanScanHostQrWithoutOwningCamera()
    {
        string xaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml");
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml.cs");

        Assert.Contains("x:Name=\"ScanPhonePairingButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"扫描保存主机二维码\"", xaml, StringComparison.Ordinal);
        Assert.Contains("StaticResource FluentCameraIcon", xaml, StringComparison.Ordinal);
        Assert.Contains("Owner?.DataContext is not MainViewModel", source, StringComparison.Ordinal);
        Assert.Contains("viewModel.ScanHostPairingQrAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("VideoCaptureDevice", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyTemporaryPairingLinkOnlyProvidesHostAddress()
    {
        const string link =
            "http://192.168.1.8:5280/?pairToken=aBcD0123456789ef&pairSecret=AbCd0123456789abcdef0123456789abcdef";

        WorkstationNetwork.ParseHostConnectionInput(link, out string address, out string accessKey);

        Assert.Equal("192.168.1.8:5280", address);
        Assert.Empty(accessKey);
    }

    [Fact]
    public void RecordingWorkstationMigrationClearsOnlyLegacyConnectionCredential()
    {
        string nodeId = Guid.NewGuid().ToString("D");
        var config = new AppConfig
        {
            DeploymentPreset = DeploymentPresets.RecordingWorkstation,
            DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion,
            NodeId = Guid.NewGuid().ToString("D"),
            NodeName = "电脑2",
            LastKnownHostNodeId = nodeId,
            LastKnownHostNodeName = "原保存主机",
            LastKnownHostAddress = "http://192.168.1.20:5280",
            LastKnownHostAccessKey = "legacy-web-derived-key",
            LastKnownHostBackupAuthVersion = 2,
            BackupConnectionSchemaVersion = 0,
            RecordingCacheMaxGB = 88,
            RecordingWorkstationActivatedAtUtc = DateTime.UtcNow.AddDays(-2)
        };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        Assert.Equal(nodeId, config.LastKnownHostNodeId);
        Assert.Equal("原保存主机", config.LastKnownHostNodeName);
        Assert.Empty(config.LastKnownHostAddress);
        Assert.Empty(config.LastKnownHostAccessKey);
        Assert.Equal(0, config.LastKnownHostBackupAuthVersion);
        Assert.Equal(AppConfig.CurrentBackupConnectionSchemaVersion, config.BackupConnectionSchemaVersion);
        Assert.Equal(88, config.RecordingCacheMaxGB);
        Assert.NotNull(config.RecordingWorkstationActivatedAtUtc);
    }

    [Fact]
    public void CurrentRecordingWorkstationTokenIsNotClearedAgain()
    {
        var config = new AppConfig
        {
            DeploymentPreset = DeploymentPresets.RecordingWorkstation,
            DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion,
            NodeId = Guid.NewGuid().ToString("D"),
            LastKnownHostNodeId = Guid.NewGuid().ToString("D"),
            LastKnownHostAddress = "http://192.168.1.20:5280",
            LastKnownHostAccessKey = new string('a', 64),
            LastKnownHostBackupAuthVersion = BackupRequestAuthentication.CurrentVersion,
            BackupConnectionSchemaVersion = AppConfig.CurrentBackupConnectionSchemaVersion
        };

        AppConfig.NormalizeAfterLoad(config);
        Assert.Equal("http://192.168.1.20:5280", config.LastKnownHostAddress);
        Assert.Equal(new string('a', 64), config.LastKnownHostAccessKey);
        Assert.Equal(BackupRequestAuthentication.CurrentVersion, config.LastKnownHostBackupAuthVersion);
    }

    [Fact]
    public void ViewerClientActionButtonsUseFluentIconsAndNamedDynamicLabels()
    {
        string xaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml");
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml.cs");

        foreach (string icon in new[]
        {
            "FluentArrowSwapIcon",
            "FluentSearchIcon",
            "FluentLinkIcon",
            "FluentPlayIcon",
            "FluentBroadcastIcon",
            "FluentWifiIcon",
            "FluentDatabaseIcon"
        })
        {
            Assert.Contains($"StaticResource {icon}", xaml, StringComparison.Ordinal);
        }

        Assert.Contains("x:Name=\"UserscriptButtonText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SendTestOrderButtonText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("UserscriptButtonText.Text = AppLanguage.Get(status.ButtonText)", source, StringComparison.Ordinal);
        Assert.Contains("SendTestOrderButtonText.Text = \"正在发送\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentWindowsUseConsistentIconsForSharedActions()
    {
        string recordingXaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml");
        string viewerXaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml");
        string mobileBackupXaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml");
        string manualConnectionXaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ManualHostConnectionWindow.xaml");

        foreach (string xaml in new[] { recordingXaml, viewerXaml, mobileBackupXaml })
        {
            Assert.Contains("StaticResource FluentArrowSwapIcon", xaml, StringComparison.Ordinal);
            Assert.Contains("StaticResource FluentIntegrationIcon", xaml, StringComparison.Ordinal);
            Assert.Contains("StaticResource FluentBroadcastIcon", xaml, StringComparison.Ordinal);
        }

        Assert.Matches(
            "x:Name=\"BtnMobileConnection\"[\\s\\S]*?StaticResource FluentWifiIcon",
            recordingXaml);
        Assert.Matches(
            "x:Name=\"ConnectPhoneButton\"[\\s\\S]*?StaticResource FluentWifiIcon",
            mobileBackupXaml);
        Assert.Matches(
            "x:Name=\"RecordingHostMobileBackupStatus\"[\\s\\S]*?StaticResource FluentWifiIcon",
            recordingXaml);
        Assert.Matches(
            "x:Name=\"RecordingWorkstationHostCard\"[\\s\\S]*?StaticResource FluentDatabaseIcon",
            recordingXaml);
        Assert.Matches(
            "x:Name=\"HostSummaryBorder\"[\\s\\S]*?StaticResource FluentDatabaseIcon",
            viewerXaml);
        Assert.Matches(
            "x:Name=\"ViewerChangeHostButton\"[\\s\\S]*?StaticResource FluentDatabaseIcon",
            viewerXaml);
        Assert.Matches(
            "x:Name=\"BindingChangeHostButton\"[\\s\\S]*?StaticResource FluentDatabaseIcon",
            viewerXaml);
        Assert.Matches(
            "x:Name=\"BindSelectedButton\"[\\s\\S]*?StaticResource FluentWifiIcon",
            viewerXaml);
        Assert.Matches(
            "x:Name=\"ConnectButton\"[\\s\\S]*?StaticResource FluentWifiIcon",
            manualConnectionXaml);
        Assert.Contains("x:Key=\"FluentDatabaseIcon\"", ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Themes",
            "FluentIcons.xaml"), StringComparison.Ordinal);
        Assert.Matches(
            "x:Name=\"OpenWebButton\"[\\s\\S]*?StaticResource FluentPlayIcon",
            viewerXaml);
        Assert.Matches(
            "x:Name=\"OpenWebButton\"[\\s\\S]*?StaticResource FluentPlayIcon",
            mobileBackupXaml);
        Assert.Matches(
            "x:Name=\"BtnSendTestOrder\"[\\s\\S]*?StaticResource FluentBroadcastIcon",
            recordingXaml);
        Assert.Matches(
            "x:Name=\"SendTestOrderButton\"[\\s\\S]*?StaticResource FluentBroadcastIcon",
            viewerXaml);
        Assert.Matches(
            "x:Name=\"SendTestOrderButton\"[\\s\\S]*?StaticResource FluentBroadcastIcon",
            mobileBackupXaml);

        foreach (string xaml in new[] { recordingXaml, mobileBackupXaml })
        {
            Assert.Contains("StaticResource FluentDataIcon", xaml, StringComparison.Ordinal);
            Assert.Contains("StaticResource FluentVideoIcon", xaml, StringComparison.Ordinal);
            Assert.Contains("StaticResource FluentSettingsIcon", xaml, StringComparison.Ordinal);
        }

        Assert.Contains("Text=\"打开网页回放\"", viewerXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("打开录像网页", viewerXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkstationAddressNormalizationRemovesPlaybackKeyAndPath()
    {
        Assert.Equal(
            "192.168.1.20:5280",
            WorkstationNetwork.NormalizeAddress("http://192.168.1.20:5280/?key=secret"));
        Assert.Equal(
            "192.168.1.20:5280",
            WorkstationNetwork.NormalizeAddress("192.168.1.20:5280/kuaidizs-install-guide"));
    }

    [Fact]
    public void PurposeSwitchUsesDirectRestartAndDisablesRecordingHostButton()
    {
        string viewerXaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml");
        string mobileBackupXaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml");
        string recordingXaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml");
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml.cs");

        Assert.Contains("Text=\"切换用途\"", viewerXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"SwitchPurpose_Click\"", viewerXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"切换用途\"", mobileBackupXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SwitchWorkstationButtonText}\"", recordingXaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanSwitchWorkstation}\"", recordingXaml, StringComparison.Ordinal);
        Assert.Contains("new WorkstationSelectionWindow(DeploymentPresets.ViewerClient)", source, StringComparison.Ordinal);
        Assert.Contains("WorkstationNetwork.RestartAfterPurposeChange(this)", source, StringComparison.Ordinal);

        string launcher = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "WorkstationLauncher.cs");
        Assert.DoesNotContain("AskRestart(", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("AppDialog.Confirm(", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void HostPurposeSwitchWaitsBeforeSavingAndCanBeCancelled()
    {
        string mobileSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml.cs");
        string recordingSource = RepositorySource.ReadMainViewModel();
        string mobileSwitch = mobileSource[mobileSource.IndexOf(
            "private async Task<bool> RunPurposeSwitchAsync",
            StringComparison.Ordinal)..];
        string recordingSwitch = recordingSource[recordingSource.IndexOf(
            "private async Task<bool> RunPurposeSwitchAsync",
            StringComparison.Ordinal)..];

        int mobileWait = mobileSwitch.IndexOf("await _host.WaitForMobileBackupsAsync", StringComparison.Ordinal);
        int mobileSave = mobileSwitch.IndexOf("if (!savePurpose())", StringComparison.Ordinal);
        Assert.True(mobileWait >= 0 && mobileWait < mobileSave);
        Assert.Contains("_lifetimeCts.Token", mobileSwitch, StringComparison.Ordinal);
        int recordingWait = recordingSwitch.IndexOf("await _webServer.WaitForMobileBackupsAsync", StringComparison.Ordinal);
        int recordingSave = recordingSwitch.IndexOf("if (!SaveConfig(nextConfig", StringComparison.Ordinal);
        Assert.True(recordingWait >= 0 && recordingWait < recordingSave);
        Assert.Contains("while (IsRecording)", recordingSwitch, StringComparison.Ordinal);
        Assert.Contains("_purposeSwitchCts.Cancel()", recordingSource, StringComparison.Ordinal);
    }

    [Fact]
    public void StorageHostPurposeUsesStorageIcon()
    {
        string selectorSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "WorkstationSelectionWindow.xaml.cs");
        string settings = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml");
        string icons = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Themes",
            "FluentIcons.xaml");

        Assert.Contains("\"FluentDatabaseIcon\"", selectorSource, StringComparison.Ordinal);
        Assert.Contains("Data=\"{StaticResource FluentDatabaseIcon}\"", settings, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"FluentDatabaseIcon\"", icons, StringComparison.Ordinal);
    }

    [Fact]
    public void PurposeSelectorUsesOnePageTwoQuestionsAndRequiresConfirmation()
    {
        string xaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "WorkstationSelectionWindow.xaml");
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "WorkstationSelectionWindow.xaml.cs");

        Assert.Contains("1. 这台电脑要用来录像吗？", xaml, StringComparison.Ordinal);
        Assert.Contains("2. 这台电脑要负责长期保存录像吗？", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"RecordingStep\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"StorageStep\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ConfirmPurposeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"确认用途\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ResultCapabilitiesList", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Matches(
            "x:Name=\"RecordNoChoice\"[\\s\\S]*?Tag=\"\\{StaticResource FluentDismissIcon\\}\"",
            xaml);
        Assert.Matches(
            "x:Name=\"StorageNoChoice\"[\\s\\S]*?Tag=\"\\{StaticResource FluentDismissIcon\\}\"",
            xaml);
        Assert.Contains("RestoreCurrentAnswers();", source, StringComparison.Ordinal);
        Assert.Contains("DialogResult = false;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPurposeListContainsAllFourUnifiedRolesWithoutRedundantRestartHint()
    {
        string settings = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml");

        foreach ((string tag, string text) in new[]
        {
            ("RecordingHost", "电脑录像并保存在本机"),
            ("RecordingWorkstation", "电脑录像并保存到其他电脑"),
            ("MobileBackupHost", "录像文件备份主机"),
            ("ViewerClient", "只连接主机查看")
        })
        {
            Assert.Contains($"Tag=\"{tag}\"", settings, StringComparison.Ordinal);
            Assert.Contains($"Text=\"{text}\"", settings, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            "更改用途后程序会自动重启，并切换到对应界面",
            settings,
            StringComparison.Ordinal);
        Assert.Matches(
            "Tag=\"RecordingHost\"[\\s\\S]*?FluentVideoIcon",
            settings);
        Assert.Matches(
            "Tag=\"RecordingWorkstation\"[\\s\\S]*?FluentWifiIcon",
            settings);
        Assert.Matches(
            "Tag=\"ViewerClient\"[\\s\\S]*?FluentPlayIcon",
            settings);
        Assert.Matches(
            "Tag=\"MobileBackupHost\"[\\s\\S]*?FluentDatabaseIcon",
            settings);
    }

    [Theory]
    [InlineData(DeploymentPresets.RecordingHost, true, false, "连接手机/电脑")]
    [InlineData(DeploymentPresets.RecordingWorkstation, true, true, "管理保存主机")]
    [InlineData(DeploymentPresets.ViewerClient, false, false, "连接保存主机")]
    [InlineData(DeploymentPresets.MobileBackupHost, false, false, "连接手机/电脑")]
    public void MainConnectionEntryIsIsolatedByDeploymentPreset(
        string preset,
        bool expectedVisible,
        bool expectedHostManagement,
        string expectedText)
    {
        var config = new AppConfig { DeploymentPreset = preset };

        Assert.Equal(expectedVisible, MainViewModel.ShouldShowMainConnection(config));
        Assert.Equal(
            expectedHostManagement,
            MainViewModel.ShouldManageBoundHostFromMainConnection(config));
        Assert.Equal(expectedText, MainViewModel.GetMainConnectionButtonText(config));
    }

    [Fact]
    public void RecordingWorkstationMainConnectionReusesSecureHostBindingFlow()
    {
        string xaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml");
        string windowSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml.cs");
        string transferSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Transfer.cs");

        Assert.Contains(
            "Text=\"{Binding MainConnectionButtonText, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding IsMainConnectionVisible, Mode=OneWay, Converter={StaticResource BoolToVisibility}}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ToolTip=\"{Binding MainConnectionButtonToolTip, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Matches(
            "x:Name=\"BtnMobileConnection\"[\\s\\S]*?FluentWifiIcon[\\s\\S]*?FluentDatabaseIcon",
            xaml);
        Assert.Contains("viewModel.ShowMainConnection(this)", windowSource, StringComparison.Ordinal);
        Assert.Contains("ChangeBoundHost(owner);", transferSource, StringComparison.Ordinal);
        Assert.Contains("ShowMobileConnection(owner);", transferSource, StringComparison.Ordinal);
        Assert.Contains(
            "_recordingTransferService?.EnqueueCompletedRecordings();",
            transferSource,
            StringComparison.Ordinal);
        Assert.Matches(
            "new ViewerClientWindow\\(\\s*Config,\\s*DeploymentPresets\\.RecordingWorkstation\\)",
            transferSource);
    }

    [Fact]
    public void PostStopMuxImmediatelyQueuesCompletedRecordingsForTransfer()
    {
        string source = RepositorySource.ReadMainViewModel();
        int methodStart = source.IndexOf("private void QueuePostStopMux", StringComparison.Ordinal);

        Assert.True(methodStart >= 0);
        int methodEnd = source.IndexOf(
            "private async Task SafeStopRecordingAsync",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodEnd > methodStart);
        string method = source[methodStart..methodEnd];
        int conversion = method.IndexOf("await BatchConvertMkvToMp4Async", StringComparison.Ordinal);
        int enqueue = method.IndexOf(
            "_recordingTransferService?.EnqueueCompletedRecordings();",
            StringComparison.Ordinal);

        Assert.True(conversion >= 0);
        Assert.True(enqueue > conversion);
    }

    [Fact]
    public void PcRecordingStatusUsesNicknameRoleCardsAndSharedOrderCard()
    {
        string mainWindow = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml");
        string transferSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Transfer.cs");
        string mainSource = RepositorySource.ReadMainViewModel();
        string statusCard = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "Controls",
            "StatusCard.xaml");

        Assert.Contains(
            "x:Name=\"RecordingWorkstationCompactStatus\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"RecordingWorkstationHostCard\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"RecordingWorkstationUploadCard\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"ComputerNicknameHeading\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"RecordingHostMobileBackupStatus\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"ReceivedOrdersCard\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            mainWindow.Split(
                "x:Name=\"RecordingWorkstationHostCard\"",
                StringSplitOptions.None).Length - 1);
        Assert.Equal(
            1,
            mainWindow.Split(
                "x:Name=\"RecordingWorkstationUploadCard\"",
                StringSplitOptions.None).Length - 1);
        Assert.Equal(
            1,
            mainWindow.Split(
                "x:Name=\"ReceivedOrdersCard\"",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "Text=\"{Binding ComputerDisplayName, Mode=OneWay}\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "DetailText=\"{Binding BoundHostNameDisplay, Mode=OneWay}\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShortStatusText=\"{Binding BoundHostOnlineStatusText, Mode=OneWay}\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShortStatusText=\"{Binding RecordingTransferShortStatusText, Mode=OneWay}\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<Run Text=\"{Binding PendingRecordingTransferCount, Mode=OneWay}\"/>",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "DetailText=\"{Binding RecordingTransferStatusText, Mode=OneWay}\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Binding=\"{Binding IsRecordingWorkstation, Mode=OneWay}\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RecordingCacheUsagePercent",
            mainWindow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RecordingCacheUsageText",
            mainWindow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RecordingCacheStatusText",
            mainWindow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IsRecordingCacheWarning",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "? \"尚未绑定保存主机\"",
            transferSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("当前用途：录制工位", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding RetryRecordingTransfersCommand", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding ChangeBoundHostCommand", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding OpenBoundHostCommand", mainWindow, StringComparison.Ordinal);
        Assert.Contains(
            "DetailText=\"{Binding OrderIntegrationStatusText, Mode=OneWay}\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShortStatusText=\"{Binding UserscriptSetupShortStatusText, Mode=OneWay}\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "ToolTip=\"{Binding UserscriptSetupStatusText, Mode=OneWay}\"",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "DeviceItemsSource=\"{Binding MobileBackupDeviceStatuses, Mode=OneWay}\"",
            mainWindow,
            StringComparison.Ordinal);
        int backupCard = mainWindow.IndexOf(
            "x:Name=\"RecordingHostMobileBackupStatus\"",
            StringComparison.Ordinal);
        int backupCardEnd = mainWindow.IndexOf(
            "x:Name=\"RecordingWorkstationCompactStatus\"",
            backupCard,
            StringComparison.Ordinal);
        string backupCardMarkup = mainWindow[backupCard..backupCardEnd];
        Assert.Matches(
            "ShortStatusText=\"\\{Binding WorkstationPrintStatusText, Mode=OneWay\\}\"",
            backupCardMarkup);
        Assert.True(
            backupCardMarkup.IndexOf(
                "ShortStatusText=\"{Binding WorkstationPrintStatusText, Mode=OneWay}\"",
                StringComparison.Ordinal)
            < backupCardMarkup.IndexOf(
                "DeviceItemsSource=\"{Binding MobileBackupDeviceStatuses, Mode=OneWay}\"",
                StringComparison.Ordinal));
        Assert.Contains("Value=\"已就绪\"", statusCard, StringComparison.Ordinal);
        Assert.Contains("Value=\"启动中\"", statusCard, StringComparison.Ordinal);
        Assert.Contains("Value=\"启动失败\"", statusCard, StringComparison.Ordinal);
        Assert.Contains("_workstationPrintStatusText = \"未连接\"", mainSource, StringComparison.Ordinal);
        Assert.Contains(": \"启动中\";", mainSource, StringComparison.Ordinal);
        Assert.Contains(": \"启动失败\";", mainSource, StringComparison.Ordinal);
        Assert.Contains("WorkstationPrintStatusText = \"已就绪\"", mainSource, StringComparison.Ordinal);
        Assert.DoesNotContain("设备备份服务：", mainSource, StringComparison.Ordinal);
        Assert.Contains("_orderIntegrationStatusText = \"暂未收到订单\"", mainSource, StringComparison.Ordinal);
        Assert.Contains("_userscriptSetupStatusText = \"未配置订单联动\"", mainSource, StringComparison.Ordinal);
        Assert.Contains("_boundHostOnlineStatusText = \"检查中\"", transferSource, StringComparison.Ordinal);
        Assert.Contains("heartbeat.Online ? \"在线\" : \"离线\"", transferSource, StringComparison.Ordinal);
        Assert.Contains("recentlyUploaded ? \"最近录像已上传\" : \"暂无待上传录像\"", transferSource, StringComparison.Ordinal);
        Assert.Contains(
            "$\"{totalCount} 个录像已保存在本机，联网后自动上传\"",
            transferSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "<DataTrigger Binding=\"{Binding IsRecordingWorkstation, Mode=OneWay}\"",
            mainWindow,
            StringComparison.Ordinal);
        int nicknameHeading = mainWindow.IndexOf(
            "x:Name=\"ComputerNicknameHeading\"",
            StringComparison.Ordinal);
        int compactStatus = mainWindow.IndexOf(
            "x:Name=\"RecordingWorkstationCompactStatus\"",
            StringComparison.Ordinal);
        int hostCard = mainWindow.IndexOf(
            "x:Name=\"RecordingWorkstationHostCard\"",
            compactStatus,
            StringComparison.Ordinal);
        int uploadCard = mainWindow.IndexOf(
            "x:Name=\"RecordingWorkstationUploadCard\"",
            hostCard,
            StringComparison.Ordinal);
        int orderCard = mainWindow.IndexOf(
            "x:Name=\"ReceivedOrdersCard\"",
            uploadCard,
            StringComparison.Ordinal);
        int existingButtons = mainWindow.IndexOf(
            "x:Name=\"BtnSwitchWorkstation\"",
            orderCard,
            StringComparison.Ordinal);
        Assert.True(nicknameHeading >= 0);
        Assert.True(compactStatus > nicknameHeading);
        Assert.True(hostCard > compactStatus);
        Assert.True(uploadCard > hostCard);
        Assert.True(orderCard > uploadCard);
        Assert.True(existingButtons > orderCard);
        Assert.Contains(
            "Text=\"{Binding ComputerIpAddress, Mode=OneWay}\"",
            mainWindow,
            StringComparison.Ordinal);
        string nicknameMarkup = mainWindow[nicknameHeading..compactStatus];
        Assert.Contains("BorderThickness=\"0,1,0,0\"", nicknameMarkup, StringComparison.Ordinal);
        Assert.Contains("FontSize=\"18\"", nicknameMarkup, StringComparison.Ordinal);
        Assert.Contains("FontWeight=\"Black\"", nicknameMarkup, StringComparison.Ordinal);
        Assert.Contains("<ColumnDefinition Width=\"Auto\"/>", nicknameMarkup, StringComparison.Ordinal);
        Assert.Contains("CardIcon=\"{StaticResource FluentPrinterIcon}\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("CardTitle=\"手机/电脑备份\"", mainWindow, StringComparison.Ordinal);
        string orderMarkup = mainWindow[orderCard..existingButtons];
        string hostMarkup = mainWindow[hostCard..uploadCard];
        string uploadMarkup = mainWindow[uploadCard..orderCard];
        Assert.Contains("Value=\"检查中\"", statusCard, StringComparison.Ordinal);
        Assert.Contains("Value=\"在线\"", statusCard, StringComparison.Ordinal);
        Assert.Contains("Value=\"离线\"", statusCard, StringComparison.Ordinal);
        Assert.Contains("DynamicResource AccentBlue", statusCard, StringComparison.Ordinal);
        Assert.Contains("DynamicResource AccentGreen", statusCard, StringComparison.Ordinal);
        Assert.Contains("DynamicResource AccentOrange", statusCard, StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding IsOnline, Mode=OneWay, Converter={StaticResource BoolToVisibility}}\"",
            statusCard,
            StringComparison.Ordinal);
        // 圆点必须带显式尺寸，否则在真实主窗口渲染上下文中会塌缩成不可见。
        Assert.Contains(
            "<Ellipse Width=\"7\" Height=\"7\" Fill=\"{DynamicResource TextMuted}\"/>",
            statusCard,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Ellipse Width=\"7\" Height=\"7\" Fill=\"{DynamicResource AccentGreen}\"",
            statusCard,
            StringComparison.Ordinal);
        Assert.Contains("Binding DisplayText, Mode=OneWay", statusCard, StringComparison.Ordinal);
        Assert.Contains("RecordingTransferShortStatusText, Mode=OneWay", uploadMarkup, StringComparison.Ordinal);
        Assert.Contains("LastRecordingTransferError, Mode=OneWay", uploadMarkup, StringComparison.Ordinal);
        Assert.Contains("Value=\"上传中\"", statusCard, StringComparison.Ordinal);
        Assert.Contains("Value=\"已完成\"", statusCard, StringComparison.Ordinal);
        Assert.Contains("Value=\"待上传\"", statusCard, StringComparison.Ordinal);
        Assert.Contains("Value=\"上传失败\"", statusCard, StringComparison.Ordinal);
        Assert.DoesNotContain("PendingRecordingTransferCount, Mode=OneWay", uploadMarkup, StringComparison.Ordinal);
        Assert.Contains("FontSize=\"11\"", statusCard, StringComparison.Ordinal);
        Assert.Contains("FontSize=\"12\"", statusCard, StringComparison.Ordinal);
        Assert.Contains("CardTitle=\"订单联动\"", orderMarkup, StringComparison.Ordinal);
        Assert.Contains("Value=\"已就绪\"", statusCard, StringComparison.Ordinal);
        Assert.Contains("Value=\"需更新\"", statusCard, StringComparison.Ordinal);
        Match orderRows = Regex.Match(
            statusCard,
            "<Grid.RowDefinitions>(?<rows>[\\s\\S]*?)</Grid.RowDefinitions>");
        Assert.True(orderRows.Success);
        Assert.Equal(
            2,
            Regex.Matches(orderRows.Groups["rows"].Value, "<RowDefinition Height=\"Auto\"/>").Count);
        Assert.DoesNotMatch(
            "\\{Binding (ComputerDisplayName|ComputerIpAddress|WorkstationPrintStatusText|WorkstationStatusToolTip|MobileBackupDeviceStatuses|IsOnline|DisplayText|OrderIntegrationStatusText|UserscriptSetupStatusText|UserscriptSetupShortStatusText|UserscriptButtonText|BoundHostNameDisplay|BoundHostOnlineStatusText|PendingRecordingTransferCount|RecordingTransferShortStatusText|RecordingTransferStatusText|LastRecordingTransferError|IsRecordingWorkstation)(?![^}]*Mode=OneWay)[^}]*\\}",
            mainWindow);
    }

    [Fact]
    public void NonHostRolesUseSimplifiedHostDiscoveryWithRoleIsolation()
    {
        string xaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml");
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml.cs");
        string manualWindow = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ManualHostConnectionWindow.xaml");
        string manualSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ManualHostConnectionWindow.xaml.cs");

        Assert.Contains("x:Name=\"ViewerActionPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BindingBoundActionsPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"HostItemTemplate\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SearchHostsButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BindSelectedButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ManualConnectionButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"选择保存主机\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"连接保存主机\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"手动连接\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"在线\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding NodeName, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding Address, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"更换保存主机\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"稍后设置\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"ViewerManualAddressTextBox\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"BindingManualAddressTextBox\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"ManualAddressExpander\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("选择并连接主机", xaml, StringComparison.Ordinal);
        Assert.Equal(
            2,
            xaml.Split("Text=\"更换保存主机\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            1,
            xaml.Split("Click=\"SearchHosts_Click\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            1,
            xaml.Split("Click=\"BindSelected_Click\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            1,
            xaml.Split("Click=\"OpenManualConnection_Click\"", StringSplitOptions.None).Length - 1);

        Assert.Contains("WindowHeadingText.Text = \"保存主机\";", source, StringComparison.Ordinal);
        Assert.Contains(
            "WindowDescriptionText.Text = \"选择一台电脑保存录像；暂时不设置也可以先录像\";",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "DeferBindingButton.Visibility = Visibility.Visible;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "FilterDiscoveredHosts(hosts, _bindingOnly)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (compatibleHosts.Count == 1)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "HostsList.SelectedIndex = 0",
            source,
            StringComparison.Ordinal);
        int autoSelection = source.IndexOf(
            "if (compatibleHosts.Count == 1)",
            StringComparison.Ordinal);
        int resultMessage = source.IndexOf(
            "string message = compatibleHosts.Count switch",
            autoSelection,
            StringComparison.Ordinal);
        Assert.True(autoSelection >= 0 && resultMessage > autoSelection);
        Assert.DoesNotContain(
            "BindHostAsync",
            source[autoSelection..resultMessage],
            StringComparison.Ordinal);
        Assert.Contains(
            "OpenManualConnectionWindow(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (_boundHost == null && (!_bindingOnly || !HasSavedHost()))",
            source,
            StringComparison.Ordinal);
        Assert.Contains("AppDialog.Confirm(", source, StringComparison.Ordinal);
        Assert.Contains("EnrollBackupDeviceAsync", source, StringComparison.Ordinal);
        Assert.Contains("DeviceToken", source, StringComparison.Ordinal);
        Assert.Contains("automatic != null", source, StringComparison.Ordinal);
        Assert.Contains("preferred ?? (compatibleHosts.Count == 1", source, StringComparison.Ordinal);
        Assert.Contains("等待保存主机允许连接", source, StringComparison.Ordinal);
        Assert.Contains("PendingRecordingTransferCount > 0", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(searchCancellation, _searchCancellation)", source, StringComparison.Ordinal);
        Assert.Contains("_isChoosingHost = true;", source, StringComparison.Ordinal);
        Assert.Contains("if (!_isChoosingHost)", source, StringComparison.Ordinal);

        Assert.Contains("x:Name=\"OpenWebButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UserscriptButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SendTestOrderButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SwitchPurposeButton\"", xaml, StringComparison.Ordinal);

        Assert.Contains("x:Name=\"ConnectionInputTextBox\"", manualWindow, StringComparison.Ordinal);
        Assert.Contains("Text=\"主机地址或连接链接\"", manualWindow, StringComparison.Ordinal);
        Assert.Contains("IsDefault=\"True\"", manualWindow, StringComparison.Ordinal);
        Assert.Contains("IsCancel=\"True\"", manualWindow, StringComparison.Ordinal);
        Assert.Contains("ConnectionSubmitted?.Invoke(input)", manualSource, StringComparison.Ordinal);
        Assert.Contains("SetBusy(true)", manualSource, StringComparison.Ordinal);
        Assert.Contains("if (_manualConnectionWindow != null)", source, StringComparison.Ordinal);
        Assert.Contains("window.ConnectionSubmitted -=", source, StringComparison.Ordinal);
        Assert.Contains("_manualConnectionWindow?.ShowError(message)", source, StringComparison.Ordinal);
        Assert.Contains("window.Show();", source, StringComparison.Ordinal);
        Assert.Contains("CloseManualConnectionWindow();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NonHostWindowUsesAdaptiveCompactLayoutWithoutEmptyRoleRows()
    {
        string xaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml");
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml.cs");

        Assert.Contains("Width=\"820\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"680\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SizeToContent=\"Height\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotMatch("(?m)^\\s+Height=\"680\"$", xaml);
        Assert.Contains("x:Name=\"HostSummaryBorder\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ViewerDetailsPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ViewerActionPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"订单联动\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BindingBoundActionsPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource PrimaryButtonStyle}\"", xaml, StringComparison.Ordinal);

        Assert.Contains("MaxHeight=\"244\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MinHeight=\"150\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SearchProgressBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SearchProgressTransform\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ProgressBar", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "await Dispatcher.Yield(DispatcherPriority.Render);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "UpdateSearchProgress(state == ConnectionViewState.Searching);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RepeatBehavior = RepeatBehavior.Forever",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "HandoffBehavior.SnapshotAndReplace",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Trigger Property=\"HasItems\" Value=\"False\">",
            xaml,
            StringComparison.Ordinal);
        Assert.Matches(
            "Trigger Property=\"HasItems\" Value=\"False\"[\\s\\S]*?" +
            "Setter Property=\"Visibility\" Value=\"Collapsed\"",
            xaml);

        int manualConnection = xaml.IndexOf(
            "x:Name=\"ManualConnectionButton\"",
            StringComparison.Ordinal);
        int deferBinding = xaml.IndexOf(
            "x:Name=\"DeferBindingButton\"",
            manualConnection,
            StringComparison.Ordinal);
        int primaryConnection = xaml.IndexOf(
            "x:Name=\"BindSelectedButton\"",
            deferBinding,
            StringComparison.Ordinal);
        Assert.True(
            manualConnection >= 0
            && deferBinding > manualConnection
            && primaryConnection > deferBinding);

        Assert.Contains(
            "ViewerDetailsPanel.Visibility = Visibility.Collapsed;",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HostSummaryLabelColumn", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentHostLabel", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("HostAddressLabel", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CapabilitiesLabel", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RecorderCountLabel", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"OnlineStatusIndicator\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OnlineStatusIndicator.Fill =", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingWorkstationDiscoveryFiltersNonReceiverHostsWithoutChangingViewerResults()
    {
        PackingProofNodeInfo receiver = CreateDiscoveredHost(
            "receiver",
            PackingProofCapabilities.MobileBackup);
        PackingProofNodeInfo viewerOnly = CreateDiscoveredHost(
            "viewer",
            PackingProofCapabilities.Host);
        PackingProofNodeInfo[] hosts = [receiver, viewerOnly];

        Assert.Equal(
            [receiver],
            ViewerClientWindow.FilterDiscoveredHosts(
                hosts,
                recordingWorkstation: true));
        Assert.Equal(
            hosts,
            ViewerClientWindow.FilterDiscoveredHosts(
                hosts,
                recordingWorkstation: false));
    }

    [Fact]
    public void AppMapsEveryPresetToItsDedicatedWindow()
    {
        string source = ReadRepositoryFile("ExpressPackingMonitoring", "App.xaml.cs");

        Assert.Contains("DeploymentPresets.ViewerClient => new ViewerClientWindow", source, StringComparison.Ordinal);
        Assert.Contains("DeploymentPresets.MobileBackupHost => new PrintWorkstationWindow", source, StringComparison.Ordinal);
        Assert.Contains("_ => new MainWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("--temporary-role", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenOtherRole", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingWorkstationStartupDoesNotRequireHostBindingBeforeMainWindow()
    {
        string source = ReadRepositoryFile("ExpressPackingMonitoring", "App.xaml.cs");
        int windowCreation = source.IndexOf(
            "Window window = DeploymentPresets.Normalize(startupPreset) switch",
            StringComparison.Ordinal);

        Assert.True(windowCreation >= 0);
        string startupBeforeWindowCreation = source[..windowCreation];
        Assert.DoesNotContain(
            "RecordingWorkstationHostBindingCancelled",
            startupBeforeWindowCreation,
            StringComparison.Ordinal);
        Assert.DoesNotMatch(
            "new ViewerClientWindow\\(\\s*config,\\s*DeploymentPresets\\.RecordingWorkstation\\)",
            startupBeforeWindowCreation);
        Assert.Contains("_ => new MainWindow", source[windowCreation..], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeploymentPresets.RecordingWorkstation, "", "", "", true)]
    [InlineData(DeploymentPresets.RecordingWorkstation, "node-1", "192.168.1.20:5280", "", true)]
    [InlineData(DeploymentPresets.RecordingWorkstation, "node-1", "192.168.1.20:5280", "0123456789abcdef", false)]
    [InlineData(DeploymentPresets.RecordingHost, "", "", "", false)]
    [InlineData(DeploymentPresets.ViewerClient, "", "", "", false)]
    [InlineData(DeploymentPresets.MobileBackupHost, "", "", "", false)]
    public void StartupHostBindingPromptOnlyAppliesToUnboundRecordingWorkstation(
        string preset,
        string nodeId,
        string address,
        string accessKey,
        bool expected)
    {
        var config = new AppConfig
        {
            DeploymentPreset = preset,
            LastKnownHostNodeId = nodeId,
            LastKnownHostAddress = address,
            LastKnownHostAccessKey = accessKey
        };

        Assert.Equal(
            expected,
            MainViewModel.ShouldPromptRecordingWorkstationHostBinding(config));
    }

    [Fact]
    public void RecordingWorkstationBindingPromptRunsAfterLanStartupAndCanReturnToMainWindow()
    {
        string mainWindow = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml.cs");
        string mainViewModel = RepositorySource.ReadMainViewModel();
        string transferSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Transfer.cs");
        string bindingWindow = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml");
        string bindingSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml.cs");

        Assert.Contains(
            "RunStartupSetupFlowsIfNeeded(this)",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "await RunRecordingWorkstationHostBindingPromptIfNeededAsync(owner);",
            mainViewModel,
            StringComparison.Ordinal);
        int waitForLan = transferSource.IndexOf(
            "lanReady = await startupTask;",
            StringComparison.Ordinal);
        int openBinding = transferSource.IndexOf(
            "ChangeBoundHost(owner);",
            waitForLan,
            StringComparison.Ordinal);
        Assert.True(waitForLan >= 0);
        Assert.True(openBinding > waitForLan);
        Assert.Contains(
            "if (window.ShowDialog() == true)",
            transferSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"DeferBindingButton\"",
            bindingWindow,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"稍后设置\"", bindingWindow, StringComparison.Ordinal);
        Assert.Contains(
            "DeferBindingButton.Visibility = Visibility.Visible;",
            bindingSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void DeferBinding_Click(object sender, RoutedEventArgs e) => Close();",
            bindingSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Shutdown(", bindingSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstDeploymentUsesDraftAndRecordingHostRequiresHardwareSetup()
    {
        string appSource = ReadRepositoryFile("ExpressPackingMonitoring", "App.xaml.cs");
        string wizardSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "FirstUseSetupWizardWindow.xaml.cs");

        Assert.Contains("JsonSerializer.Serialize(config)", appSource, StringComparison.Ordinal);
        Assert.Contains(
            "FirstUseSetupWizardWindow.TryConfigureRecordingHost(",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains("AppConfig.ShouldRunDeploymentSetup(config)", appSource, StringComparison.Ordinal);
        Assert.Contains("AppConfig.ShouldRunRecordingSetup(config)", appSource, StringComparison.Ordinal);
        Assert.Contains("SkipButton.Visibility = allowSkip", wizardSource, StringComparison.Ordinal);
        Assert.Contains("录制主机必须先选择可用摄像头", wizardSource, StringComparison.Ordinal);
        Assert.DoesNotContain("录制主机必须先选择可用麦克风", wizardSource, StringComparison.Ordinal);
        Assert.Contains("_config.EnableAudioRecording = true;", wizardSource, StringComparison.Ordinal);
        Assert.Contains("_config.AudioDeviceName = \"\";", wizardSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedRecordingWizardReturnsToPurposeSelection()
    {
        string appSource = ReadRepositoryFile("ExpressPackingMonitoring", "App.xaml.cs");

        Assert.Contains("confirmCurrentPreset: true", appSource, StringComparison.Ordinal);
        Assert.Contains("AppConfig.ResetDeploymentSetupForRetry(config)", appSource, StringComparison.Ordinal);
        Assert.Contains("requiresDeploymentSetup = true;", appSource, StringComparison.Ordinal);
        Assert.Contains("continue;", appSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstUseWizardUsesDedicatedAdvisoryRecordingPerformanceStep()
    {
        string wizard = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "FirstUseSetupWizardWindow.xaml");
        string wizardSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "FirstUseSetupWizardWindow.xaml.cs");

        Assert.Contains("x:Name=\"StepRecordingProfileText\"", wizard, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecordingProfilePage\"", wizard, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecordingProfileProgress\"", wizard, StringComparison.Ordinal);
        Assert.Contains("IsIndeterminate=\"True\"", wizard, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecordingProfileResultText\"", wizard, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RecordingProfileRetryButton\"", wizard, StringComparison.Ordinal);

        int showDetectionStep = wizardSource.IndexOf("ShowStep(3);", StringComparison.Ordinal);
        int startDetection = wizardSource.IndexOf(
            "await EnsureRecommendedCameraProfileAsync();",
            showDetectionStep,
            StringComparison.Ordinal);
        Assert.True(showDetectionStep >= 0);
        Assert.True(startDetection > showDetectionStep);
        int nextHandler = wizardSource.IndexOf(
            "private async void NextButton_Click",
            StringComparison.Ordinal);
        int detectionStepGuard = wizardSource.IndexOf(
            "if (_stepIndex == 3)",
            nextHandler,
            StringComparison.Ordinal);
        int retryDetection = wizardSource.IndexOf(
            "if (!await EnsureRecommendedCameraProfileAsync())",
            detectionStepGuard,
            StringComparison.Ordinal);
        Assert.True(nextHandler >= 0);
        Assert.True(detectionStepGuard > nextHandler);
        Assert.InRange(retryDetection - detectionStepGuard, 1, 120);
        Assert.Contains("NextButton.IsEnabled = false;", wizardSource, StringComparison.Ordinal);
        Assert.Contains("SkipButton.IsEnabled = false;", wizardSource, StringComparison.Ordinal);
        Assert.Contains("RecordingProfileResultPanel.Visibility = Visibility.Visible;", wizardSource, StringComparison.Ordinal);
        Assert.Contains("_evaluatedCameraMoniker = GetSelectedCameraConfigKey();", wizardSource, StringComparison.Ordinal);
        Assert.Contains("NextButton.Content = \"下一步\";", wizardSource, StringComparison.Ordinal);
        Assert.Contains("SelectSafeFallback(nativeModes)", wizardSource, StringComparison.Ordinal);
        Assert.Contains("return true;", wizardSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstUseWizardStopsRunningPreviewWhenSwitchingToNetworkCamera()
    {
        string wizardSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "FirstUseSetupWizardWindow.xaml.cs");

        int networkBranch = wizardSource.IndexOf(
            "if (IsNetworkCameraSelected)",
            StringComparison.Ordinal);
        int stopPreview = wizardSource.IndexOf(
            "StopCameraPreview()",
            networkBranch,
            StringComparison.Ordinal);
        int showPanel = wizardSource.IndexOf(
            "ShowNetworkCameraPanelUi()",
            stopPreview,
            StringComparison.Ordinal);

        Assert.True(networkBranch >= 0);
        Assert.True(stopPreview > networkBranch);
        Assert.True(showPanel > stopPreview);
        Assert.Contains("CameraPreviewImage.Source = null;", wizardSource, StringComparison.Ordinal);
        Assert.Contains("上一个摄像头未能停止，请重新插拔后重试", wizardSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstUseWizardSeparatesCameraSelectionFromRecognitionPreview()
    {
        string wizard = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "FirstUseSetupWizardWindow.xaml");
        string wizardSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "FirstUseSetupWizardWindow.xaml.cs");

        Assert.Contains("Text=\"3. 摄像头识别\"", wizard, StringComparison.Ordinal);
        Assert.Contains("Text=\"7. 完成\"", wizard, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CameraRecognitionPage\"", wizard, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"UseCameraRecognitionRadio\"", wizard, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DisableCameraRecognitionRadio\"", wizard, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CameraRecognitionPreviewImage\"", wizard, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CameraRecognitionStatusBorder\"", wizard, StringComparison.Ordinal);

        int cameraPageStart = wizard.IndexOf(
            "<Grid x:Name=\"CameraPage\"",
            StringComparison.Ordinal);
        int recognitionPageStart = wizard.IndexOf(
            "<Grid x:Name=\"CameraRecognitionPage\"",
            cameraPageStart,
            StringComparison.Ordinal);
        int profilePageStart = wizard.IndexOf(
            "<Grid x:Name=\"RecordingProfilePage\"",
            recognitionPageStart,
            StringComparison.Ordinal);
        Assert.True(cameraPageStart >= 0);
        Assert.True(recognitionPageStart > cameraPageStart);
        Assert.True(profilePageStart > recognitionPageStart);

        string cameraPage = wizard[cameraPageStart..recognitionPageStart];
        string recognitionPage = wizard[recognitionPageStart..profilePageStart];
        Assert.DoesNotContain("CameraRecognitionGuide", cameraPage, StringComparison.Ordinal);
        Assert.DoesNotContain("<ComboBox", recognitionPage, StringComparison.Ordinal);
        Assert.Contains("CameraRecognitionGuide", recognitionPage, StringComparison.Ordinal);

        Assert.Contains(
            "reportVisibleCodes: true",
            wizardSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_config.EnableCameraBarcodeRecognition =",
            wizardSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_cameraRecognitionFeedbackUntil",
            wizardSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CameraBarcodeRecognitionState.Visible",
            wizardSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetCameraRecognitionGuideColor(\"AccentGreen\")",
            wizardSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CameraRecognitionGuideBorder.Fill =",
            wizardSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CameraRecognitionStatusBorder.Background =",
            wizardSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FirstUseWizardCameraRotationActionStaysReadableOverPreviewFrames()
    {
        string wizard = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "FirstUseSetupWizardWindow.xaml");
        string colorTokens = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Themes",
            "ColorTokens.xaml");

        Assert.Contains("x:Key=\"CameraPreviewOverlayButtonStyle\"", wizard, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource CameraPreviewOverlayButtonBackground}", wizard, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource CameraPreviewOverlayButtonBorder}", wizard, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource CameraPreviewOverlayButtonText}", wizard, StringComparison.Ordinal);
        Assert.Contains(
            "Style=\"{StaticResource CameraPreviewOverlayButtonStyle}\"",
            wizard,
            StringComparison.Ordinal);
        Assert.Contains("x:Key=\"CameraPreviewOverlayButtonBackground\"", colorTokens, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"CameraPreviewOverlayButtonBorder\"", colorTokens, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"CameraPreviewOverlayButtonText\"", colorTokens, StringComparison.Ordinal);
    }

    [Fact]
    public void WizardSkipButtonSitsLeftOfBackButtonInBottomActions()
    {
        string wizard = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "FirstUseSetupWizardWindow.xaml");

        int bottomBar = wizard.LastIndexOf("Grid.Row=\"2\"", StringComparison.Ordinal);
        Assert.True(bottomBar >= 0);
        string bottomSection = wizard[bottomBar..];
        int rightGroup = bottomSection.IndexOf("HorizontalAlignment=\"Right\"", StringComparison.Ordinal);
        int skip = bottomSection.IndexOf("x:Name=\"SkipButton\"", StringComparison.Ordinal);
        int back = bottomSection.IndexOf("x:Name=\"BackButton\"", StringComparison.Ordinal);

        Assert.True(rightGroup >= 0);
        Assert.True(skip > rightGroup);
        Assert.True(back > skip);
        Assert.DoesNotContain("HorizontalAlignment=\"Left\"", bottomSection, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerClientCompletesFirstUseOnlyAfterBindingAValidatedHost()
    {
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml.cs");

        int validation = source.IndexOf(
            "if (!node.IsValidHost)",
            StringComparison.Ordinal);
        int completion = source.IndexOf(
            "AppConfig.MarkDeploymentSetupCompleted(config)",
            StringComparison.Ordinal);

        Assert.True(validation >= 0);
        Assert.True(completion > validation);
    }

    [Fact]
    public void ViewerClientSavesPurposeOnCloseWithoutCompletingSetup()
    {
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml.cs");

        Assert.Contains("PersistPurposeWithoutCompletion();", source, StringComparison.Ordinal);
        int methodStart = source.IndexOf(
            "private void PersistPurposeWithoutCompletion()",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0);

        string method = source[methodStart..];
        int nextMethod = method.IndexOf("private ", 1, StringComparison.Ordinal);
        if (nextMethod > 0)
            method = method[..nextMethod];

        Assert.DoesNotContain("MarkDeploymentSetupCompleted", method, StringComparison.Ordinal);
        Assert.DoesNotContain("DeploymentSetupVersion", method, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchingNonRecordingComputerToRecordingDefersSetupUntilRestart()
    {
        string viewerSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml.cs");
        string mobileBackupSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml.cs");
        string settingsSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml.cs");

        Assert.DoesNotContain(
            "FirstUseSetupWizardWindow.TryConfigureRecordingHost(",
            viewerSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FirstUseSetupWizardWindow.TryConfigureRecordingHost(",
            mobileBackupSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FirstUseSetupWizardWindow.TryConfigureRecordingHost(",
            settingsSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingSetupCopyMakesCameraRecognitionPrimaryAndScannerOptional()
    {
        string selector = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "WorkstationSelectionWindow.xaml");
        string wizard = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "FirstUseSetupWizardWindow.xaml");
        string wizardSource = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "FirstUseSetupWizardWindow.xaml.cs");

        Assert.Contains("面单识别、扫码枪、订单联动和语音提醒", selector, StringComparison.Ordinal);
        Assert.Contains("是否使用摄像头识别面单", wizard, StringComparison.Ordinal);
        Assert.Contains("没有扫码枪可直接进入下一步", wizard, StringComparison.Ordinal);
        Assert.Contains("可选扫码枪仍可随时作为后备方案", wizardSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingHostWindowExposesNodeAndUserscriptStatus()
    {
        string xaml = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "MainWindow.xaml");
        string source = RepositorySource.ReadMainViewModel();

        Assert.Contains("x:Name=\"BtnInstallUserscript\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Config.NodeName", source, StringComparison.Ordinal);
        Assert.Contains(
            "WorkstationPrintStatusText = \"已就绪\";",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WorkstationPrintStatusText = $\"{Config.NodeName} · {verifiedAddress}\";",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("· 录像设备 {recorderCount}", source, StringComparison.Ordinal);
        Assert.Contains("public void OpenUserscriptGuide()", source, StringComparison.Ordinal);
        Assert.Contains("UserscriptGuideNavigation.TryOpen", source, StringComparison.Ordinal);
        Assert.Contains(
            "DeviceItemsSource=\"{Binding MobileBackupDeviceStatuses, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding MainConnectionButtonText, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding UserscriptButtonText, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("UserscriptSetupStatusText", source, StringComparison.Ordinal);
        Assert.Contains("UserscriptSetupShortStatusText", source, StringComparison.Ordinal);
        Assert.Contains("OrderIntegrationStatusText", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingPreviewShowsMultipleItemCountAsIndependentBottomRightBadge()
    {
        string xaml = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "MainWindow.xaml");

        Assert.Contains("Text=\"{Binding PreviewOrderItemCountText}\"", xaml, StringComparison.Ordinal);
        Assert.Matches(
            "HorizontalAlignment=\"Right\"[\\s\\S]*VerticalAlignment=\"Bottom\"[\\s\\S]*PreviewOrderItemCountText",
            xaml);
    }

    [Fact]
    public void EveryDeploymentWindowExposesTestOrderAndRecordingHostUsesTwoRows()
    {
        string recordingXaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml");
        string viewerXaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "ViewerClientWindow.xaml");
        string mobileBackupXaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml");

        Assert.Contains("x:Name=\"BtnSendTestOrder\"", recordingXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SendTestOrderButton\"", viewerXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SendTestOrderButton\"", mobileBackupXaml, StringComparison.Ordinal);
        Assert.Equal(1, recordingXaml.Split("Text=\"发送测试订单\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, viewerXaml.Split("Text=\"发送测试订单\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, mobileBackupXaml.Split("Text=\"发送测试订单\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("<Grid.RowDefinitions>", recordingXaml, StringComparison.Ordinal);
        Assert.Matches("x:Name=\"BtnInstallUserscript\"\\s+Grid.Row=\"2\"", recordingXaml);
        Assert.Matches("x:Name=\"BtnSendTestOrder\"\\s+Grid.Row=\"2\"", recordingXaml);
    }

    [Fact]
    public void RecordingHostSettingsOpenHostedUserscriptGuide()
    {
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml.cs");

        Assert.Contains("Context.OpenUserscriptGuide?.Invoke();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PrintToolInstallGuide.CreateLocalGuide(address)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileBackupHostSettingsOpenHostedUserscriptGuide()
    {
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml.cs");

        Assert.Contains("OpenUserscriptGuide = OpenUserscriptGuide", source, StringComparison.Ordinal);
        Assert.Contains("UserscriptGuideNavigation.TryOpen", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordingWorkstationPlaybackUsesLocalDatabaseAndCacheLocation()
    {
        string mainSource = RepositorySource.ReadMainViewModel();
        string recordingSource = RepositorySource.ReadMainViewModel();
        int methodStart = mainSource.IndexOf(
            "private void OpenPlaybackWindow()",
            StringComparison.Ordinal);
        int methodEnd = mainSource.IndexOf(
            "private static bool ActivateExistingWindow",
            methodStart,
            StringComparison.Ordinal);
        string playbackMethod = mainSource[methodStart..methodEnd];

        Assert.Contains("new PlaybackWindow(", playbackMethod, StringComparison.Ordinal);
        Assert.Contains("folderPath,", playbackMethod, StringComparison.Ordinal);
        Assert.Contains("_db,", playbackMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenBoundHost();", playbackMethod, StringComparison.Ordinal);
        Assert.Contains(
            "RecordingWorkstationCachePolicy.GetConfiguredLocation(Config)",
            recordingSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BackupHostPlaybackCreatesImportServiceForImportButton()
    {
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml.cs");

        Assert.Contains("new VideoFolderImportService(", source, StringComparison.Ordinal);
        Assert.Contains("new PlaybackWindow(", source, StringComparison.Ordinal);
        Assert.Contains("_host.StoragePath", source, StringComparison.Ordinal);
        Assert.Contains("saveImportFolder:", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOrderIntegrationEntryUsesUnifiedPluginCopy()
    {
        string mainWindow = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "MainWindow.xaml");
        string settings = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "SettingsWindow.xaml");
        string viewer = ReadRepositoryFile("ExpressPackingMonitoring", "Workstations", "ViewerClientWindow.xaml");
        string backupHost = ReadRepositoryFile("ExpressPackingMonitoring", "Workstations", "PrintWorkstationWindow.xaml");
        string guide = ReadRepositoryFile("ExpressPackingMonitoring", "Workstations", "PrintToolInstallGuide.cs");
        string guideTemplate = ReadRepositoryFile("ExpressPackingMonitoring", "Web", "kuaidizs-install-guide.html");
        string state = ReadRepositoryFile("ExpressPackingMonitoring", "Services", "UserscriptTargetState.cs");

        Assert.Contains("UserscriptButtonText, Mode=OneWay", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Content=\"安装订单联动\"", settings, StringComparison.Ordinal);
        Assert.Contains("Text=\"安装订单联动\"", viewer, StringComparison.Ordinal);
        Assert.Contains("Text=\"安装订单联动\"", backupHost, StringComparison.Ordinal);
        Assert.Contains(">安装订单联动</a>", guide, StringComparison.Ordinal);
        Assert.Contains(
            "map['安装订单联动']='Install order integration'",
            guideTemplate,
            StringComparison.Ordinal);
        Assert.Equal(4, state.Split("\"安装订单联动\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("安装订单联动插件", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("安装订单联动插件", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("安装订单联动插件", viewer, StringComparison.Ordinal);
        Assert.DoesNotContain("安装订单联动插件", backupHost, StringComparison.Ordinal);
        Assert.DoesNotContain("安装订单联动插件", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("安装订单联动插件", guideTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("安装订单联动插件", state, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"安装订单备注插件\"", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseStartupSmokeTestReadsCurrentSetupVersions()
    {
        string script = ReadRepositoryFile(
            "Tools",
            "Test-Release-Automated.ps1");

        Assert.Contains("function Get-AppConfigVersion", script, StringComparison.Ordinal);
        Assert.Contains(
            "Get-AppConfigVersion \"CurrentRecordingSetupVersion\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "RecordingSetupVersion = $recordingSetupVersion",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "CameraBarcodeSetupVersion = $cameraBarcodeSetupVersion",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "MobileConnectionSetupVersion = $mobileConnectionSetupVersion",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-AppConfigVersion \"CurrentWebProtectionSetupVersion\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "WebProtectionSetupVersion = $webProtectionSetupVersion",
            script,
            StringComparison.Ordinal);
        Assert.Contains("RequireWebAccessKey = $true", script, StringComparison.Ordinal);
        Assert.Contains(
            "FirstUseWizardCompleted = $true",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$env:EPM_DISABLE_LAN_ACCESS_SETUP = \"1\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$env:EPM_DISABLE_LAN_ACCESS_SETUP = $previousDisableLanAccessSetup",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RecordingSetupVersion = 1", script, StringComparison.Ordinal);
    }

    private static PackingProofNodeInfo CreateDiscoveredHost(
        string nodeId,
        string capability) =>
        new()
        {
            Protocol = PackingProofNodeInfo.ExpectedProtocol,
            ProtocolVersion = PackingProofNodeInfo.SupportedProtocolVersion,
            NodeId = nodeId,
            NodeName = nodeId,
            Preset = DeploymentPresets.RecordingHost,
            Capabilities = [capability],
            HttpPort = 5280,
            Address = $"http://127.0.0.1:5280/{nodeId}",
            BackupCompatibility = string.Equals(
                capability,
                PackingProofCapabilities.MobileBackup,
                StringComparison.OrdinalIgnoreCase)
                    ? BackupCompatibilityPolicy.CreateHostInfo()
                    : null
        };

    private static string ReadRepositoryFile(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }

}
