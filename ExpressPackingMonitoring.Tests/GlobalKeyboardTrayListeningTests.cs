using System.Text;
using System.Text.Json;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Input;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class GlobalKeyboardTrayListeningTests
{
    [Theory]
    [InlineData(false, false, null, null, false)]
    [InlineData(true, false, null, null, true)]
    [InlineData(true, true, null, null, true)]
    [InlineData(true, true, TrayKeyboardListeningBehaviors.Continue, null, true)]
    [InlineData(true, true, TrayKeyboardListeningBehaviors.Pause, null, false)]
    [InlineData(true, true, TrayKeyboardListeningBehaviors.Ask, false, false)]
    [InlineData(true, true, TrayKeyboardListeningBehaviors.Pause, true, true)]
    public void ListeningPolicy_DistinguishesTaskbarAndTrayStates(
        bool enabled,
        bool isInTray,
        string? trayBehavior,
        bool? sessionOverride,
        bool expected)
    {
        Assert.Equal(
            expected,
            GlobalKeyboardListeningPolicy.ShouldListen(
                enabled,
                isInTray,
                trayBehavior,
                sessionOverride));
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(true, TrayKeyboardListeningBehaviors.Ask, true)]
    [InlineData(true, TrayKeyboardListeningBehaviors.Continue, false)]
    [InlineData(true, TrayKeyboardListeningBehaviors.Pause, false)]
    public void ListeningPolicy_PromptsOnlyWhenEnabledAndConfiguredToAsk(
        bool enabled,
        string? trayBehavior,
        bool expected)
    {
        Assert.Equal(
            expected,
            GlobalKeyboardListeningPolicy.ShouldPromptBeforeTray(enabled, trayBehavior));
    }

    [Fact]
    public void ExistingConfig_DefaultsToAskAndKeepsLegacyListeningFallback()
    {
        AppConfig config = JsonSerializer.Deserialize<AppConfig>(
            "{\"EnableGlobalKeyboard\":true}")!;

        Assert.Equal(
            TrayKeyboardListeningBehaviors.Ask,
            config.TrayKeyboardListeningBehavior);
        Assert.True(GlobalKeyboardListeningPolicy.ShouldPromptBeforeTray(
            config.EnableGlobalKeyboard,
            config.TrayKeyboardListeningBehavior));
        Assert.True(GlobalKeyboardListeningPolicy.ShouldListen(
            config.EnableGlobalKeyboard,
            isInTray: true,
            config.TrayKeyboardListeningBehavior));

        config.TrayKeyboardListeningBehavior = TrayKeyboardListeningBehaviors.Pause;
        AppConfig restored = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;
        Assert.Equal(
            TrayKeyboardListeningBehaviors.Pause,
            restored.TrayKeyboardListeningBehavior);
        Assert.False(GlobalKeyboardListeningPolicy.ShouldPromptBeforeTray(
            restored.EnableGlobalKeyboard,
            restored.TrayKeyboardListeningBehavior));
    }

    [Fact]
    public void RuntimeController_PausesInTrayAndRestoresAfterReturning()
    {
        var hook = new FakeGlobalKeyboardHook();
        using var controller = new GlobalKeyboardRuntimeController(hook, _ => { });
        var config = new AppConfig
        {
            EnableGlobalKeyboard = true,
            TrayKeyboardListeningBehavior = TrayKeyboardListeningBehaviors.Pause
        };

        controller.Apply(config, _ => true);
        Assert.True(hook.IsRunning);

        controller.SetTrayState(isInTray: true);
        Assert.False(hook.IsRunning);
        Assert.Equal(1, hook.StopCalls);

        controller.SetTrayState(isInTray: false);
        Assert.True(hook.IsRunning);
        Assert.Equal(2, hook.StartCalls);
    }

    [Fact]
    public void RuntimeController_UsesUnsavedChoiceForCurrentTraySessionOnly()
    {
        var hook = new FakeGlobalKeyboardHook();
        using var controller = new GlobalKeyboardRuntimeController(hook, _ => { });
        var config = new AppConfig
        {
            EnableGlobalKeyboard = true,
            TrayKeyboardListeningBehavior = TrayKeyboardListeningBehaviors.Ask
        };

        controller.Apply(config, _ => true);
        controller.SetTrayState(isInTray: true, sessionTrayOverride: false);
        Assert.False(hook.IsRunning);

        controller.SetTrayState(isInTray: false);
        Assert.True(hook.IsRunning);

        controller.SetTrayState(isInTray: true);
        Assert.True(hook.IsRunning);
    }

    [Fact]
    public void SettingsAndTrayUi_ExposeThreeStateChoiceAndRememberOption()
    {
        string settings = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "SettingsWindow.xaml");
        string trayService = ReadRepositoryFile("ExpressPackingMonitoring", "Services", "TrayIconService.cs");
        string trayPrompt = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "TrayKeyboardListeningDialog.xaml");
        string closePrompt = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "CloseBehaviorDialog.xaml");

        Assert.DoesNotContain("首次进入系统托盘时会询问是否继续监听", settings, StringComparison.Ordinal);
        Assert.Contains("托盘期间扫码监听", settings, StringComparison.Ordinal);
        Assert.Contains("Config.TrayKeyboardListeningBehavior", settings, StringComparison.Ordinal);
        Assert.Contains("Tag=\"Ask\" Content=\"每次询问\"", settings, StringComparison.Ordinal);
        Assert.Contains("Tag=\"Continue\" Content=\"继续监听\"", settings, StringComparison.Ordinal);
        Assert.Contains("Tag=\"Pause\" Content=\"不监听\"", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility\" Value=\"Collapsed\"", settings[settings.IndexOf("托盘期间扫码监听", StringComparison.Ordinal)..settings.IndexOf("无回车窗口内识别", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains("new TrayKeyboardListeningDialog", trayService, StringComparison.Ordinal);
        Assert.Contains("if (!dialog.RememberChoice)", trayService, StringComparison.Ordinal);
        Assert.Contains("不再提示，记住我的选择", trayPrompt, StringComparison.Ordinal);
        Assert.Contains("Content=\"继续监听\"", trayPrompt, StringComparison.Ordinal);
        Assert.Contains("Content=\"不监听\"", trayPrompt, StringComparison.Ordinal);
        Assert.Contains("扫码枪是否继续监听由你的设置决定", closePrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayService_NotifiesOnlyExplicitTrayTransitions()
    {
        string trayService = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Services",
            "TrayIconService.cs");
        string mainWindow = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml.cs");

        Assert.Contains("_trayStateChanged?.Invoke(true, sessionKeyboardListeningOverride)", trayService, StringComparison.Ordinal);
        Assert.Contains("_trayStateChanged?.Invoke(false, null)", trayService, StringComparison.Ordinal);
        Assert.Contains("SetMainWindowInTray(isInTray, sessionOverride)", mainWindow, StringComparison.Ordinal);

        int stateChangedStart = mainWindow.IndexOf("StateChanged +=", StringComparison.Ordinal);
        int stateChangedEnd = mainWindow.IndexOf("// 全局鼠标/键盘活跃检测", stateChangedStart, StringComparison.Ordinal);
        Assert.True(stateChangedStart >= 0 && stateChangedEnd > stateChangedStart);
        Assert.DoesNotContain(
            "SetMainWindowInTray",
            mainWindow[stateChangedStart..stateChangedEnd],
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] relativeParts)
    {
        string path = Path.Combine(FindRepositoryRoot(), Path.Combine(relativeParts));
        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }

    private sealed class FakeGlobalKeyboardHook : IGlobalKeyboardHook
    {
        public event Action<string>? BarcodeScanned
        {
            add { }
            remove { }
        }

        internal bool IsRunning { get; private set; }
        internal int StartCalls { get; private set; }
        internal int StopCalls { get; private set; }

        public void ConfigureAutoSubmit(
            bool enabled,
            int minLength,
            int quietMs,
            int maxAverageIntervalMs,
            int maxKeyIntervalMs,
            Func<string, bool>? isCandidate)
        {
        }

        public void Start()
        {
            StartCalls++;
            IsRunning = true;
        }

        public void Stop()
        {
            StopCalls++;
            IsRunning = false;
        }

        public void Dispose()
        {
            IsRunning = false;
        }
    }
}
