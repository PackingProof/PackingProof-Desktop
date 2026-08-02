using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.UI;
using LibVLCSharp.Shared;
using System.Windows;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class PlaybackWindowTests
{
    [Fact]
    public void PlaybackLayout_UsesTwoRowPaginationAndNamedPlayerHost()
    {
        string xaml = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring", "UI", "PlaybackWindow.xaml"));

        Assert.Contains("x:Name=\"PageStatusText\"", xaml);
        Assert.Contains("<Grid Grid.Row=\"1\">", xaml);
        Assert.Contains("x:Name=\"BtnPreviousPage\" Grid.Column=\"0\"", xaml);
        Assert.Contains("x:Name=\"BtnNextPage\" Grid.Column=\"2\"", xaml);
        Assert.Contains("x:Name=\"PlayerHost\"", xaml);
    }

    [Theory]
    [InlineData(3, 3, false, true)]
    [InlineData(2, 3, false, false)]
    [InlineData(3, 3, true, false)]
    public void IsCurrentLoadRequest_AcceptsOnlyLatestOpenWindowRequest(
        int requestVersion,
        int currentVersion,
        bool isClosing,
        bool expected)
    {
        Assert.Equal(expected, PlaybackWindow.IsCurrentLoadRequest(requestVersion, currentVersion, isClosing));
    }

    [Fact]
    public void GetOrderDisplayName_PrefersTrackingNumber()
    {
        string result = PlaybackWindow.GetOrderDisplayName(
            "YT123456789012",
            "ORDER-OLD",
            "FILE-NAME_20260723_发货.mp4");

        Assert.Equal("YT123456789012", result);
    }

    [Fact]
    public void GetOrderDisplayName_FallsBackToOrderId()
    {
        string result = PlaybackWindow.GetOrderDisplayName(
            "",
            "SF123456789012",
            "FILE-NAME_20260723_发货.mp4");

        Assert.Equal("SF123456789012", result);
    }

    [Theory]
    [InlineData("JD123456789012_20260723_120000_发货.mp4", "JD123456789012")]
    [InlineData("YT123456789012.mkv", "YT123456789012")]
    [InlineData("", "未识别面单")]
    public void GetOrderDisplayName_ExtractsFileSystemFallback(string fileName, string expected)
    {
        Assert.Equal(expected, PlaybackWindow.GetOrderDisplayName("", "", fileName));
    }

    [Theory]
    [InlineData("external", "android-1234567890a1b2c3", "手机1", "来源：手机1")]
    [InlineData("EXTERNAL", "", "", "来源：手机设备")]
    [InlineData("external", "", "一号打包手机", "来源：一号打包手机")]
    [InlineData("pc", "pc-1", "一号电脑", "来源：电脑")]
    [InlineData("", "", "", "来源：电脑")]
    public void GetSourceDisplay_UsesBackupDeviceIdentity(
        string sourceType,
        string sourceDeviceId,
        string sourceDeviceName,
        string expected)
    {
        Assert.Equal(
            expected,
            PlaybackWindow.GetSourceDisplay(sourceType, sourceDeviceId, sourceDeviceName));
    }

    [Theory]
    [InlineData("external", "APP 备份", "")]
    [InlineData("external", "APP备份", "")]
    [InlineData("external", "上传完成", "上传完成")]
    [InlineData("pc", "扫码枪停止", "扫码枪停止")]
    public void GetStopReasonDisplay_HidesDuplicatedBackupLabel(
        string sourceType,
        string stopReason,
        string expected)
    {
        Assert.Equal(expected, PlaybackWindow.GetStopReasonDisplay(sourceType, stopReason));
    }

    [Fact]
    public void GetVideoDisplayAspect_AccountsForSampleAspectAndQuarterTurn()
    {
        Assert.Equal(
            16.0 / 9.0,
            PlaybackWindow.GetVideoDisplayAspect(1920, 1080, 1, 1, VideoOrientation.TopLeft)!.Value,
            precision: 6);
        Assert.Equal(
            9.0 / 16.0,
            PlaybackWindow.GetVideoDisplayAspect(1920, 1080, 1, 1, VideoOrientation.RightTop)!.Value,
            precision: 6);
        Assert.Equal(
            4.0 / 3.0,
            PlaybackWindow.GetVideoDisplayAspect(720, 576, 16, 15, VideoOrientation.TopLeft)!.Value,
            precision: 6);
        Assert.Null(PlaybackWindow.GetVideoDisplayAspect(0, 1080, 1, 1, VideoOrientation.TopLeft));
    }

    [Fact]
    public void CalculateAdaptiveWindowBounds_FitsPortraitWithoutCroppingAndStaysOnScreen()
    {
        Rect result = PlaybackWindow.CalculateAdaptiveWindowBounds(
            9.0 / 16.0,
            new Rect(0, 0, 1920, 1080),
            new Rect(500, 180, 1100, 700),
            horizontalChrome: 365,
            verticalChrome: 130,
            currentPlayerHeight: 570);

        Assert.Equal(685.625, result.Width, precision: 3);
        Assert.Equal(700, result.Height, precision: 3);
        Assert.InRange(result.Left, 16, 1920 - 16 - result.Width);
        Assert.InRange(result.Top, 16, 1080 - 16 - result.Height);
        Assert.Equal(9.0 / 16.0, (result.Width - 365) / (result.Height - 130), precision: 6);
    }

    [Fact]
    public void CalculateAdaptiveWindowBounds_ClampsWideVideoToWorkArea()
    {
        Rect result = PlaybackWindow.CalculateAdaptiveWindowBounds(
            32.0 / 9.0,
            new Rect(100, 50, 1280, 720),
            new Rect(200, 100, 1100, 700),
            horizontalChrome: 365,
            verticalChrome: 130,
            currentPlayerHeight: 570);

        Assert.InRange(result.Left, 116, 1364 - result.Width);
        Assert.InRange(result.Top, 66, 754 - result.Height);
        Assert.True(result.Width <= 1248);
        Assert.True(result.Height <= 688);
        Assert.Equal(32.0 / 9.0, (result.Width - 365) / (result.Height - 130), precision: 6);
    }

    [Fact]
    public void FileLocator_SelectsNormalizedFileWithoutOpeningFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"packingproof-locate-{Guid.NewGuid():N}");
        string file = Path.Combine(folder, "video.mp4");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(file, [1]);
        try
        {
            string? selected = null;
            bool opened = false;
            FileLocationResult result = WindowsShellFileLocator.Locate(
                file,
                path => { selected = path; return true; },
                _ => opened = true);

            Assert.Equal(FileLocationResult.Selected, result);
            Assert.Equal(Path.GetFullPath(file), selected);
            Assert.False(opened);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void FileLocator_OpensContainingFolderWhenSelectionFails()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"packingproof-locate-{Guid.NewGuid():N}");
        string file = Path.Combine(folder, "video.mp4");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(file, [1]);
        try
        {
            string? openedFolder = null;
            FileLocationResult result = WindowsShellFileLocator.Locate(
                file,
                _ => false,
                path => openedFolder = path);

            Assert.Equal(FileLocationResult.OpenedFolder, result);
            Assert.Equal(folder, openedFolder);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        foreach (string startPath in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? directory = new(startPath);
            while (directory != null)
            {
                string candidate = Path.Combine([directory.FullName, .. relativeParts]);
                if (File.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"无法定位仓库文件：{Path.Combine(relativeParts)}");
    }
}
