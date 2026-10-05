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

    /// <summary>
    /// 几何存的是原生画面坐标：换旋转角度时要保证框仍盖住同一块画面区域。
    /// 这里用像素级旋转（顺时针 90°）对一遍答案。
    /// </summary>
    [Fact]
    public void Rotate_NinetyDegreesKeepsTheSameRegionOnTheRotatedFrame()
    {
        var geometry = new CameraBarcodeGuideGeometry(0.5, 0.4, 0.5, -1);
        Rect nativeFrame = new(0, 0, 1280, 720);
        Rect rotatedFrame = new(0, 0, 720, 1280);
        Rect nativeBox = CameraBarcodeGuideLayout.ToDisplayRect(geometry, nativeFrame);
        AssertClose(new Rect(480, 0, 640, 288), nativeBox);

        Rect rotatedBox = CameraBarcodeGuideLayout.ToDisplayRect(
            CameraBarcodeGuideLayout.Rotate(geometry, 90),
            rotatedFrame);

        // 顺时针 90°：像素 (x,y) → (719-y, x)，原框换算过去是 (431,480,288,640)。
        // 比例换算按"中心 + 留白比例"定义，允许 1 像素的取整差。
        AssertCloseWithin(new Rect(431, 480, 288, 640), rotatedBox, 1.0);
    }

    /// <summary>90/180/270 都要能原样还原：拖动后按这个还原再落盘，来回切换旋转不会累积偏移。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RotateInverse_UndoesRotation(int degrees)
    {
        var geometry = new CameraBarcodeGuideGeometry(0.5, 0.25, 0.5, -1);

        CameraBarcodeGuideGeometry recovered = CameraBarcodeGuideLayout.RotateInverse(
            CameraBarcodeGuideLayout.Rotate(geometry, degrees),
            degrees);

        Assert.Equal(geometry.WidthRatio, recovered.WidthRatio, 6);
        Assert.Equal(geometry.HeightRatio, recovered.HeightRatio, 6);
        Assert.Equal(geometry.OffsetX, recovered.OffsetX, 6);
        Assert.Equal(geometry.OffsetY, recovered.OffsetY, 6);
    }

    /// <summary>
    /// 旋转只是换个坐标系，比例仍落在 30%~100% 的允许区间里，
    /// 不会像"把显示比例直接存成新比例"那样被夹紧规则压变形。
    /// </summary>
    [Fact]
    public void Rotate_SwapsRatiosWithoutLeavingTheAllowedRange()
    {
        var geometry = new CameraBarcodeGuideGeometry(0.5, 0.3, 1, -1);

        CameraBarcodeGuideGeometry rotated = CameraBarcodeGuideLayout.Rotate(geometry, 90);

        Assert.Equal(0.3, rotated.WidthRatio, 6);
        Assert.Equal(0.5, rotated.HeightRatio, 6);
        Assert.Equal(1, rotated.OffsetX, 6);
        Assert.Equal(1, rotated.OffsetY, 6);
        Assert.InRange(rotated.WidthRatio, CameraBarcodeGuideLayout.MinRatio, CameraBarcodeGuideLayout.MaxRatio);
        Assert.InRange(rotated.HeightRatio, CameraBarcodeGuideLayout.MinRatio, CameraBarcodeGuideLayout.MaxRatio);
    }

    /// <summary>默认的居中方形（宽高比例相同、不偏移）转不转都是同一块区域，用户不用重调。</summary>
    [Fact]
    public void Rotate_KeepsTheCenteredSquareUnchanged()
    {
        var geometry = new CameraBarcodeGuideGeometry(0.6, 0.6, 0, 0);

        Assert.Equal(geometry, CameraBarcodeGuideLayout.Rotate(geometry, 90));
        Assert.Equal(geometry, CameraBarcodeGuideLayout.Rotate(geometry, 180));
        Assert.Equal(geometry, CameraBarcodeGuideLayout.Rotate(geometry, 270));
    }

    /// <summary>
    /// 放大倍率 = 1 / max(框宽比例, 框高比例)：旋转只是把这两个比例对调，
    /// max 不变，所以同一块取景框横屏竖屏的倍率必须是同一个数 ——
    /// 不会因为转屏就把 2 倍变成别的倍数。
    /// </summary>
    [Fact]
    public void Rotate_KeepsTheZoomScaleUnchanged()
    {
        var geometry = new CameraBarcodeGuideGeometry(0.5, 0.4, 0.5, -1);
        Rect landscapeFrame = new(0, 0, 1280, 720);
        Rect portraitFrame = new(0, 0, 720, 1280);

        double landscapeScale = ZoomCropPolicy.ResolveScale(
            (int)landscapeFrame.Width,
            (int)landscapeFrame.Height,
            CameraBarcodeGuideLayout.ToDisplayRect(geometry, landscapeFrame));
        double portraitScale = ZoomCropPolicy.ResolveScale(
            (int)portraitFrame.Width,
            (int)portraitFrame.Height,
            CameraBarcodeGuideLayout.ToDisplayRect(
                CameraBarcodeGuideLayout.Rotate(geometry, 90),
                portraitFrame));

        Assert.Equal(2.0, landscapeScale, 6);
        Assert.Equal(landscapeScale, portraitScale, 6);
    }

    /// <summary>
    /// 放大框规范成"和画面同长宽比"之后，"框住的那块"必须正好等于放大实际裁的那一块
    /// （以框为中心、按画面比例的一大块）：不多不少，也不会拉伸变形。
    /// </summary>
    [Theory]
    [InlineData(0.5, 0.3, 0.0, 0.0)]
    [InlineData(0.5, 0.3, 0.5, 0.0)]
    [InlineData(0.9, 0.3, 1.0, -1.0)]
    [InlineData(0.3, 0.9, -1.0, 1.0)]
    public void NormalizeToFrameAspect_MatchesTheZoomCropRegion(
        double widthRatio,
        double heightRatio,
        double offsetX,
        double offsetY)
    {
        var geometry = new CameraBarcodeGuideGeometry(widthRatio, heightRatio, offsetX, offsetY);

        CameraBarcodeGuideGeometry normalized = CameraBarcodeGuideLayout.NormalizeToFrameAspect(geometry);
        Assert.Equal(normalized.WidthRatio, normalized.HeightRatio, 6);

        foreach ((int width, int height) in new[] { (1280, 720), (720, 1280), (1920, 1080) })
        {
            Rect frame = new(0, 0, width, height);
            Rect originalBox = CameraBarcodeGuideLayout.ToDisplayRect(geometry, frame);
            Rect crop = ZoomCropPolicy.CreateCropRect(
                width,
                height,
                ZoomCropPolicy.ResolveScale(width, height, originalBox),
                originalBox);

            AssertCloseWithin(crop, CameraBarcodeGuideLayout.ToDisplayRect(normalized, frame), 1.0);
        }
    }

    /// <summary>放大框拖动把手时保持画面比例，且被拖动把手的对角那个角不动。</summary>
    [Fact]
    public void Resize_WithFrameAspectLockKeepsRatioAndOppositeCorner()
    {
        Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(1280, 720, 1280, 720);
        var geometry = new CameraBarcodeGuideGeometry(0.5, 0.5, 0, 0);

        CameraBarcodeGuideGeometry grown = CameraBarcodeGuideLayout.Resize(
            geometry,
            videoRect,
            CameraBarcodeGuideHandle.BottomRight,
            100,
            0,
            keepFrameAspect: true);

        Assert.Equal(grown.WidthRatio, grown.HeightRatio, 6);
        Rect rect = CameraBarcodeGuideLayout.ToDisplayRect(grown, videoRect);
        Assert.Equal(320, rect.Left, 3);
        Assert.Equal(180, rect.Top, 3);
        // 只往右拖也能整体变大：另一轴按画面比例一起长。
        Assert.True(rect.Width > 640);
        Assert.True(rect.Height > 360);
    }

    /// <summary>
    /// 拖四角缩放放大框时尺寸必须平滑：每一步只按这一步的拖动量缩放，
    /// 不能因为"这次以横坐标为准、下次以竖坐标为准"而一会儿大一会儿小。
    /// </summary>
    [Fact]
    public void Resize_WithFrameAspectLockScalesSmoothly()
    {
        Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(1280, 720, 1280, 720);
        var geometry = new CameraBarcodeGuideGeometry(0.5, 0.5, 0, 0);

        // 横竖比例相近的斜向拖动，最容易被两轴来回抢：每一步都必须是同一个方向的平滑缩放。
        foreach ((double deltaX, double deltaY) in new[]
                 {
                     (6.0, 4.0), (6.0, 5.0), (6.0, 6.0), (6.0, 5.0), (6.0, 4.0), (6.0, 3.0)
                 })
        {
            double before = geometry.WidthRatio;
            geometry = CameraBarcodeGuideLayout.Resize(
                geometry,
                videoRect,
                CameraBarcodeGuideHandle.BottomRight,
                deltaX,
                deltaY,
                keepFrameAspect: true);

            Assert.Equal(geometry.WidthRatio, geometry.HeightRatio, 6);
            Assert.True(geometry.WidthRatio > before, "往同一个方向拖，尺寸不能反向变化");
            double expectedStep =
                ((deltaX / videoRect.Width) + (deltaY / videoRect.Height)) / 2.0;
            Assert.Equal(before + expectedStep, geometry.WidthRatio, 4);
        }
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
    public void UnlockedGuideSuspendsZoomUntilItIsLockedAgain()
    {
        string source = RepositorySource.ReadMainViewModel();

        Assert.Contains("ZoomCropPolicy.ShouldApplyZoom", source, StringComparison.Ordinal);
        // 放大步进抽在 MainViewModel.Zoom.cs：不可放大时先走复位分支，预览回到整帧
        Assert.Contains(
            "if (!CanApplyZoom)",
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

    private static void AssertCloseWithin(Rect expected, Rect actual, double tolerance)
    {
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
        Assert.InRange(actual.Width, expected.Width - tolerance, expected.Width + tolerance);
        Assert.InRange(actual.Height, expected.Height - tolerance, expected.Height + tolerance);
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
