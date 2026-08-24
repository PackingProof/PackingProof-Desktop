using System.Xml.Linq;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class SettingsCapabilityVisibilityTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("StatisticsButton", "数据")]
    [InlineData("PlaybackButton", "回放")]
    [InlineData("SettingsButton", "设置")]
    [InlineData("OpenWebButton", "网页回放")]
    public void NoCameraWindowExposesSharedWindowAndWebPlaybackEntries(string name, string content)
    {
        XElement button = Assert.Single(
            LoadXaml("Workstations", "PrintWorkstationWindow.xaml")
                .Descendants(Presentation + "Button"),
            element => (string?)element.Attribute(Xaml + "Name") == name);

        Assert.Contains(
            button.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == content);
    }

    [Theory]
    [InlineData("面单放大", "Capabilities.CanRecordPcVideo")]
    [InlineData("录像设置", "Capabilities.CanRecordPcVideo")]
    [InlineData("声音与播报", "Capabilities.IsRecordingDevice")]
    [InlineData("扫码与识别", "Capabilities.CanUseScanner")]
    public void CameraOnlyTabsAreControlledByCapabilities(string header, string capability)
    {
        XElement tab = Assert.Single(
            LoadSettingsXaml().Descendants(Presentation + "TabItem"),
            element => (string?)element.Attribute("Header") == header);

        Assert.Contains(capability, (string?)tab.Attribute("Visibility") ?? string.Empty);
    }

    [Theory]
    [InlineData("摄像头", "Capabilities.CanUseCamera")]
    [InlineData("麦克风", "Capabilities.CanRecordAudio")]
    [InlineData("录制声音", "Capabilities.CanRecordAudio")]
    [InlineData("视频编码器", "Capabilities.CanRecordPcVideo")]
    [InlineData("启用录像水印", "Capabilities.CanRecordPcVideo")]
    [InlineData("配置向导", "Capabilities.CanUseCamera")]
    [InlineData("订单备注播报", "Capabilities.IsRecordingDevice")]
    public void CameraOnlyRowsOrCardsAreControlledByCapabilities(string label, string capability)
    {
        XElement labelElement = Assert.Single(
            LoadSettingsXaml().Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == label);

        Assert.Contains(
            labelElement.AncestorsAndSelf()
                .Select(element => (string?)element.Attribute("Visibility"))
                .Where(value => value != null),
            value => value!.Contains(capability, StringComparison.Ordinal));
    }

    [Fact]
    public void SettingsTabsFollowTheUserWorkflow()
    {
        string?[] headers = LoadSettingsXaml()
            .Descendants(Presentation + "TabItem")
            .Select(element => (string?)element.Attribute("Header"))
            .ToArray();

        Assert.Equal(
            new string?[]
            {
                "设备与外观",
                "扫码与识别",
                "面单放大",
                "存储与备份",
                "存储与备份",
                "录像设置",
                "声音与播报",
                "局域网与网页",
                "扩展与联动",
                "高级设置",
                "关于"
            },
            headers);
    }

    [Fact]
    public void EncoderSettingsUseOneActualEncoderSelectorWithoutLegacyFormatOrHardwareSelectors()
    {
        XDocument document = LoadSettingsXaml();

        Assert.Single(
            document.Descendants(Presentation + "ComboBox"),
            element => (string?)element.Attribute(Xaml + "Name") == "VideoEncoderComboBox");
        Assert.DoesNotContain(
            document.Descendants(Presentation + "ComboBox"),
            element => (string?)element.Attribute(Xaml + "Name") is "VideoCodecComboBox" or "GpuEncoderComboBox");
        Assert.DoesNotContain(
            document.Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") is "视频编码格式" or "硬件加速");
    }

    [Theory]
    [InlineData("麦克风", "设备与外观")]
    [InlineData("录制声音", "录像设置")]
    [InlineData("视频编码器", "录像设置")]
    [InlineData("接收第三方水印", "录像设置")]
    [InlineData("订单备注播报", "声音与播报")]
    [InlineData("播报商品件数", "声音与播报")]
    [InlineData("网页访问端口", "局域网与网页")]
    [InlineData("安装订单联动", "扩展与联动")]
    [InlineData("自定义订单脚本", "扩展与联动")]
    public void SettingsAreGroupedByUserPurpose(string label, string expectedTab)
    {
        XElement labelElement = Assert.Single(
            LoadSettingsXaml().Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == label);
        XElement tab = Assert.Single(labelElement.Ancestors(Presentation + "TabItem"));

        Assert.Equal(expectedTab, (string?)tab.Attribute("Header"));
    }

    [Theory]
    [InlineData("电脑用途")]
    [InlineData("关闭窗口时")]
    [InlineData("界面语言")]
    [InlineData("外观主题")]
    [InlineData("开机自启动")]
    [InlineData("自动检查更新")]
    public void SharedSettingsAreNotHiddenByWorkstationCapabilities(string label)
    {
        XElement labelElement = Assert.Single(
            LoadSettingsXaml().Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == label);

        Assert.DoesNotContain(
            labelElement.AncestorsAndSelf()
                .Select(element => (string?)element.Attribute("Visibility"))
                .Where(value => value != null),
            value => value!.Contains("Capabilities.", StringComparison.Ordinal));
    }

    [Fact]
    public void AdvancedSettingsEntry_IsNotHiddenByWorkstationCapabilities()
    {
        XElement toggle = Assert.Single(
            LoadSettingsXaml().Descendants(Presentation + "ToggleButton"),
            element => (string?)element.Attribute(Xaml + "Name") == "AdvancedModeButton");

        Assert.DoesNotContain(
            toggle.AncestorsAndSelf()
                .Select(element => (string?)element.Attribute("Visibility"))
                .Where(value => value != null),
            value => value!.Contains("Capabilities.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("网页访问端口")]
    [InlineData("录像网页访问密钥")]
    [InlineData("调试日志")]
    public void HostWebSettingsAreControlledByWebServerCapability(string label)
    {
        XElement labelElement = Assert.Single(
            LoadSettingsXaml().Descendants(Presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == label);

        Assert.Contains(
            labelElement.AncestorsAndSelf()
                .Select(element => (string?)element.Attribute("Visibility"))
                .Where(value => value != null),
            value => value!.Contains("Capabilities.CanRunWebServer", StringComparison.Ordinal));
    }

    private static XDocument LoadSettingsXaml()
        => LoadXaml("UI", "SettingsWindow.xaml");

    private static XDocument LoadXaml(string directoryName, string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "ExpressPackingMonitoring",
                directoryName,
                fileName);
            if (File.Exists(candidate))
                return XDocument.Load(candidate);

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"找不到 {fileName}");
    }
}
