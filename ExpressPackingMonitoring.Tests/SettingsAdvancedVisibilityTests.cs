using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.ViewModels;
using System.Xml.Linq;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class SettingsAdvancedVisibilityTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void MkvMigrationSummary_ReportsActualOutcomesWithoutFailureLabels()
    {
        var result = new MkvBatchConversionResult
        {
            SuccessCount = 5,
            FailureCount = 2,
            SkippedCount = 3,
            SuppressedCount = 2
        };

        string summary = SettingsWindow.FormatMkvMigrationSummary(result);

        Assert.Equal(
            "处理完成：已生成兼容 MP4 5 个；未生成 2 个，原始录像已保留",
            summary);
        Assert.DoesNotContain("失败", summary);
        Assert.DoesNotContain("长期", summary);
        Assert.DoesNotContain("待核对", summary);
    }

    [Fact]
    public void MkvMigrationSummary_ReportsNoWorkWithoutMissingDatabaseRecords()
    {
        var result = new MkvBatchConversionResult { SkippedCount = 176 };

        Assert.Equal(
            "处理完成：没有需要转换的 MKV",
            SettingsWindow.FormatMkvMigrationSummary(result));
    }

    [Fact]
    public void AdvancedSettingsToggle_IsPersistedAndControlsProfessionalRows()
    {
        XDocument document = LoadSettingsXaml();
        XElement toggle = Assert.Single(
            document.Descendants(Presentation + "ToggleButton"),
            element => (string?)element.Attribute(Xaml + "Name") == "AdvancedModeButton");

        Assert.Contains(
            "Config.ShowAdvancedSettings",
            (string?)toggle.Attribute("IsChecked") ?? string.Empty);
        Assert.Null(toggle.Attribute("AutomationProperties.Name"));
        Assert.Empty(toggle.Descendants(Presentation + "Path"));

        XElement text = Assert.Single(toggle.Descendants(Presentation + "TextBlock"));
        Assert.Contains("AdvancedModeTextConverter", (string?)text.Attribute("Text") ?? string.Empty);
        Assert.Contains("Mode=OneWay", (string?)text.Attribute("Text") ?? string.Empty);

        XElement style = Assert.Single(
            document.Descendants(Presentation + "Style"),
            element => (string?)element.Attribute(Xaml + "Key") == "AdvancedModeButtonStyle");
        Assert.Contains(
            style.Elements(Presentation + "Setter"),
            element => (string?)element.Attribute("Property") == "AutomationProperties.Name"
                && ((string?)element.Attribute("Value"))?.Contains("AdvancedModeTextConverter", StringComparison.Ordinal) == true
                && ((string?)element.Attribute("Value"))?.Contains("Mode=OneWay", StringComparison.Ordinal) == true);
        Assert.Contains(
            style.Elements(Presentation + "Setter"),
            element => (string?)element.Attribute("Property") == "HorizontalContentAlignment"
                && (string?)element.Attribute("Value") == "Center");
        Assert.Contains(
            style.Elements(Presentation + "Setter"),
            element => (string?)element.Attribute("Property") == "BorderBrush"
                && (string?)element.Attribute("Value") == "{DynamicResource BorderStrong}");
        Assert.Contains(
            style.Elements(Presentation + "Setter"),
            element => (string?)element.Attribute("Property") == "BorderThickness"
                && (string?)element.Attribute("Value") == "1.5");

        XElement template = Assert.Single(style.Descendants(Presentation + "ControlTemplate"));
        XElement checkedTrigger = Assert.Single(
            template.Descendants(Presentation + "Trigger"),
            element => (string?)element.Attribute("Property") == "IsChecked"
                && (string?)element.Attribute("Value") == "True");
        Assert.Contains(checkedTrigger.Descendants(Presentation + "Setter"),
            element => (string?)element.Attribute("Property") == "Background"
                && (string?)element.Attribute("Value") == "{DynamicResource AccentBlue}");
        Assert.Contains(checkedTrigger.Descendants(Presentation + "Setter"),
            element => (string?)element.Attribute("Property") == "Foreground"
                && (string?)element.Attribute("Value") == "{StaticResource TextOnAccent}");
        Assert.Contains(template.Descendants(Presentation + "MultiTrigger").Descendants(Presentation + "Setter"),
            element => (string?)element.Attribute("Property") == "Background"
                && (string?)element.Attribute("Value") == "{DynamicResource AccentBlueDark}");
        Assert.DoesNotContain(
            document.Descendants(Presentation + "CheckBox"),
            element => (string?)element.Attribute(Xaml + "Name") == "ShowAdvancedSettingsCheckBox");

        XElement directAacToggle = Assert.Single(
            document.Descendants(Presentation + "CheckBox"),
            element => (string?)element.Attribute(Xaml + "Name") == "DirectAacRecordingCheckBox");
        Assert.Contains("Config.EnableDirectAacRecording", (string?)directAacToggle.Attribute("IsChecked") ?? string.Empty);
        Assert.Equal("DirectAacRecordingCheckBox_Checked", (string?)directAacToggle.Attribute("Checked"));
        Assert.Equal(
            "DirectAacRecordingCheckBox_PreviewMouseLeftButtonDown",
            (string?)directAacToggle.Attribute("PreviewMouseLeftButtonDown"));
        Assert.Equal(
            "DirectAacRecordingCheckBox_PreviewKeyDown",
            (string?)directAacToggle.Attribute("PreviewKeyDown"));

        string[] hiddenLabels =
        [
            // 分辨率与帧率已随"主摄像头"卡片一起移到常用项，不再属于高级设置
            "视频编码格式", "硬件加速", "画质与文件大小",
            // 放大前等待/放大停留时间也已移到常用项：面单放大是现场高频功能
            "平滑过渡", "过渡时长",
            // 自动停录/限长/摄像头休眠的时长都跟着各自的常用开关一起放出来了，
            // 只留下真正需要权衡的：太短丢弃阈值、高峰时段例外
            "太短的视频自动丢弃", "高峰时段不休眠",
            "最小文件大小", "显示已清理记录",
            // 语音引擎与它带的声线/在线声音仍属高级（切换引擎有联网/本地模型依赖），只把语速放出来
            "语音引擎", "普通播报声线", "警告播报声线", "在线普通声音", "在线警告声音", "语音预览", "断句关键词",
            "网页访问端口", "网页临时缓存上限", "调试日志",
            // 识别框的宽高与偏移已在主界面直接拖动调整，设置页不再保留这四个滑块
            "识别频率",
            "同码消失时间", "单号判断规则", "扫码间隔保护",
            "扫码最小长度", "自动提交停顿", "平均输入间隔", "单字符间隔上限",
            "音频直接写入 MKV", "声音同步微调"
        ];

        foreach (string label in hiddenLabels)
        {
            XElement labelElement = FindLabel(document, label);
            Assert.True(IsControlledByAdvancedToggle(labelElement), $"{label} 未接入高级设置开关");
        }
    }

    [Fact]
    public void AboutPage_ShowsOneWayCommitSummaryAndFullCommitToolTip()
    {
        XDocument document = LoadSettingsXaml();
        XElement commit = Assert.Single(
            document.Descendants(Presentation + "TextBlock"),
            element => ((string?)element.Attribute("Text"))?.Contains("AppCommitText", StringComparison.Ordinal) == true);

        Assert.Contains("Mode=OneWay", (string?)commit.Attribute("Text") ?? string.Empty);
        Assert.Contains("AppCommitToolTip", (string?)commit.Attribute("ToolTip") ?? string.Empty);
        Assert.Contains("Mode=OneWay", (string?)commit.Attribute("ToolTip") ?? string.Empty);
    }

    [Fact]
    public void CustomUserscripts_UseStorageGridWithEmbeddedManagementAction()
    {
        XDocument document = LoadSettingsXaml();
        XElement grid = Assert.Single(
            document.Descendants(Presentation + "DataGrid"),
            element => ((string?)element.Attribute("ItemsSource"))?.Contains(
                "CustomUserscripts",
                StringComparison.Ordinal) == true);

        XElement[] columns = grid.Descendants(Presentation + "DataGridTemplateColumn").ToArray();
        Assert.Contains(columns, column => (string?)column.Attribute("Header") == "已导入脚本");
        Assert.Contains(columns, column => (string?)column.Attribute("Header") == "管理");
        Assert.Contains(
            grid.Descendants(Presentation + "Button"),
            button => (string?)button.Attribute("Content") == "删除"
                && (string?)button.Attribute("Style") == "{StaticResource DeleteUserscriptButtonStyle}");
        Assert.Null(grid.Attribute("IsEnabled"));
        XElement managementSection = grid.Ancestors(Presentation + "Grid").First();
        Assert.Null(managementSection.Attribute("Visibility"));
        XElement developmentHint = Assert.Single(
            document.Descendants(Presentation + "TextBlock"),
            text => (string?)text.Attribute("Text") == "开发第三方脚本时，可参考官方开发手册或 API 扩展文档");
        Assert.Null(developmentHint.Ancestors(Presentation + "Grid").First().Attribute("Style"));
    }

    [Fact]
    public void ExtensionAuthorizations_UseManagementGridWithSafeActions()
    {
        XDocument document = LoadSettingsXaml();
        XElement grid = Assert.Single(
            document.Descendants(Presentation + "DataGrid"),
            element => ((string?)element.Attribute("ItemsSource"))?.Contains(
                "ExtensionAuthorizations",
                StringComparison.Ordinal) == true);

        XElement[] columns = grid.Descendants(Presentation + "DataGridTemplateColumn").ToArray();
        Assert.Contains(columns, column => (string?)column.Attribute("Header") == "扩展");
        Assert.Contains(columns, column =>
            (string?)column.Attribute("Header") == "权限与绑定"
            && (string?)column.Attribute("Width") == "1.5*");
        Assert.Contains(columns, column => (string?)column.Attribute("Header") == "管理");
        Assert.DoesNotContain(grid.Descendants(Presentation + "Button"), button =>
            (string?)button.Attribute("Content") == "轮换凭据");
        Assert.Contains(grid.Descendants(Presentation + "Button"), button =>
            (string?)button.Attribute("Content") == "撤销"
            && (string?)button.Attribute("Style") == "{StaticResource DeleteUserscriptButtonStyle}"
            && (string?)button.Attribute("Width") == "72"
            && (string?)button.Attribute("HorizontalAlignment") == "Center");
        XElement displayName = Assert.Single(
            grid.Descendants(Presentation + "TextBlock"),
            text => (string?)text.Attribute("Text") == "{Binding DisplayName}");
        XElement identityPanel = Assert.IsType<XElement>(displayName.Parent?.Parent);
        Assert.Equal("Horizontal", (string?)identityPanel.Attribute("Orientation"));
        Assert.Equal("Left", (string?)identityPanel.Attribute("HorizontalAlignment"));
        Assert.Equal(Presentation + "Ellipse", identityPanel.Elements().First().Name);
        Assert.Contains(
            grid.Descendants(Presentation + "DataTrigger"),
            trigger => (string?)trigger.Attribute("Binding") == "{Binding Online}"
                && (string?)trigger.Attribute("Value") == "True");
        Assert.Contains(
            grid.Descendants(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Fill"
                && (string?)setter.Attribute("Value") == "{DynamicResource TextSecondary}");
        Assert.DoesNotContain("TextTertiary", grid.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            grid.Descendants(Presentation + "TextBlock"),
            text => (string?)text.Attribute("Text") == "{Binding ActivityText}");
        string code = LoadSettingsCode();
        int timerStart = code.IndexOf("private void IntegrationStatusTimer_Tick", StringComparison.Ordinal);
        int nextMethod = code.IndexOf("private void RefreshOrderIntegrationDevices", timerStart, StringComparison.Ordinal);
        Assert.True(timerStart >= 0 && nextMethod > timerStart);
        string timerBody = code[timerStart..nextMethod];
        Assert.Contains("RefreshExtensionAuthorizations();", timerBody, StringComparison.Ordinal);
        Assert.Contains("RefreshOrderIntegrationDevices();", timerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("RotateExtensionCredential_Click", code, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderIntegrationDevices_UseOnlineIndicatorAndRecentActivityGrid()
    {
        XDocument document = LoadSettingsXaml();
        XElement grid = Assert.Single(
            document.Descendants(Presentation + "DataGrid"),
            element => ((string?)element.Attribute("ItemsSource"))?.Contains(
                "OrderIntegrationDevices",
                StringComparison.Ordinal) == true);

        Assert.Contains(
            grid.Descendants(Presentation + "DataTrigger"),
            trigger => (string?)trigger.Attribute("Binding") == "{Binding Online}"
                && (string?)trigger.Attribute("Value") == "True");
        Assert.Contains(
            grid.Descendants(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Fill"
                && (string?)setter.Attribute("Value") == "{DynamicResource TextSecondary}");
        Assert.DoesNotContain("TextTertiary", grid.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            grid.Descendants(Presentation + "DataGridTemplateColumn"),
            column => (string?)column.Attribute("Header") == "最近活动"
                && column.Descendants(Presentation + "TextBlock").Any(text =>
                    (string?)text.Attribute("Text") == "{Binding ActivityText}"
                    && (string?)text.Attribute("VerticalAlignment") == "Center"));
        Assert.Equal("13", (string?)grid.Attribute("FontSize"));
        Assert.Equal("Center", (string?)grid.Attribute("VerticalContentAlignment"));
    }

    [Fact]
    public void OrderIntegrationCardPlacesApiThenMarketThenInstallAndKeepsSidebarShortcut()
    {
        string xaml = LoadSettingsXaml().ToString(SaveOptions.DisableFormatting);
        int toggle = xaml.IndexOf("Text=\"启用扩展 API\"", StringComparison.Ordinal);
        int install = xaml.IndexOf("Text=\"安装订单联动\"", StringComparison.Ordinal);
        int market = xaml.IndexOf("Text=\"扩展市场\"", StringComparison.Ordinal);
        int devices = xaml.IndexOf("Text=\"订单联动设备\"", StringComparison.Ordinal);

        Assert.True(toggle >= 0 && market > toggle && install > market && devices > install);
        Assert.Contains("允许经过授权的 ERP、称重设备和其他扩展连接", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("关闭时不启动扩展任务服务", xaml, StringComparison.Ordinal);

        XDocument document = LoadSettingsXaml();
        XElement installLabel = Assert.Single(
            document.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "安装订单联动");
        XElement marketLabel = Assert.Single(
            document.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "扩展市场");
        XElement installCard = installLabel.Ancestors(Presentation + "Border")
            .First(element => (string?)element.Attribute("Style") == "{StaticResource SectionCardStyle}");
        XElement toggleLabel = Assert.Single(
            document.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "启用扩展 API");
        XElement toggleCard = toggleLabel.Ancestors(Presentation + "Border")
            .First(element => (string?)element.Attribute("Style") == "{StaticResource SectionCardStyle}");
        Assert.Same(installCard, toggleCard);
        Assert.Equal("扩展与联动", (string?)Assert.Single(marketLabel.Ancestors(Presentation + "TabItem")).Attribute("Header"));

        XElement[] marketButtons = document.Descendants(Presentation + "Button")
            .Where(element => (string?)element.Attribute("Content") == "扩展市场")
            .ToArray();
        Assert.Equal(2, marketButtons.Length);
        XElement marketButton = Assert.Single(
            marketButtons,
            element => element.Ancestors(Presentation + "TabItem").Any());
        XElement sidebarButton = Assert.Single(
            marketButtons,
            element => (string?)element.Attribute(Xaml + "Name") == "SidebarExtensionMarketButton");
        // 左栏分成"搜索 / 页签 / 底部按钮"三块，底部这一块里先高级模式、再扩展市场
        XElement sidebarActions = Assert.Single(
            sidebarButton.Ancestors(Presentation + "StackPanel"),
            element => (string?)element.Attribute("Grid.Row") == "4");
        Assert.DoesNotContain(sidebarButton.Ancestors(), element => element.Name == Presentation + "TabItem");
        Assert.Equal("OpenExtensionMarket_Click", (string?)sidebarButton.Attribute("Click"));
        XElement advancedButton = Assert.Single(
            document.Descendants(Presentation + "ToggleButton"),
            element => (string?)element.Attribute(Xaml + "Name") == "AdvancedModeButton");
        Assert.Same(sidebarActions, advancedButton.Parent);
        Assert.Empty(advancedButton.ElementsBeforeSelf());
        Assert.Empty(sidebarButton.ElementsAfterSelf());
        Assert.Null((string?)marketButton.Attribute("IsEnabled"));
        XElement marketRow = marketButton.Ancestors(Presentation + "Grid")
            .First(element => (string?)element.Attribute("Style") == "{StaticResource SettingRowStyle}");
        Assert.Null((string?)marketRow.Attribute("Visibility"));
        XElement customGrid = Assert.Single(
            document.Descendants(Presentation + "DataGrid"),
            element => ((string?)element.Attribute("ItemsSource"))?.Contains("CustomUserscripts", StringComparison.Ordinal) == true);
        Assert.Null((string?)customGrid.Attribute("IsEnabled"));
        XElement diagnosticButton = Assert.Single(
            document.Descendants(Presentation + "Button"),
            element => (string?)element.Attribute("Content") == "导出诊断日志");
        Assert.Equal("关于", (string?)Assert.Single(diagnosticButton.Ancestors(Presentation + "TabItem")).Attribute("Header"));
        Assert.DoesNotContain("点击反馈问题", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("安装自定义扩展", xaml, StringComparison.Ordinal);
        Assert.Contains("扩展 API 授权", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("已授权扩展", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderIntegrationDeviceActivityDescribesProcessingWithoutAmbiguousDirection()
    {
        string activity = MainViewModel.FormatOrderIntegrationDeviceActivity(DateTimeOffset.UtcNow, 3);

        Assert.Contains("已处理 3 条联动数据", activity, StringComparison.Ordinal);
        Assert.DoesNotContain("收到", activity, StringComparison.Ordinal);
        Assert.Equal("暂无联动数据", MainViewModel.FormatOrderIntegrationDeviceActivity(null, 0));
    }

    [Fact]
    public void SettingRows_HighlightOnlyWhileEnabledAndPurposeHintIsRemoved()
    {
        XDocument document = LoadSettingsXaml();
        XElement style = Assert.Single(
            document.Descendants(Presentation + "Style"),
            element => (string?)element.Attribute(Xaml + "Key") == "SettingRowStyle");

        XElement hoverTrigger = Assert.Single(style.Descendants(Presentation + "MultiTrigger"));
        Assert.Contains(
            hoverTrigger.Descendants(Presentation + "Condition"),
            element => (string?)element.Attribute("Property") == "IsMouseOver"
                && (string?)element.Attribute("Value") == "True");
        Assert.Contains(
            hoverTrigger.Descendants(Presentation + "Condition"),
            element => (string?)element.Attribute("Property") == "IsEnabled"
                && (string?)element.Attribute("Value") == "True");
        Assert.Contains(
            hoverTrigger.Descendants(Presentation + "Setter"),
            element => (string?)element.Attribute("Value") == "{DynamicResource ControlBackgroundHover}");

        Assert.DoesNotContain(
            document.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "更改用途后程序会自动重启，并切换到对应界面");
    }

    [Fact]
    public void ConfirmationCountRow_UsesTheSharedRowSpacing()
    {
        XDocument document = LoadSettingsXaml();
        XElement label = FindLabel(document, "识别确认次数");
        XElement row = Assert.Single(label.Ancestors(Presentation + "Grid").Take(1));

        // 行间距只有一套：不管这一行是常用项还是高级项，都用共享的行样式，
        // 不能自己写死 Margin/间距（谁最后露出来由卡片自己收尾）。
        string rowText = row.ToString(SaveOptions.DisableFormatting);
        Assert.True(
            rowText.Contains("AdvancedSettingRowStyle", StringComparison.Ordinal)
            || rowText.Contains("{StaticResource SettingRowStyle}", StringComparison.Ordinal),
            "识别确认次数必须使用共享的行样式");
    }

    [Fact]
    public void DirectMkvAudio_UsesStableCopyAndKeepsCompatibilityWarning()
    {
        XDocument document = LoadSettingsXaml();
        XElement label = FindLabel(document, "音频直接写入 MKV");
        XElement row = Assert.Single(label.Ancestors(Presentation + "Grid").Take(1));

        Assert.Contains(
            row.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "将声音直接编码到临时录像文件，减少临时文件和后处理");
        Assert.Contains("兼容", (string?)row.Attribute("ToolTip") ?? string.Empty);
        Assert.DoesNotContain("实验", row.ToString(SaveOptions.DisableFormatting));
    }

    [Fact]
    public void DirectMkvAudio_ConfirmsBeforeVisualToggleAndRollsBackBothStates()
    {
        string code = LoadSettingsCode();

        Assert.Contains("ApplyDirectAacRecordingChoice(checkBox, ConfirmDirectAacRecordingRisk())", code);
        Assert.Contains("Config.EnableDirectAacRecording = enabled", code);
        Assert.Contains("checkBox.SetCurrentValue(", code);
        Assert.Contains("ToggleButton.IsCheckedProperty", code);
        Assert.Contains("?.UpdateSource()", code);
        Assert.Contains(
            "实时封装时如果麦克风断开或音频设备异常被占用，可能导致 FFmpeg 录制中断，从而造成视频异常或录制失败",
            code);
    }

    [Theory]
    [InlineData("分辨率")]
    [InlineData("显示放大取景框")]
    [InlineData("放大前等待")]
    [InlineData("放大停留时间")]
    // 跟着各自的常用开关一起放出来的时长/灵敏度设置
    [InlineData("静止超时")]
    [InlineData("提前提醒时间")]
    [InlineData("最大时长")]
    [InlineData("空闲超时")]
    [InlineData("识别确认时间")]
    [InlineData("识别确认次数")]
    [InlineData("语速")]
    [InlineData("录像网页访问密钥")]
    public void CommonSettings_RemainVisibleWhenAdvancedSettingsAreHidden(string label)
    {
        // 同一行标签可能出现在多张卡上（例如主摄与每一路叠加画面各有一个"分辨率"）：
        // 只要没有任何一处受高级设置开关控制就算合格。
        foreach (XElement labelElement in FindLabels(LoadSettingsXaml(), label))
            Assert.False(IsControlledByAdvancedToggle(labelElement), $"{label} 不应受高级设置开关控制");
    }

    [Fact]
    public void SameBarcodeConfirmationSlider_KeepsTenSecondMaximum()
    {
        XDocument document = LoadSettingsXaml();
        XElement slider = Assert.Single(
            document.Descendants(Presentation + "Slider"),
            element => (string?)element.Attribute(Xaml + "Name") == "CameraSameBarcodeConfirmationSlider");

        Assert.Equal("10", (string?)slider.Attribute("Maximum"));
    }

    private static bool IsControlledByAdvancedToggle(XElement labelElement)
    {
        XElement? row = labelElement.Ancestors(Presentation + "Grid").FirstOrDefault();
        if (row?.ToString(SaveOptions.DisableFormatting).Contains(
                "AdvancedSetting",
                StringComparison.Ordinal) == true)
        {
            return true;
        }

        return labelElement
            .Ancestors(Presentation + "Border")
            .Select(border => (string?)border.Attribute("Visibility"))
            .Any(visibility => visibility?.Contains(
                "Config.ShowAdvancedSettings",
                StringComparison.Ordinal) == true);
    }

    private static XElement FindLabel(XDocument document, string label) =>
        Assert.Single(
            document.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == label);

    private static List<XElement> FindLabels(XDocument document, string label)
    {
        List<XElement> matches = document.Descendants(Presentation + "TextBlock")
            .Where(element => (string?)element.Attribute("Text") == label)
            .ToList();
        Assert.NotEmpty(matches);
        return matches;
    }

    private static XDocument LoadSettingsXaml()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "ExpressPackingMonitoring",
                "UI",
                "SettingsWindow.xaml");
            if (File.Exists(candidate))
            {
                return XDocument.Load(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("找不到 SettingsWindow.xaml");
    }
    private static string StyleKey(XElement element) =>
        ((string?)element.Attribute("Style") ?? "")
            .Replace("{StaticResource ", "", StringComparison.Ordinal)
            .Replace("}", "", StringComparison.Ordinal);

    private static string LoadSettingsCode()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "ExpressPackingMonitoring",
                "UI",
                "SettingsWindow.xaml.cs");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("找不到 SettingsWindow.xaml.cs");
    }
}
