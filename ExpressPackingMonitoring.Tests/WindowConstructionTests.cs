using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.UI.Controls;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 所有 XAML 窗口/控件都要能真正构造并走完一次布局。
///
/// 设置窗口把 ContextMenu 内联写在 Setter.Value 里以后，加载直接抛 InvalidCastException，
/// 而编译、BAML 校验和"读源码断言字符串"的守卫都发现不了，只有用户点开设置才炸。
/// 这里两道守卫：真的构造一遍 + 新增 XAML 窗口必须登记（构造或写清为什么暂时不构造）。
/// </summary>
[Collection("WPF render tests")]
public sealed class WindowConstructionTests
{
    private const string AppXamlClass = "ExpressPackingMonitoring.App";

    /// <summary>在别的渲染测试里已经真构造过的窗口。</summary>
    private static readonly string[] CoveredByOtherTests =
    [
        "ExpressPackingMonitoring.UI.PlaybackWindow",  // PlaybackWindowRenderTests
        "ExpressPackingMonitoring.UI.SettingsWindow"   // SettingsWindowRenderTests
    ];

    /// <summary>暂时不做真构造的窗口：必须写清原因，改到它们时重新评估。</summary>
    private static readonly Dictionary<string, string> KnownGaps = new(StringComparer.Ordinal)
    {
        ["ExpressPackingMonitoring.UI.FirstUseSetupWizardWindow"] = "构造里就拉起条码识别与摄像头枚举",
        ["ExpressPackingMonitoring.UI.FloatingPreviewWindow"] = "需要真实 MainViewModel 与摄像头预览栈",
        ["ExpressPackingMonitoring.PrintWorkstationWindow"] = "构造即启动工位会话",
        ["ExpressPackingMonitoring.ViewerClientWindow"] = "构造即启动查看端会话"
    };

    [Fact]
    public void EveryXamlWindowOrControlIsConstructedOrRegistered()
    {
        string[] declared = DeclaredXamlClasses();
        string[] unregistered = declared
            .Where(name => !CoveredByOtherTests.Contains(name, StringComparer.Ordinal))
            .Where(name => !KnownGaps.ContainsKey(name))
            .Where(name => !Factories().Any(entry => entry.Name == name))
            .ToArray();

        Assert.True(
            unregistered.Length == 0,
            "新增/改名的 XAML 窗口或控件必须在 WindowConstructionTests 里真构造一次，"
            + "或在 KnownGaps 里登记并写清原因: " + string.Join("、", unregistered));

        // 反向检查：登记过的类型真的还在，避免改名后留下过期条目
        string[] staleCovered = CoveredByOtherTests
            .Concat(KnownGaps.Keys)
            .Where(name => !declared.Contains(name, StringComparer.Ordinal))
            .ToArray();
        Assert.True(
            staleCovered.Length == 0,
            "这些登记项已经不存在了，请从守卫名单里删掉: " + string.Join("、", staleCovered));
    }

    [Fact]
    public void RegisteredWindowsAndControls_ConstructAndLayout()
    {
        var disposables = new List<IDisposable>();
        var failures = new List<string>();

        RunOnStaThread(() =>
        {
            foreach ((string name, Func<object> factory) in Factories())
            {
                try
                {
                    object instance = factory();
                    if (instance is IDisposable disposable) disposables.Add(disposable);
                    Layout(instance);
                }
                catch (Exception ex)
                {
                    var detail = new System.Text.StringBuilder();
                    for (Exception? current = ex; current != null; current = current.InnerException)
                        detail.Append($"{current.GetType().Name}: {current.Message} ");
                    failures.Add($"{name}（{detail.ToString().Trim()}）");
                }
            }
        });

        foreach (IDisposable disposable in disposables)
        {
            try { disposable.Dispose(); } catch { }
        }

        Assert.True(failures.Count == 0, "这些窗口/控件构造失败：" + string.Join("；", failures));
    }

