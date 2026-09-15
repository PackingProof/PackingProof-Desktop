using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 预览按控件实际显示尺寸发布：1080p 整帧每帧约 6MB 的克隆 + 写位图 + 传 GPU 缩放，
/// 缩到显示尺寸后这些开销按像素数等比下降。
/// </summary>
public sealed class PreviewDownscalePolicyTests
{
    [Fact]
    public void DownscalesFullHdPreviewToDisplayWidthKeepingAspect()
    {
        (int Width, int Height)? target = PreviewDownscalePolicy.ResolveTarget(1920, 1080, 960);

        Assert.NotNull(target);
        Assert.Equal(960, target!.Value.Width);
        Assert.Equal(540, target.Value.Height);
    }

    /// <summary>显示尺寸比帧本身还大时不放大，按原始尺寸发布。</summary>
    [Fact]
    public void NeverUpscalesBeyondSource()
    {
        Assert.Null(PreviewDownscalePolicy.ResolveTarget(640, 480, 1600));
        Assert.Null(PreviewDownscalePolicy.ResolveTarget(1920, 1080, 1920));
    }

    /// <summary>窗口被拉得很小时保留下限，避免糊成一片。</summary>
    [Fact]
    public void KeepsMinimumWidth()
    {
        (int Width, int Height)? target = PreviewDownscalePolicy.ResolveTarget(1920, 1080, 120);

        Assert.NotNull(target);
        Assert.Equal(PreviewDownscalePolicy.MinimumWidth, target!.Value.Width);
    }

    /// <summary>
    /// 还没量到显示尺寸（窗口尚未布局）时按下限发布：预览只喂给主界面与小窗两个控件，
    /// 从便宜的一侧起步没有副作用，窗口一布局好就会上报真实宽度。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void UnknownDisplayWidthFallsBackToMinimumWidth(int displayWidth)
    {
        (int Width, int Height)? target = PreviewDownscalePolicy.ResolveTarget(1920, 1080, displayWidth);

        Assert.NotNull(target);
        Assert.Equal(PreviewDownscalePolicy.MinimumWidth, target!.Value.Width);
    }

    /// <summary>宽高都取偶数，避免奇数 stride 让下游按行对齐出问题。</summary>
    [Fact]
    public void RoundsToEvenDimensions()
    {
        (int Width, int Height)? target = PreviewDownscalePolicy.ResolveTarget(1921, 1081, 961);

        Assert.NotNull(target);
        Assert.Equal(0, target!.Value.Width % 2);
        Assert.Equal(0, target.Value.Height % 2);
    }

    /// <summary>异常输入不能抛，退回按原始尺寸发布。</summary>
    [Theory]
    [InlineData(0, 0, 800)]
    [InlineData(-1920, 1080, 800)]
    [InlineData(1920, 0, 800)]
    public void InvalidSourceSizeFallsBackToNative(int width, int height, int displayWidth)
    {
        Assert.Null(PreviewDownscalePolicy.ResolveTarget(width, height, displayWidth));
    }
}
