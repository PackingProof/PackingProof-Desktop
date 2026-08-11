using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Xml.Linq;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class NoCameraWorkstationTests
{
    private const string AccessKey = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void LegacyPrintStationRoleMigratesToMobileBackupHost()
    {
        var config = new AppConfig { WorkstationRole = "PrintStation" };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(DeploymentPresets.MobileBackupHost, config.DeploymentPreset);
        Assert.Equal("录像文件备份主机", DeploymentPresets.GetDisplayName(config.DeploymentPreset));
    }

    [Fact]
    public void NoCameraWindowDoesNotOwnCameraOrRecordingViewModel()
    {
        Type[] fieldTypes = typeof(PrintWorkstationWindow)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.DoesNotContain(typeof(MainViewModel), fieldTypes);
        Assert.DoesNotContain(fieldTypes, type => type.Name is "VideoCapture" or "VideoWriter");
        Assert.DoesNotContain(fieldTypes, type => type.Name.Contains("KeyboardHook", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MobileBackupWindowUsesHostCopyAndLanUserscriptGuide()
    {
        string xaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml");
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml.cs");

        Assert.Contains("PackingProof 录像文件备份主机", xaml, StringComparison.Ordinal);
        Assert.Contains("集中保存手机/电脑上传的录像，并提供局域网回放和订单联动", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"连接手机/电脑\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("手机备份主机", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("集中保存手机录像", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"HostIdentityTextBlock\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HeaderComputerNameText.Text = GetHostName();", source, StringComparison.Ordinal);
        Assert.Contains("MobileBackupStatusCard.ShortStatusText = visual switch", source, StringComparison.Ordinal);
        Assert.Contains("\"已就绪\"", source, StringComparison.Ordinal);
        Assert.Contains("\"启动失败\"", source, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TodayBackupCountTextBlock\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TotalBackupCountTextBlock\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"LanAddressTextBlock\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"CopyLanAddressButton\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"StoragePathTextBlock\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, xaml.Split("Style=\"{StaticResource CardStyle}\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("FluentDatabaseIcon", xaml, StringComparison.Ordinal);
        Assert.Matches(
            "x:Name=\"ConnectPhoneButton\"[\\s\\S]*?FluentWifiIcon",
            xaml);
        Assert.Contains("FluentDataIcon", xaml, StringComparison.Ordinal);
        Assert.Contains("FluentVideoIcon", xaml, StringComparison.Ordinal);
        Assert.Contains("FluentSettingsIcon", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("打印工位", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("我没有电脑摄像头", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("打印端", xaml, StringComparison.Ordinal);
        Assert.Contains("if (!_host.IsLanAvailable)", source, StringComparison.Ordinal);
        Assert.Contains(
            "UserscriptGuideNavigation.TryOpen(_host.LanAccessUrl",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PrintToolInstallGuide.CreateLocalGuide(LocalOrderAddress)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("ToastBackground", xaml, StringComparison.Ordinal);
        Assert.Contains("ToastState.IsToastVisible", xaml, StringComparison.Ordinal);
        Assert.Contains("private void ShowToast(", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(2500)", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(4)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox.Show", source, StringComparison.Ordinal);
        Assert.Contains("AppDialog.ShowMessage(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileBackupWindow_SizesToContentAndScrollsBelowScreenLimit()
    {
        string xaml = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml");
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml.cs");
        XElement window = Assert.IsType<XElement>(XDocument.Parse(xaml).Root);

        Assert.Equal("Height", (string?)window.Attribute("SizeToContent"));
        Assert.Equal("720", (string?)window.Attribute("MaxHeight"));
        Assert.Null(window.Attribute("Height"));
        Assert.Null(window.Attribute("MinHeight"));
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "MaxHeight = CalculateWindowMaxHeight(SystemParameters.WorkArea.Height);",
            source,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1080, 720)]
    [InlineData(728, 696)]
    [InlineData(500, 468)]
    public void CalculateWindowMaxHeight_LeavesMarginAndCapsLargeScreens(
        double workAreaHeight,
        double expected)
    {
        Assert.Equal(expected, PrintWorkstationWindow.CalculateWindowMaxHeight(workAreaHeight));
    }

    [Fact]
    public void MobileBackupToastState_RaisesChangesForMessageAndVisibility()
    {
        var state = new PrintWorkstationWindow.MobileBackupToastState();
        var changed = new List<string?>();
        state.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        state.ToastMessage = "测试提示";
        state.IsToastVisible = true;

        Assert.Equal("测试提示", state.ToastMessage);
        Assert.True(state.IsToastVisible);
        Assert.Contains(nameof(state.ToastMessage), changed);
        Assert.Contains(nameof(state.IsToastVisible), changed);
    }

    [Fact]
    public void MobileBackupStatus_MergesOnlineDevicesWithTodayBackupHistory()
    {
        IReadOnlyList<PrintWorkstationWindow.MobileBackupStatusItem> statuses =
            PrintWorkstationWindow.BuildMobileBackupStatuses(
                [
                    new MobileBackupDailyCount("phone-1", "手机1", 3),
                    new MobileBackupDailyCount("phone-2", "手机2", 2),
                    new MobileBackupDailyCount("host-1", "本机", 9, "pc")
                ],
                [
                    new RecordingDeviceInfo
                    {
                        NodeId = "phone-1",
                        NodeName = "手机1",
                        DeviceType = "mobile",
                        Online = true
                    },
                    new RecordingDeviceInfo
                    {
                        NodeId = "phone-1",
                        NodeName = "手机1",
                        DeviceType = "mobile",
                        Online = true
                    },
                    new RecordingDeviceInfo
                    {
                        NodeId = "pc-1",
                        NodeName = "电脑1",
                        DeviceType = "pc",
                        Online = true
                    },
                    new RecordingDeviceInfo
                    {
                        NodeId = "host-1",
                        NodeName = "本机",
                        DeviceType = "pc",
                        Online = true
                    }
                ],
                "host-1");

        Assert.Equal(3, statuses.Count);
        Assert.Contains(statuses, item =>
            item.DeviceId == "phone-1" && item.IsOnline && item.DisplayText == "手机1 · 今日备份 3 个");
        Assert.Contains(statuses, item =>
            item.DeviceId == "phone-2" && !item.IsOnline && item.DisplayText == "手机2 · 今日备份 2 个");
        Assert.Contains(statuses, item =>
            item.DeviceId == "pc-1" && item.IsOnline && item.DisplayText == "电脑1 · 今日备份 0 个");
        Assert.DoesNotContain(statuses, item => item.DeviceId == "host-1");
    }

    [Fact]
    public void MobileBackupStatus_UsesRemoteIdSuffixOnlyForUnnamedRemoteDevices()
    {
        IReadOnlyList<PrintWorkstationWindow.MobileBackupStatusItem> statuses =
            PrintWorkstationWindow.BuildMobileBackupStatuses(
                [new MobileBackupDailyCount("remote-pc-ABC123", "", 1, "pc")],
                [],
                "host-1");

        PrintWorkstationWindow.MobileBackupStatusItem status = Assert.Single(statuses);
        Assert.Equal("remote-pc-ABC123", status.DeviceId);
        Assert.Equal("电脑设备 ABC123 · 今日备份 1 个", status.DisplayText);
    }

    [Fact]
    public void MobileBackupStatus_UsesUnifiedEmptyDeviceCopy()
    {
        PrintWorkstationWindow.MobileBackupStatusItem status = Assert.Single(
            PrintWorkstationWindow.BuildMobileBackupStatuses([], [], "host-1"));

        Assert.Equal("", status.DeviceId);
        Assert.Equal("暂无手机/电脑设备", status.DisplayText);
    }

    [Fact]
    public void MobileBackupPresetIsSavedOnlyAfterLanServiceStarts()
    {
        string source = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml.cs").ReplaceLineEndings("\n");

        int lanReady = source.IndexOf(
            "if (_host.IsLanAvailable)\n                CompleteDeploymentSetup();",
            StringComparison.Ordinal);
        int presetSaved = source.IndexOf(
            "config.DeploymentPreset = DeploymentPresets.MobileBackupHost",
            StringComparison.Ordinal);

        Assert.True(lanReady >= 0);
        Assert.True(presetSaved > lanReady);
    }

    [Fact]
    public void SettingsCapabilitiesAreDerivedFromDeploymentPreset()
    {
        SettingsCapabilities noCamera = SettingsCapabilities.ForPreset(DeploymentPresets.MobileBackupHost);
        SettingsCapabilities camera = SettingsCapabilities.ForPreset(DeploymentPresets.RecordingHost);

        Assert.True(noCamera.IsNoCameraWorkstation);
        Assert.False(noCamera.CanUseCamera);
        Assert.False(noCamera.CanRecordAudio);
        Assert.False(noCamera.CanUseScanner);
        Assert.False(noCamera.IsRecordingDevice);
        Assert.True(noCamera.CanConfigureStorage);

        Assert.False(camera.IsNoCameraWorkstation);
        Assert.True(camera.CanUseCamera);
        Assert.True(camera.CanRecordAudio);
        Assert.True(camera.CanUseScanner);
        Assert.True(camera.IsRecordingDevice);
        Assert.True(camera.CanRecordPcVideo);
    }

    [Fact]
    public void SharedSettingsWindowDoesNotRetainMainViewModel()
    {
        Type[] fieldTypes = typeof(SettingsWindow)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.DoesNotContain(typeof(MainViewModel), fieldTypes);
    }

    [Fact]
    public void SettingsPreviewDoesNotRunBeforeContextAndWindowAreReady()
    {
        Assert.False(SettingsWindow.ShouldPreviewZoomScale(isLoaded: false, context: null!));
        Assert.False(SettingsWindow.ShouldPreviewZoomScale(
            isLoaded: false,
            new SettingsContext
            {
                Capabilities = SettingsCapabilities.ForRole(WorkstationRoles.CameraMonitor),
                ApplyAsync = _ => Task.FromResult(true)
            }));
        Assert.False(SettingsWindow.ShouldPreviewZoomScale(
            isLoaded: true,
            new SettingsContext
            {
                Capabilities = SettingsCapabilities.ForRole(WorkstationRoles.PrintStation),
                ApplyAsync = _ => Task.FromResult(true)
            }));
        Assert.True(SettingsWindow.ShouldPreviewZoomScale(
            isLoaded: true,
            new SettingsContext
            {
                Capabilities = SettingsCapabilities.ForRole(WorkstationRoles.CameraMonitor),
                ApplyAsync = _ => Task.FromResult(true)
            }));
    }

    [Fact]
    public void StorageResolverUsesConfiguredPriorityAndDoesNotFallBackInStrictMode()
    {
        string directory = CreateTempDirectory();
        try
        {
            string first = Path.Combine(directory, "first");
            string second = Path.Combine(directory, "second");
            var config = new AppConfig
            {
                StorageLocations =
                [
                    new StorageLocation { Path = second, Priority = 2 },
                    new StorageLocation { Path = first, Priority = 1 }
                ]
            };

            Assert.Equal(Path.GetFullPath(first), StorageLocationResolver.Resolve(config, allowDefaultFallback: false));

            config.StorageLocations = [];
            IOException exception = Assert.Throws<IOException>(
                () => StorageLocationResolver.Resolve(config, allowDefaultFallback: false));
            Assert.Contains("未配置录像存储位置", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task HostStartsLocalPlaybackMobileBackupAndLocalOrderReceiver()
    {
        string directory = CreateTempDirectory();
        int port = GetFreeTcpPort();
        var config = new AppConfig
        {
            WebServerPort = port,
            WebAccessKey = AccessKey,
            MobileBackupComputerId = Guid.NewGuid().ToString(),
            StorageLocations = [new StorageLocation { Path = Path.Combine(directory, "recordings"), Priority = 1 }]
        };

        try
        {
            using var host = new NoCameraWorkstationHost(
                config,
                Path.Combine(directory, "videos.db"),
                Path.Combine(directory, "state"));

            await host.StartAsync(
                requestLanAccess: false,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(host.IsRunning);
            Assert.NotNull(host.Database);
            Assert.StartsWith($"http://127.0.0.1:{port}", host.LocalPlaybackUrl, StringComparison.Ordinal);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using HttpResponseMessage response = await client.GetAsync(
                "/api/node-info",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(
                "mobile-backup",
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                StringComparison.Ordinal);

            WorkstationNetwork.TestOrderSendResult order =
                await WorkstationNetwork.SendTestOrderAsync(
                    $"127.0.0.1:{port}",
                    TestContext.Current.CancellationToken);
            Assert.True(order.Sent, order.ErrorMessage);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task CorruptDatabaseIsReportedAndServiceDoesNotStart()
    {
        string directory = CreateTempDirectory();
        string databasePath = Path.Combine(directory, "videos.db");
        await File.WriteAllTextAsync(databasePath, "not a sqlite database", TestContext.Current.CancellationToken);
        var config = new AppConfig
        {
            WebServerPort = GetFreeTcpPort(),
            WebAccessKey = AccessKey,
            StorageLocations = [new StorageLocation { Path = Path.Combine(directory, "recordings"), Priority = 1 }]
        };

        try
        {
            using var host = new NoCameraWorkstationHost(config, databasePath, Path.Combine(directory, "state"));
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => host.StartAsync(
                    requestLanAccess: false,
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.False(host.IsRunning);
            Assert.Contains("录像数据库无法打开", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task OccupiedPortIsReportedAndServiceDoesNotStart()
    {
        string directory = CreateTempDirectory();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var config = new AppConfig
        {
            WebServerPort = port,
            WebAccessKey = AccessKey,
            StorageLocations = [new StorageLocation { Path = Path.Combine(directory, "recordings"), Priority = 1 }]
        };

        try
        {
            using var host = new NoCameraWorkstationHost(
                config,
                Path.Combine(directory, "videos.db"),
                Path.Combine(directory, "state"));
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => host.StartAsync(
                    requestLanAccess: false,
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.False(host.IsRunning);
            Assert.Contains("端口", exception.Message, StringComparison.Ordinal);
            Assert.Contains("占用", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("缺少监听权限", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task RepairLanAccessRepairsPermissionsBeforeReportingStorageFailure()
    {
        string directory = CreateTempDirectory();
        bool permissionsRepaired = false;
        var config = new AppConfig
        {
            WebServerPort = GetFreeTcpPort(),
            WebAccessKey = AccessKey,
            StorageLocations = []
        };

        try
        {
            using var host = new NoCameraWorkstationHost(
                config,
                Path.Combine(directory, "videos.db"),
                Path.Combine(directory, "state"),
                _ => permissionsRepaired = true);

            bool repaired = await host.RepairLanAccessAsync(TestContext.Current.CancellationToken);

            Assert.True(permissionsRepaired);
            Assert.False(repaired);
            Assert.False(host.IsRunning);
            Assert.Contains("局域网权限已修复", host.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("录像存储不可用", host.ErrorMessage, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ExpressPackingMonitoring.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }

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