    /// <summary>真构造的窗口与控件清单（与守卫的名单一一对应）。</summary>
    private static IEnumerable<(string Name, Func<object> Factory)> Factories()
    {
        yield return (
            "ExpressPackingMonitoring.UI.MainWindow",
            () => new MainWindow(enableCloseBehaviorPrompt: false));
        yield return (
            "ExpressPackingMonitoring.UI.StatisticsWindow",
            () => new StatisticsWindow(new VideoDatabase(":memory:")));
        yield return (
            "ExpressPackingMonitoring.UI.BackupDeviceEnrollmentApprovalWindow",
            () => new BackupDeviceEnrollmentApprovalWindow(new BackupDeviceEnrollmentRequest
            {
                DeviceId = "test-device",
                DeviceName = "测试手机",
                DeviceKind = "mobile",
                RemoteAddress = "192.168.1.10"
            }));
        yield return (
            "ExpressPackingMonitoring.UI.CameraBarcodeUpgradeDialog",
            () => new CameraBarcodeUpgradeDialog());
        yield return (
            "ExpressPackingMonitoring.UI.CloseBehaviorDialog",
            () => new CloseBehaviorDialog());
        yield return (
            "ExpressPackingMonitoring.UI.ConfirmDialog",
            () => new ConfirmDialog("测试消息", "测试标题"));
        yield return (
            "ExpressPackingMonitoring.UI.ExtensionEnrollmentApprovalWindow",
            () => new ExtensionEnrollmentApprovalWindow(
                new ExtensionEnrollmentRequest
                {
                    ExtensionInstanceId = "test-extension",
                    ProviderId = "test-provider",
                    DisplayName = "测试扩展",
                    Version = "1.0.0",
                    RequestedPermissions = ["storage.read"]
                },
                localOriginNodeId: "local-node",
                localOriginNodeName: "本机"));
        yield return (
            "ExpressPackingMonitoring.UI.ExtensionMarketWindow",
            () => new ExtensionMarketWindow());
        yield return (
            "ExpressPackingMonitoring.UI.ManualCleanupDialog",
            () => new ManualCleanupDialog(ManualCleanupKind.ByTime));
        yield return (
            "ExpressPackingMonitoring.UI.MobileAppUpdatePromptWindow",
            () => new MobileAppUpdatePromptWindow("测试提示", () => { }));
        yield return (
            "ExpressPackingMonitoring.UI.MobileConnectionWindow",
            () => new MobileConnectionWindow("http://127.0.0.1:5280/?key=test", accessProtected: true));
        yield return (
            "ExpressPackingMonitoring.UI.ModeTransitionButton",
            () => new ModeTransitionButton());
        yield return (
            "ExpressPackingMonitoring.UI.OrderNumberExportProgressDialog",
            () => new OrderNumberExportProgressDialog(
                new VideoDatabase(":memory:"),
                new OrderNumberExportFilter(null, null),
                Path.Combine(Path.GetTempPath(), "订单号导出.csv")));
        yield return (
            "ExpressPackingMonitoring.UI.StoragePathSelectionDialog",
            () => new StoragePathSelectionDialog(Path.Combine(Path.GetTempPath(), "快递打包视频")));
        yield return (
            "ExpressPackingMonitoring.UI.StorageReserveDialog",
            () => new StorageReserveDialog(new StorageLocation
            {
                Path = Path.Combine(Path.GetTempPath(), "快递打包视频"),
                Priority = 0
            }));
        yield return (
            "ExpressPackingMonitoring.UI.Controls.StatusCard",
            () => new StatusCard());
        yield return (
            "ExpressPackingMonitoring.UI.TrayKeyboardListeningDialog",
            () => new TrayKeyboardListeningDialog());
        yield return (
            "ExpressPackingMonitoring.UI.UpdateAvailableDialog",
            () => new UpdateAvailableDialog(new UpdateCheckResult
            {
                HasUpdate = true,
                LatestVersion = "0.0.99",
                Title = "测试更新",
                Body = "测试说明",
                DownloadUrl = "https://example.invalid/download"
            }));
        yield return (
            "ExpressPackingMonitoring.UI.VideoImportDialog",
            () => new VideoImportDialog(
                new VideoFolderImportService(
                    new VideoDatabase(":memory:"),
                    [Path.GetTempPath()],
                    sourceDeviceId: "test-device",
                    sourceDeviceName: "测试设备"),
                Path.GetTempPath(),
                Path.GetTempPath()));
        yield return (
            "ExpressPackingMonitoring.ManualHostConnectionWindow",
            () => new ManualHostConnectionWindow(requiresCompleteLink: false));
        yield return (
            "ExpressPackingMonitoring.WorkstationSelectionWindow",
            () => new WorkstationSelectionWindow());
    }

    /// <summary>走完一次布局：模板展开、资源键解析与内容赋值都在这一步发生。</summary>
    private static void Layout(object instance)
    {
        if (instance is Window window)
        {
            window.Measure(new Size(1200, 900));
            window.Arrange(new Rect(0, 0, 1200, 900));
            window.UpdateLayout();
            window.Close();
            return;
        }

        if (instance is FrameworkElement element)
        {
            element.Measure(new Size(1200, 900));
            element.Arrange(new Rect(0, 0, 1200, 900));
            element.UpdateLayout();
        }
    }

    private static string[] DeclaredXamlClasses()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring");
        return Directory.GetFiles(projectPath, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => Regex
                .Matches(File.ReadAllText(path), "x:Class=\"([^\"]+)\"")
                .Select(match => match.Groups[1].Value))
            .Where(name => !string.Equals(name, AppXamlClass, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>WPF 控件必须在 STA 线程上创建，测试宿主默认 MTA。</summary>
    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current == null)
                    _ = new Application();
                LoadAppResources();
                action();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "STA 线程执行超时");
        if (failure != null)
        {
            var detail = new System.Text.StringBuilder();
            for (Exception? current = failure; current != null; current = current.InnerException)
                detail.AppendLine($"{current.GetType().Name}: {current.Message}");
            detail.AppendLine(failure.StackTrace);
            throw new Xunit.Sdk.XunitException($"窗口守卫执行失败：{detail}");
        }
    }

    /// <summary>与 App.xaml 相同的合并顺序，否则窗口里的资源键解析不到。</summary>
    private static void LoadAppResources()
    {
        string[] files =
        [
            "ColorTokens.xaml", "LightTheme.xaml", "ComboBoxTheme.xaml", "DatePickerTheme.xaml",
            "SpinBoxTheme.xaml", "TextBoxTheme.xaml", "ButtonTheme.xaml", "ScrollBarTheme.xaml",
            "FluentIcons.xaml", "SliderTheme.xaml", "MenuTheme.xaml"
        ];

        var merged = new ResourceDictionary();
        foreach (string file in files)
        {
            merged.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/ExpressPackingMonitoring;component/themes/{file.ToLowerInvariant()}",
                    UriKind.Absolute)
            });
        }

        if (Application.Current != null)
            Application.Current.Resources = merged;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
