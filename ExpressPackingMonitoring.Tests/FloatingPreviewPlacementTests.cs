using System.Windows;
using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 小窗停靠位置规则：只记角落不记坐标，
/// 换分辨率、换显示器或改窗口大小都不能把小窗放到屏幕外或压住标题栏按钮。
/// </summary>
public sealed class FloatingPreviewPlacementTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1040);
    private static readonly Size WindowSize = new(340, 248);

    [Theory]
    [InlineData(10, 10, FloatingPreviewCorner.TopLeft)]
    [InlineData(1500, 10, FloatingPreviewCorner.TopRight)]
    [InlineData(10, 900, FloatingPreviewCorner.BottomLeft)]
    [InlineData(1500, 900, FloatingPreviewCorner.BottomRight)]
    public void ResolveCorner_UsesWindowCentre(double left, double top, FloatingPreviewCorner expected)
    {
        var window = new Rect(left, top, WindowSize.Width, WindowSize.Height);

        Assert.Equal(expected, FloatingPreviewPlacement.ResolveCorner(window, WorkArea));
    }

    /// <summary>判定按中心而不是左上角，贴着中线右侧的窗口应算作右半边。</summary>
    [Fact]
    public void ResolveCorner_WindowStraddlingCentre_FollowsCentre()
    {
        // 左边缘在中线左侧，但中心已经越过中线。
        var window = new Rect(880, 900, WindowSize.Width, WindowSize.Height);

        Assert.Equal(FloatingPreviewCorner.BottomRight,
            FloatingPreviewPlacement.ResolveCorner(window, WorkArea));
    }

    /// <summary>顶部要留出更大边距，避免压住标题栏的最小化、全屏和关闭按钮。</summary>
    [Theory]
    [InlineData(FloatingPreviewCorner.TopLeft)]
    [InlineData(FloatingPreviewCorner.TopRight)]
    public void TopCorners_LeaveRoomForWindowButtons(FloatingPreviewCorner corner)
    {
        Point position = FloatingPreviewPlacement.ResolvePosition(corner, WindowSize, WorkArea);

        Assert.Equal(WorkArea.Top + FloatingPreviewPlacement.TopEdgeMargin, position.Y);
        Assert.True(
            FloatingPreviewPlacement.TopEdgeMargin > FloatingPreviewPlacement.EdgeMargin,
            "顶部边距必须大于其它方向，否则会压住窗口按钮");
    }

    [Fact]
    public void BottomRight_SitsInsideWorkArea()
    {
        Point position = FloatingPreviewPlacement.ResolvePosition(
            FloatingPreviewCorner.BottomRight, WindowSize, WorkArea);

        Assert.Equal(WorkArea.Right - WindowSize.Width - FloatingPreviewPlacement.EdgeMargin, position.X);
        Assert.Equal(WorkArea.Bottom - WindowSize.Height - FloatingPreviewPlacement.EdgeMargin, position.Y);
    }

    /// <summary>每个角落算出来的位置都必须完整落在工作区内。</summary>
    [Theory]
    [InlineData(FloatingPreviewCorner.TopLeft)]
    [InlineData(FloatingPreviewCorner.TopRight)]
    [InlineData(FloatingPreviewCorner.BottomLeft)]
    [InlineData(FloatingPreviewCorner.BottomRight)]
    public void EveryCorner_StaysWithinWorkArea(FloatingPreviewCorner corner)
    {
        Point position = FloatingPreviewPlacement.ResolvePosition(corner, WindowSize, WorkArea);

        Assert.InRange(position.X, WorkArea.Left, WorkArea.Right - WindowSize.Width);
        Assert.InRange(position.Y, WorkArea.Top, WorkArea.Bottom - WindowSize.Height);
    }

    /// <summary>工作区不是从 0,0 开始时（任务栏在左或在上）也要贴对位置。</summary>
    [Fact]
    public void OffsetWorkArea_IsRespected()
    {
        var offsetArea = new Rect(100, 60, 1400, 900);

        Point position = FloatingPreviewPlacement.ResolvePosition(
            FloatingPreviewCorner.TopLeft, WindowSize, offsetArea);

        Assert.Equal(offsetArea.Left + FloatingPreviewPlacement.EdgeMargin, position.X);
        Assert.Equal(offsetArea.Top + FloatingPreviewPlacement.TopEdgeMargin, position.Y);
    }

    /// <summary>窗口比工作区还大时至少保证左上角可见，用户还能拖得动。</summary>
    [Fact]
    public void OversizedWindow_ClampsToWorkAreaOrigin()
    {
        var huge = new Size(3000, 2000);

        Point position = FloatingPreviewPlacement.ResolvePosition(
            FloatingPreviewCorner.BottomRight, huge, WorkArea);

        Assert.Equal(WorkArea.Left, position.X);
        Assert.Equal(WorkArea.Top, position.Y);
    }

    /// <summary>配置缺失或被改坏时退回右下角这个默认位置，而不是抛异常。</summary>
    [Theory]
    [InlineData(null, FloatingPreviewCorner.BottomRight)]
    [InlineData("", FloatingPreviewCorner.BottomRight)]
    [InlineData("不是角落", FloatingPreviewCorner.BottomRight)]
    [InlineData("TopLeft", FloatingPreviewCorner.TopLeft)]
    [InlineData("topleft", FloatingPreviewCorner.TopLeft)]
    [InlineData("BottomLeft", FloatingPreviewCorner.BottomLeft)]
    public void Parse_FallsBackToBottomRight(string? value, FloatingPreviewCorner expected)
    {
        Assert.Equal(expected, FloatingPreviewPlacement.Parse(value));
    }

    /// <summary>关闭时判定的角落，下次打开必须能原样还原回去。</summary>
    [Theory]
    [InlineData(FloatingPreviewCorner.TopLeft)]
    [InlineData(FloatingPreviewCorner.TopRight)]
    [InlineData(FloatingPreviewCorner.BottomLeft)]
    [InlineData(FloatingPreviewCorner.BottomRight)]
    public void CornerRoundTrips_ThroughPositionAndBack(FloatingPreviewCorner corner)
    {
        Point position = FloatingPreviewPlacement.ResolvePosition(corner, WindowSize, WorkArea);
        var placed = new Rect(position.X, position.Y, WindowSize.Width, WindowSize.Height);

        Assert.Equal(corner, FloatingPreviewPlacement.ResolveCorner(placed, WorkArea));
    }
}
