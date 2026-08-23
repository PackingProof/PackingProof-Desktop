using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.UI;
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
            "视频编码格式", "硬件加速", "画面流畅度", "画质与文件大小",
            "放大前等待", "放大停留时间", "平滑过渡", "过渡时长",
            "静止超时", "提前提醒时间", "最大时长", "太短的视频自动丢弃", "空闲超时", "高峰时段不休眠",
            "最小文件大小", "显示已清理记录",
            "语音引擎", "语速", "普通播报声线", "警告播报声线", "在线普通声音", "在线警告声音", "语音预览", "断句关键词",
            "网页访问端口", "网页临时缓存上限", "调试日志",
            "识别频率", "识别区域宽度", "识别区域高度", "左右偏移", "上下偏移",
            "同码消失时间", "识别确认时间", "识别确认次数", "单号判断规则", "扫码间隔保护",
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
                && (string?)button.Attribute("Style") == "{StaticResource DangerButtonStyle}");
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
        Assert.Contains(columns, column => (string?)column.Attribute("Header") == "权限与绑定");
        Assert.Contains(columns, column => (string?)column.Attribute("Header") == "管理");
        Assert.Contains(grid.Descendants(Presentation + "Button"), button =>
            (string?)button.Attribute("Content") == "轮换凭据"
            && (string?)button.Attribute("Click") == "RotateExtensionCredential_Click");
        Assert.Contains(grid.Descendants(Presentation + "Button"), button =>
            (string?)button.Attribute("Content") == "撤销"
            && (string?)button.Attribute("Style") == "{StaticResource DangerButtonStyle}");
        Assert.Contains(
            grid.Descendants(Presentation + "DataTrigger"),
            trigger => (string?)trigger.Attribute("Binding") == "{Binding Online}"
                && (string?)trigger.Attribute("Value") == "True");
        Assert.Contains(
            grid.Descendants(Presentation + "TextBlock"),
            text => (string?)text.Attribute("Text") == "{Binding ActivityText}");
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
            grid.Descendants(Presentation + "DataGridTextColumn"),
            column => (string?)column.Attribute("Header") == "最近活动"
                && (string?)column.Attribute("Binding") == "{Binding ActivityText}");
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
    public void ConfirmationCountRow_UsesRegularAdvancedSpacing()
    {
        XDocument document = LoadSettingsXaml();
        XElement label = FindLabel(document, "识别确认次数");
        XElement row = Assert.Single(label.Ancestors(Presentation + "Grid").Take(1));

        Assert.Contains("AdvancedSettingRowStyle", row.ToString(SaveOptions.DisableFormatting));
        Assert.DoesNotContain(
            "AdvancedSettingRowLastStyle",
            row.ToString(SaveOptions.DisableFormatting));
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
    [InlineData("放大倍数")]
    [InlineData("录像网页访问密钥")]
    public void CommonSettings_RemainVisibleWhenAdvancedSettingsAreHidden(string label)
    {
        XElement labelElement = FindLabel(LoadSettingsXaml(), label);

        Assert.False(IsControlledByAdvancedToggle(labelElement), $"{label} 不应受高级设置开关控制");
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

    private static XElement FindLabel(XDocument document, string label)
    {
        return Assert.Single(
            document.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == label);
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
