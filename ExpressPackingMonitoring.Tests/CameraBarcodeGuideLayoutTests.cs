using ExpressPackingMonitoring.Services;
using System.Windows;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 主界面拖动识别框的换算守卫：预览里的矩形必须和识别实际使用的取景矩形同源，
/// 拖动与缩放后仍要落在画面内，并且不小于设置里允许的最小比例。
/// </summary>
public sealed class CameraBarcodeGuideLayoutTests
{
    [Fact]
    public void DisplayRect_MatchesRecognitionGuideRect()
    {
        Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(1280, 720, 1280, 720);
        var geometry = new CameraBarcodeGuideGeometry(0.5, 0.5, 0, 0);

        AssertClose(new Rect(320, 180, 640, 360), CameraBarcodeGuideLayout.ToDisplayRect(geometry, videoRect));
        AssertClose(
            ToWindowsRect(CameraBarcodeFrameDecoder.GetGuideRect(1280, 720, geometry)),
            CameraBarcodeGuideLayout.ToDisplayRect(geometry, videoRect));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    [InlineData(1, 1)]
    public void DisplayRect_FollowsOffsetsLikeRecognition(double offsetX, double offsetY)
    {
        Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(1280, 720, 1280, 720);
        var geometry = new CameraBarcodeGuideGeometry(0.6, 0.5, offsetX, offsetY);

        AssertClose(
            ToWindowsRect(CameraBarcodeFrameDecoder.GetGuideRect(1280, 720, geometry)),
            CameraBarcodeGuideLayout.ToDisplayRect(geometry, videoRect));
    }

    [Fact]
    public void RoundTrip_KeepsGeometryWhenPreviewHasLetterbox()
    {
        // 1920x1080 的画面放进 800x600 的预览，上下会留黑边
        Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(1920, 1080, 800, 600);
        var geometry = new CameraBarcodeGuideGeometry(0.5, 0.6, 0.25, -0.4);

        CameraBarcodeGuideGeometry recovered = CameraBarcodeGuideLayout.FromDisplayRect(
            CameraBarcodeGuideLayout.ToDisplayRect(geometry, videoRect),
            videoRect);

        Assert.Equal(geometry.WidthRatio, recovered.WidthRatio, 3);
        Assert.Equal(geometry.HeightRatio, recovered.HeightRatio, 3);
        Assert.Equal(geometry.OffsetX, recovered.OffsetX, 3);
        Assert.Equal(geometry.OffsetY, recovered.OffsetY, 3);
    }

    [Fact]
    public void Move_StopsAtVideoEdgesAndKeepsSize()
    {
        Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(1280, 720, 1280, 720);
        var geometry = new CameraBarcodeGuideGeometry(0.5, 0.5, 0, 0);

        CameraBarcodeGuideGeometry moved = CameraBarcodeGuideLayout.Move(geometry, videoRect, 5000, -5000);

        Assert.Equal(1.0, moved.OffsetX, 3);
        Assert.Equal(-1.0, moved.OffsetY, 3);
        Assert.Equal(geometry.WidthRatio, moved.WidthRatio, 3);
        Assert.Equal(geometry.HeightRatio, moved.HeightRatio, 3);
        Assert.True(videoRect.Contains(CameraBarcodeGuideLayout.ToDisplayRect(moved, videoRect)));
    }

    [Fact]
    public void Resize_KeepsOppositeCornerFixed()
    {
        Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(1280, 720, 1280, 720);
        var geometry = new CameraBarcodeGuideGeometry(0.5, 0.5, 0, 0);

        CameraBarcodeGuideGeometry grown = CameraBarcodeGuideLayout.Resize(
            geometry,
            videoRect,
            CameraBarcodeGuideHandle.BottomRight,
            100,
            50);

        AssertClose(
            new Rect(320, 180, 740, 410),
            CameraBarcodeGuideLayout.ToDisplayRect(grown, videoRect));
    }

    [Fact]
    public void Resize_StopsAtMinimumRatioAndStaysInsideVideo()
    {
        Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(1280, 720, 1280, 720);
        var geometry = new CameraBarcodeGuideGeometry(0.5, 0.5, 0, 0);

        CameraBarcodeGuideGeometry shrunk = CameraBarcodeGuideLayout.Resize(
            geometry,
            videoRect,
            CameraBarcodeGuideHandle.TopLeft,
            5000,
            5000);

        Assert.Equal(CameraBarcodeGuideLayout.MinRatio, shrunk.WidthRatio, 3);
        Assert.Equal(CameraBarcodeGuideLayout.MinRatio, shrunk.HeightRatio, 3);
        AssertClose(
            new Rect(576, 324, 384, 216),
            CameraBarcodeGuideLayout.ToDisplayRect(shrunk, videoRect));

        CameraBarcodeGuideGeometry grown = CameraBarcodeGuideLayout.Resize(
            geometry,
            videoRect,
            CameraBarcodeGuideHandle.BottomRight,
            5000,
            5000);

        AssertClose(new Rect(320, 180, 960, 540), CameraBarcodeGuideLayout.ToDisplayRect(grown, videoRect));
        Assert.True(videoRect.Contains(CameraBarcodeGuideLayout.ToDisplayRect(grown, videoRect)));
    }

    [Fact]
    public void UnlockedGuideSuspendsSmartZoomUntilItIsLockedAgain()
    {
        string source = RepositorySource.ReadMainViewModel();

        Assert.Contains("SmartZoomPolicy.ShouldApplyZoom", source, StringComparison.Ordinal);
        Assert.Contains(
            "CanApplySmartZoom || PreviewZoomScale.HasValue",
            source,
            StringComparison.Ordinal);
        // 解锁时要能中途停掉已经在跑的放大，否则取景框会一直没法拖动
        Assert.Contains("GuideLocked={IsCameraBarcodeGuideLocked}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowGuideEditing_WiresHandlesAndLock()
    {
        string xaml = ReadMainWindowFile("MainWindow.xaml");
        foreach (string handle in new[]
                 {
                     "CameraGuideHandleTopLeft",
                     "CameraGuideHandleTopRight",
                     "CameraGuideHandleBottomLeft",
                     "CameraGuideHandleBottomRight"
                 })
        {
            Assert.Contains(handle, xaml, StringComparison.Ordinal);
        }

        Assert.Contains("BtnCameraGuideLock_Click", xaml, StringComparison.Ordinal);
        // 提示必须走绑定：样式触发器改 ToolTip 会被自动本地化写入的值压住，锁状态切换后就不更新了
        Assert.Contains(
            "ToolTip=\"{Binding CameraBarcodeGuideLockTipText}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("{Binding IsCameraBarcodeGuideEditable}", xaml, StringComparison.Ordinal);
        Assert.Contains("DragCompleted=\"CameraGuideDrag_Completed\"", xaml, StringComparison.Ordinal);

        string code = ReadMainWindowFile("MainWindow.xaml.cs");
        Assert.Contains("CameraBarcodeGuideLayout.Move", code, StringComparison.Ordinal);
        Assert.Contains("CameraBarcodeGuideLayout.Resize", code, StringComparison.Ordinal);
        Assert.Contains("persist: true", code, StringComparison.Ordinal);
    }

    private static string ReadMainWindowFile(string fileName) =>
        File.ReadAllText(Path.Combine(FindUiDirectory(), fileName));

    private static Rect ToWindowsRect(OpenCvSharp.Rect rect) =>
        new(rect.X, rect.Y, rect.Width, rect.Height);

    private static void AssertClose(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Width, actual.Width, 3);
        Assert.Equal(expected.Height, actual.Height, 3);
    }

    private static string FindUiDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return Path.Combine(directory.FullName, "ExpressPackingMonitoring", "UI");
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到 UI 目录");
    }
}
