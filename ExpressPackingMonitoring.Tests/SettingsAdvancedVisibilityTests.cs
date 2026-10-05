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
    public void OrderIntegrationDeviceActivityDescribesProcessingWithoutAmbiguousDirection()
    {
        string activity = MainViewModel.FormatOrderIntegrationDeviceActivity(DateTimeOffset.UtcNow, 3);

        Assert.Contains("已处理 3 条联动数据", activity, StringComparison.Ordinal);
        Assert.DoesNotContain("收到", activity, StringComparison.Ordinal);
        Assert.Equal("暂无联动数据", MainViewModel.FormatOrderIntegrationDeviceActivity(null, 0));
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
