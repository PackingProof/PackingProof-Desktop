using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.ViewModels;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("zh-Hans", "PackingProofVideos", "快递打包视频")]
    [InlineData("zh-Hans", "PhoneBackup", "手机备份")]
    [InlineData("zh-Hans", "PCUpload", "电脑上传")]
    [InlineData("ja-JP", "PackingProofVideos", "梱包動画")]
    [InlineData("ja-JP", "PhoneBackup", "スマートフォンのバックアップ")]
    [InlineData("ja-JP", "PCUpload", "PCアップロード")]
    [InlineData("en-US", "PhoneBackup", null)]
    public void StorageFolderAlias_FollowsLocalizedResource(
        string culture,
        string folderName,
        string? expected)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(expected, StorageFolderAliases.GetAliasName(folderName));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("zh-CN", AppLanguage.Chinese)]
    [InlineData("zh-TW", AppLanguage.Chinese)]
    [InlineData("ja-JP", AppLanguage.Japanese)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("fr-FR", AppLanguage.English)]
    public void Resolve_AutoUsesChineseFamilyAndFallsBackToEnglish(string culture, string expected)
    {
        Assert.Equal(expected, AppLanguage.Resolve(AppLanguage.Auto, CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void NormalizeAfterLoad_SelectsJapaneseVoicesForJapaneseLanguage()
    {
        var config = new AppConfig { Language = AppLanguage.Japanese };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal("ja-JP-NanamiNeural", config.EdgeTtsVoiceJaJp);
        Assert.Equal("ja-JP-KeitaNeural", config.EdgeTtsWarningVoiceJaJp);
        Assert.Equal(config.EdgeTtsVoiceJaJp, config.EdgeTtsVoice);
        Assert.Equal(config.EdgeTtsWarningVoiceJaJp, config.EdgeTtsWarningVoice);
        Assert.DoesNotContain("zh-CN", config.EdgeTtsVoice, StringComparison.Ordinal);
    }

    /// <summary>保存设置时只写回当前语言，不覆盖其他语言已经选好的声线。</summary>
    [Fact]
    public void StoreSelectedSpeechVoices_WritesCurrentLanguageSlotOnly()
    {
        var config = new AppConfig { Language = AppLanguage.English };
        AppConfig.NormalizeAfterLoad(config);
        string chineseVoice = config.EdgeTtsVoiceZhHans;

        config.EdgeTtsVoice = "en-US-AriaNeural";
        config.EdgeTtsWarningVoice = "en-US-DavisNeural";
        config.StoreSelectedSpeechVoices();

        Assert.Equal("en-US-AriaNeural", config.EdgeTtsVoiceEnUs);
        Assert.Equal("en-US-DavisNeural", config.EdgeTtsWarningVoiceEnUs);
        Assert.NotEqual("en-US-AriaNeural", config.EdgeTtsVoiceZhHans);
        Assert.Equal(chineseVoice, config.EdgeTtsVoiceZhHans);

        AppConfig.NormalizeAfterLoad(config);
        Assert.Equal("en-US-AriaNeural", config.EdgeTtsVoice);
    }

    [Fact]
    public void NormalizeAfterLoad_MigratesLegacyVoicesAndInvalidLanguage()
    {
        var config = new AppConfig
        {
            Language = "invalid",
            EdgeTtsVoice = "zh-CN-XiaoyiNeural",
            EdgeTtsWarningVoice = "zh-CN-YunxiNeural",
            EdgeTtsVoiceZhHans = "",
            EdgeTtsWarningVoiceZhHans = ""
        };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        Assert.Equal(AppLanguage.Auto, config.Language);
        Assert.Equal("zh-CN-XiaoyiNeural", config.EdgeTtsVoiceZhHans);
        Assert.Equal("zh-CN-YunxiNeural", config.EdgeTtsWarningVoiceZhHans);
        Assert.Equal("en-US-JennyNeural", config.EdgeTtsVoiceEnUs);
    }

    [Fact]
    public void Resources_ContainEnglishDefaultAndChineseSatelliteValues()
    {
        Assert.Equal("Settings", AppLanguage.Get("设置", CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("设置", AppLanguage.Get("设置", CultureInfo.GetCultureInfo("zh-Hans")));
        Assert.Equal("設定", AppLanguage.Get("设置", CultureInfo.GetCultureInfo("ja-JP")));
        Assert.Equal("Recording started", AppLanguage.Get("Speech.StartRecording", CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("开始录制", AppLanguage.Get("Speech.StartRecording", CultureInfo.GetCultureInfo("zh-Hans")));
        Assert.Equal("録画を開始しました", AppLanguage.Get("Speech.StartRecording", CultureInfo.GetCultureInfo("ja-JP")));
    }

    [Theory]
    [InlineData("zh-Hans", "PackingProof 包裹留证", "PackingProof 包裹留证 v9.9.9")]
    [InlineData("en-US", "PackingProof package evidence", "PackingProof package evidence v9.9.9")]
    [InlineData("ja-JP", "PackingProof 荷物の証跡", "PackingProof 荷物の証跡 v9.9.9")]
    public void MainTitle_UsesBrandTranslationAndVersion(
        string culture,
        string expectedProductName,
        string expectedTitle)
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(expectedProductName, AppLanguage.Get("App.ProductName"));
            Assert.Equal(expectedTitle, AppLanguage.Format("Main.Title", "v9.9.9"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    /// <summary>
    /// 代码里拼出来的中文（标签＋冒号、带参数的整句）也要能翻译，
    /// 否则回放悬浮提示和查看端状态在英文/日文界面会整段显示中文。
    /// </summary>
    [Fact]
    public void Translate_LocalizesComposedLabelsAndTemplates()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("Tracking number: SF001", AppLanguage.Translate("快递单号：SF001"));
            Assert.Equal("Found 3 hosts. Select one to connect", AppLanguage.Translate("找到 3 台主机，请选择要连接的主机"));
            Assert.Equal("Connected to PC-A", AppLanguage.Translate("已连接 PC-A"));
            Assert.Equal("未知文本", AppLanguage.Translate("未知文本"));
            Assert.Equal("Cannot access the storage path: D:\\videos", AppLanguage.Translate("无法访问存储路径：D:\\videos"));
            Assert.Equal(
                "Shipping / Return: Shipping" + Environment.NewLine
                + "Tracking number: SF001" + Environment.NewLine
                + "Buyer message: 尽快发货",
                AppLanguage.Translate("发退货：发货\n快递单号：SF001\n买家留言：尽快发货"));

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            Assert.Equal("追跡番号: SF001", AppLanguage.Translate("快递单号：SF001"));
            Assert.Equal("PC-A に接続しました", AppLanguage.Translate("已连接 PC-A"));
            Assert.Equal("保存先にアクセスできません: D:\\videos", AppLanguage.Translate("无法访问存储路径：D:\\videos"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    /// <summary>
    /// 绑定文案只翻译整条词条和完整句式，不能把用户数据里的“标签：内容”拆开。
    /// </summary>
    [Fact]
    public void TranslateRuntime_HandlesTemplatesWithoutRewritingUserData()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("Sub camera 1", AppLanguage.TranslateRuntime("副摄像头 1"));
            Assert.Equal("Connected to PC-A", AppLanguage.TranslateRuntime("已连接 PC-A"));
            Assert.Equal("订单：SF001", AppLanguage.TranslateRuntime("订单：SF001"));

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            Assert.Equal("サブカメラ 1", AppLanguage.TranslateRuntime("副摄像头 1"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Theory]
    [InlineData("en-US", "auto", "Auto (benchmark)")]
    [InlineData("en-US", "cpu", "CPU software encoding")]
    [InlineData("en-US", "h264", "H.264 (best compatibility)")]
    [InlineData("en-US", "h265", "H.265 / HEVC (smaller files)")]
    [InlineData("en-US", "av1", "AV1 (maximum compression)")]
    [InlineData("ja-JP", "auto", "自動（実測）")]
    [InlineData("ja-JP", "cpu", "CPU ソフトウェア エンコード")]
    public void EncoderOptions_AreLocalizedByStableValue(
        string culture,
        string value,
        string expected)
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(expected, GpuEncoderOption.BuildDisplayName(value));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void ScannerAlerts_HaveLocalizedMessagesAndTemplates()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("Warning: no order number", AppLanguage.Translate("警告：没有单号"));
            Assert.Equal("Order number mismatch: SF001", AppLanguage.Translate("单号不一致：SF001"));
            Assert.Equal(
                "Warning: waybill SF001, refund pending",
                AppLanguage.Translate("警告：快递单 SF001，refund pending"));
            Assert.Equal(
                "Printed refund detected; verify manually",
                AppLanguage.Translate("存在打印后退款，请人工核对"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    /// <summary>
    /// 默认资源（英文）里每一把键都必须在 zh-Hans 卫星资源里有对应条目。
    /// 只加英文不加中文时，中文界面会回退到中性资源，也就是直接显示英文。
    /// </summary>
    [Fact]
    public void Resources_EveryDefaultKeyHasChineseEntry()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        string defaultPath = Path.Combine(projectPath, "Resources", "Strings.resx");
        string chinesePath = Path.Combine(projectPath, "Resources", "Strings.zh-Hans.resx");

        string[] missing = ReadKeys(defaultPath)
            .Except(ReadKeys(chinesePath), StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, "Missing zh-Hans resources: " + string.Join(" | ", missing));
    }

    /// <summary>
    /// 日语同样依赖卫星资源：缺词条时会回退到中性的英文资源。
    /// </summary>
    [Fact]
    public void Resources_EveryDefaultKeyHasJapaneseEntry()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        string defaultPath = Path.Combine(projectPath, "Resources", "Strings.resx");
        string japanesePath = Path.Combine(projectPath, "Resources", "Strings.ja-JP.resx");

        string[] missing = ReadKeys(defaultPath)
            .Except(ReadKeys(japanesePath), StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, "Missing ja-JP resources: " + string.Join(" | ", missing));
    }

    [Fact]
    public void WpfViews_AllStaticChineseTextHasEnglishResource()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        string[] views = Directory.GetFiles(projectPath, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith($"{Path.DirectorySeparatorChar}App.xaml", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var values = views
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    "(?:Text|Content|Header|Title|ToolTip|AutomationProperties\\.Name)=\"([^\"]*[\\p{IsCJKUnifiedIdeographs}][^\"]*)\"")
                .Select(match => ResolveLocalizationKey(match.Groups[1].Value)))
            .Where(value => !value.StartsWith("{Binding ", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var english = CultureInfo.GetCultureInfo("en-US");
        string[] missing = values
            .Where(value => Regex.IsMatch(AppLanguage.Get(value, english), "[\\p{IsCJKUnifiedIdeographs}]"))
            .ToArray();

        Assert.True(missing.Length == 0, "Missing English resources: " + string.Join(" | ", missing));
    }

    [Fact]
    public void WpfViews_StaticVisibleTextDoesNotEndWithChineseFullStop()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        string[] views = Directory.GetFiles(projectPath, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith($"{Path.DirectorySeparatorChar}App.xaml", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        string[] xamlViolations = views
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    "(?:Text|Content|Header|Title|ToolTip|AutomationProperties\\.Name)=\"([^\"]*)\"")
                .Select(match => ResolveLocalizationKey(match.Groups[1].Value)))
            .Where(value => value.EndsWith("。", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(xamlViolations.Length == 0, "XAML 可见文案不得以句号结尾: " + string.Join(" | ", xamlViolations));

        string zhResxPath = Path.Combine(projectPath, "Resources", "Strings.zh-Hans.resx");
        string[] resxViolations = Regex.Matches(File.ReadAllText(zhResxPath), "<value>([^<]*)</value>")
            .Select(match => match.Groups[1].Value)
            .Where(value => value.EndsWith("。", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(resxViolations.Length == 0, "zh-Hans 资源值不得以句号结尾: " + string.Join(" | ", resxViolations));
    }

    /// <summary>
    /// DataGridColumn 不在可视树里，自动本地化遍历不到；中文列标题必须用 loc:Localized。
    /// </summary>
    [Fact]
    public void WpfViews_DataGridColumnHeadersUseLocalizedExtension()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        string[] violations = Directory.GetFiles(projectPath, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    "<DataGrid[A-Za-z]*Column\\b[^>]*Header=\"([^\"]*[\\p{IsCJKUnifiedIdeographs}][^\"]*)\"")
                .Select(match => match.Groups[1].Value))
            .Where(value => !value.StartsWith("{loc:Localized ", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "DataGrid 列标题必须使用 {loc:Localized ...}: " + string.Join(" | ", violations));
    }

    /// <summary>
    /// ContextMenu 和普通 MenuItem 的 Header 不在窗口可视树遍历里，
    /// 静态中文必须显式使用 {loc:Localized ...}，否则资源存在也不会生效。
    /// </summary>
    [Fact]
    public void WpfViews_MenuItemHeadersUseLocalizedExtension()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        string[] violations = Directory.GetFiles(projectPath, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    "<MenuItem\\b[^>]*Header=\"([^\"]*[\\p{IsCJKUnifiedIdeographs}][^\"]*)\"")
                .Select(match => match.Groups[1].Value))
            .Where(value => !value.StartsWith("{loc:Localized ", StringComparison.Ordinal)
                && !value.StartsWith("{localization:Localized ", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "MenuItem 静态中文标题必须使用 {loc:Localized ...}: " + string.Join(" | ", violations));
    }

    [Fact]
    public void ExtensionAuthorizationDisplayText_FollowsCurrentLanguage()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var value = new ExtensionAuthorizationContext
            {
                ExtensionInstanceId = "extension-1",
                DisplayName = "Test extension",
                Version = "1.0",
                Source = "test",
                Permissions = ["recordings.delivery", "scan-results.write"],
                RoutingScope = ExtensionRoutingScope.AllLocalRecordingNodes,
                LastBusinessActivityUtc = DateTimeOffset.UtcNow,
                LastBusinessDataCount = 3
            };

            ExtensionAuthorizationDisplayItem item = ExtensionAuthorizationDisplayFactory.Create(value);

            Assert.Equal("recordings.delivery, scan-results.write", item.PermissionsText);
            Assert.Equal("All local recording workstations", item.BindingText);
            Assert.Contains("3 received", item.ActivityText, StringComparison.Ordinal);
            Assert.Equal("PC recording device", MainViewModel.FormatOrderIntegrationDeviceType("pc"));
            Assert.Equal("Phone recording device", MainViewModel.FormatOrderIntegrationDeviceType("mobile"));
            Assert.Contains(
                "3 processed",
                MainViewModel.FormatOrderIntegrationDeviceActivity(DateTimeOffset.UtcNow, 3),
                StringComparison.Ordinal);
            Assert.Equal(
                "No integration data yet",
                MainViewModel.FormatOrderIntegrationDeviceActivity(null, 0));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void SettingsAboutRepositoryActionsUseSingleRow()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml");
        string xaml = File.ReadAllText(path);
        int start = xaml.IndexOf("Text=\"项目仓库\"", StringComparison.Ordinal);
        int end = xaml.IndexOf("Text=\"导出诊断日志\"", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "无法定位关于页项目仓库操作区");
        string section = xaml[start..end];

        Assert.Contains("<ColumnDefinition Width=\"300\"/>", section, StringComparison.Ordinal);
        Assert.Contains("<StackPanel Grid.Column=\"2\" Orientation=\"Horizontal\">", section, StringComparison.Ordinal);
        Assert.DoesNotContain("WrapPanel", section, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsAboutTitleUsesLocalizedProductBrand()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "SettingsWindow.xaml");
        string xaml = File.ReadAllText(path);

        Assert.Contains("Text=\"{loc:Localized App.ProductName}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"快递打包监控\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileConnectionActionsUseSingleRow()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "MobileConnectionWindow.xaml");
        string xaml = File.ReadAllText(path);

        Assert.DoesNotContain("<WrapPanel Grid.Row=\"4\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(
            xaml,
            "<StackPanel Grid.Row=\"4\"\\s+Orientation=\"Horizontal\"",
            RegexOptions.Singleline).Count);
        Assert.True(Regex.IsMatch(
            xaml,
            "<StackPanel Grid.Row=\"2\"\\s+Orientation=\"Horizontal\"",
            RegexOptions.Singleline));
    }

    [Fact]
    public void ToastLiterals_AllHaveEnglishResources()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        string[] sourceFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string[] values = sourceFiles
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    "ShowToast\\(\\s*\\$?\"([^\"\\r\\n]*[\\p{IsCJKUnifiedIdeographs}][^\"\\r\\n]*)\"")
                .Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        CultureInfo original = CultureInfo.CurrentUICulture;
        string[] missing;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            missing = values
                .Where(value => Regex.IsMatch(
                    AppLanguage.Translate(SampleInterpolation(value)),
                    "[\\p{IsCJKUnifiedIdeographs}]"))
                .ToArray();
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }

        Assert.True(missing.Length == 0, "Missing English toast resources: " + string.Join(" | ", missing));
    }

    /// <summary>
    /// 代码直接给 Text/Content/Header/ToolTip 赋的中文也要有资源。
    /// XAML 静态文案守卫看不到这些状态文案，分页、检查更新等运行时文字过去就是这样漏掉的。
    /// </summary>
    [Fact]
    public void CodeAssignedUiText_AllHaveEnglishResources()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        string[] sourceFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string[] values = sourceFiles
            // 赋值右侧可能跨行，也可能是三元表达式或拼接；截到语句结束再统一提取字面量。
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    "(?:\\.|(?<=[\\{,])\\s*)(?:Text|Content|Header|ToolTip)\\s*=\\s*(?<expr>.*?);",
                    RegexOptions.Singleline)
                .Where(match => !match.Groups["expr"].Value.Contains("\\n", StringComparison.Ordinal)
                    && !match.Groups["expr"].Value.Contains("\\r", StringComparison.Ordinal))
                .SelectMany(match => Regex.Matches(
                        match.Groups["expr"].Value,
                        "\"((?:\\\\.|[^\"\\\\])*)\"")
                    .Select(literal => literal.Groups[1].Value)))
            .Select(literal => Regex.Unescape(literal))
            .Where(value => Regex.IsMatch(value, "[\\p{IsCJKUnifiedIdeographs}]"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        CultureInfo original = CultureInfo.CurrentUICulture;
        string[] missing;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            missing = values
                .Where(value => Regex.IsMatch(
                    AppLanguage.TranslateRuntime(SampleInterpolation(value)),
                    "[\\p{IsCJKUnifiedIdeographs}]"))
                .ToArray();
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }

        Assert.True(
            missing.Length == 0,
            "Missing English resources for code-assigned UI text: " + string.Join(" | ", missing));
    }

    [Fact]
    public void AlertRequestMessages_AllHaveEnglishResources()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        string[] sourceFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string[] values = sourceFiles
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    "new AlertRequest\\s*\\{[^}]*Message\\s*=\\s*\\$?\"([^\"\\r\\n]*[\\p{IsCJKUnifiedIdeographs}][^\"\\r\\n]*)\"",
                    RegexOptions.Singleline)
                .Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        CultureInfo original = CultureInfo.CurrentUICulture;
        string[] missing;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            missing = values
                .Where(value => Regex.IsMatch(
                    AppLanguage.Translate(SampleInterpolation(value)),
                    "[\\p{IsCJKUnifiedIdeographs}]"))
                .ToArray();
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }

        Assert.True(missing.Length == 0, "Missing English alert resources: " + string.Join(" | ", missing));
    }

    [Fact]
    public void ScannerAlertLiterals_AllHaveEnglishResources()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        string[] sourceFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string[] values = sourceFiles
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    "PublishScannerAlert\\(\\s*\\$?\"[^\"\\r\\n]*\"\\s*,\\s*\\$?\"([^\"\\r\\n]*[\\p{IsCJKUnifiedIdeographs}][^\"\\r\\n]*)\"",
                    RegexOptions.Singleline)
                .Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        CultureInfo original = CultureInfo.CurrentUICulture;
        string[] missing;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            missing = values
                .Where(value => Regex.IsMatch(
                    AppLanguage.Translate(SampleInterpolation(value)),
                    "[\\p{IsCJKUnifiedIdeographs}]"))
                .ToArray();
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }

        Assert.True(missing.Length == 0, "Missing English scanner alert resources: " + string.Join(" | ", missing));
    }

    /// <summary>
    /// 下拉框的 DisplayMemberPath 最终也可能走 ContentPresenter；这里锁住这条路径，
    /// 防止再次出现“资源有了、弹层和选中值仍是中文”的情况。
    /// </summary>
    [Fact]
    public void ContentPresenter_StringContentUsesRuntimeLocalization()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            CultureInfo original = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
                var presenter = new ContentPresenter { Content = "CPU 软编码" };

                WpfLocalization.LocalizeTree(presenter);

                Assert.Equal("CPU software encoding", presenter.Content);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                CultureInfo.CurrentUICulture = original;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }

    /// <summary>
    /// 选项显示名必须由稳定的 Value/资源键生成，不能再把中文写进 GpuEncoderOption.DisplayName。
    /// </summary>
    [Fact]
    public void GpuEncoderOptionDisplayNames_DoNotUseChineseLiterals()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "src", "ExpressPackingMonitoring");
        string[] sourceFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string[] violations = sourceFiles
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    "new GpuEncoderOption\\s*\\{[^}]*DisplayName\\s*=\\s*\"[^\"]*[\\p{IsCJKUnifiedIdeographs}][^\"]*\"",
                    RegexOptions.Singleline)
                .Select(match => $"{Path.GetFileName(path)}: {match.Value}"))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "GpuEncoderOption.DisplayName 必须使用 GpuEncoderOption.BuildDisplayName: " + string.Join(" | ", violations));
    }

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(startPath));
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }

    private static string[] ReadKeys(string path) =>
        Regex.Matches(File.ReadAllText(path), "<data name=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string ResolveLocalizationKey(string value)
    {
        Match localized = Regex.Match(value, "^\\{loc:Localized\\s+(.+)\\}$");
        return localized.Success ? localized.Groups[1].Value : value;
    }

    private static string SampleInterpolation(string value) =>
        Regex.Replace(value, "\\{[^{}]+\\}", "1");

    [Fact]
    public void TextBlockLocalization_DistinguishesTextPropertyFromExplicitInlines()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var ordinaryText = new TextBlock { Text = "摄像头" };
                var inlineText = new TextBlock();
                inlineText.Inlines.Add(new Run("今日:"));
                inlineText.Inlines.Add(new Run("0"));
                var businessDataContainer = new StackPanel();
                WpfLocalization.SetAutoLocalize(businessDataContainer, false);
                var businessDataText = new TextBlock { Text = "开始录制" };
                businessDataContainer.Children.Add(businessDataText);

                Assert.True(WpfLocalization.ShouldLocalizeTextProperty(ordinaryText));
                Assert.False(WpfLocalization.ShouldLocalizeTextProperty(inlineText));
                Assert.False(WpfLocalization.ShouldLocalizeTextProperty(businessDataText));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }

    /// <summary>
    /// 绑定到 ViewModel 的文本和 ToolTip 会在窗口加载后变化，必须仍然走本地化。
    /// </summary>
    [Fact]
    public void RuntimeBindings_TranslateTextAndToolTips()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            CultureInfo original = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
                var source = new RuntimeTextSource
                {
                    Text = "副摄像头 1",
                    ToolTip = "快递单号：SF001"
                };
                var text = new TextBlock();
                text.SetBinding(TextBlock.TextProperty, new Binding(nameof(source.Text)) { Source = source });
                var button = new Button();
                button.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(source.ToolTip)) { Source = source });
                var root = new StackPanel();
                root.Children.Add(text);
                root.Children.Add(button);

                WpfLocalization.LocalizeTree(root);

                Assert.Equal("Sub camera 1", text.Text);
                Assert.Equal("Tracking number: SF001", button.ToolTip);

                source.Text = "已连接 PC-A";
                source.ToolTip = "已连接 PC-A";
                WpfLocalization.LocalizeToolTip(button);
                Assert.Equal("Connected to PC-A", text.Text);
                Assert.Equal("Connected to PC-A", button.ToolTip);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                CultureInfo.CurrentUICulture = original;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }

    private sealed class RuntimeTextSource : System.ComponentModel.INotifyPropertyChanged
    {
        private string _text = "";
        private string _toolTip = "";

        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Text)));
            }
        }

        public string ToolTip
        {
            get => _toolTip;
            set
            {
                _toolTip = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ToolTip)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

}
