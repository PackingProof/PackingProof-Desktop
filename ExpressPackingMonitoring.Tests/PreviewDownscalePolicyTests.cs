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
    /// 还没量到显示尺寸（窗口尚未布局）时按原始尺寸发布：发布得比控件小就会被 WPF 放大，
    /// 画面立刻发糊 —— 镜像套镜像时一层比一层糊，宁可贵一点也不放大。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void UnknownDisplayWidthPublishesNativeSize(int displayWidth)
    {
        Assert.Null(PreviewDownscalePolicy.ResolveTarget(1920, 1080, displayWidth));
    }

    /// <summary>
    /// 发布尺寸与源严格同比例。之前宽高各向下取偶，发布帧比源宽 0.2%~0.4%，
    /// 画面被横向拉伸；镜像套镜像时每层再乘一次，看起来就是画面一直在左右抖。
    /// 常规比例（16:9、4:3、16:10）都能整步吸附，误差必须是 0。
    /// </summary>
    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(1600, 1200)]
    [InlineData(1280, 800)]
    public void PublishesExactSourceAspectRatio(int sourceWidth, int sourceHeight)
    {
        for (int displayWidth = PreviewDownscalePolicy.MinimumWidth; displayWidth < sourceWidth; displayWidth++)
        {
            (int Width, int Height)? target =
                PreviewDownscalePolicy.ResolveTarget(sourceWidth, sourceHeight, displayWidth);
            if (target == null)
                continue;

            // 交叉相乘判等：整数运算，不受浮点误差影响。
            Assert.Equal(
                (long)sourceWidth * target.Value.Height,
                (long)sourceHeight * target.Value.Width);
        }
    }

    /// <summary>
    /// 吸附一律向上取步：发布得比控件小就会被 WPF 插值放大，画面立刻发糊。
    /// 比控件大几像素只是被缩掉，不损画质。
    /// </summary>
    [Fact]
    public void NeverPublishesNarrowerThanDisplayWidth()
    {
        for (int displayWidth = PreviewDownscalePolicy.MinimumWidth; displayWidth < 1920; displayWidth++)
        {
            (int Width, int Height)? target = PreviewDownscalePolicy.ResolveTarget(1920, 1080, displayWidth);
            if (target == null)
                continue;

            Assert.True(
                target.Value.Width >= displayWidth,
                $"displayWidth={displayWidth} 发布成了 {target.Value.Width}，会被放大发糊");
        }
    }

    /// <summary>
    /// 尺寸按比例步长量化：控件宽度变动一两像素不再换发布尺寸，
    /// 否则 WriteableBitmap 会跟着反复重建，拖窗口时就是一串 UI 线程上的大对象分配。
    /// </summary>
    [Fact]
    public void QuantizesSizeSoSmallLayoutNudgesDoNotRepublish()
    {
        (int Width, int Height)? first = PreviewDownscalePolicy.ResolveTarget(1920, 1080, 945);
        (int Width, int Height)? second = PreviewDownscalePolicy.ResolveTarget(1920, 1080, 947);

        Assert.Equal(first, second);
        Assert.Equal(960, first!.Value.Width);
    }

    /// <summary>
    /// 约简不掉的怪尺寸走四舍五入分支：宽取控件宽度、高就近取整，
    /// 比例误差控制在 0.1% 以内，且不像双向下取整那样带单向偏移。
    /// </summary>
    [Fact]
    public void UnusualSourceSizeKeepsAspectWithinTolerance()
    {
        (int Width, int Height)? target = PreviewDownscalePolicy.ResolveTarget(1921, 1081, 961);

        Assert.NotNull(target);
        Assert.Equal(961, target!.Value.Width);
        double sourceAspect = 1921 / 1081d;
        double publishedAspect = target.Value.Width / (double)target.Value.Height;
        Assert.True(
            Math.Abs(publishedAspect - sourceAspect) / sourceAspect < 0.001,
            $"比例误差过大：源 {sourceAspect:F5}，发布 {publishedAspect:F5}");
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

    /// <summary>
    /// 预览发布的停止条件：拖动窗口、用户关闭实时预览、已释放、摄像头休眠，
    /// 以及**没有可见预览消费方**（最小化/隐藏且小窗未显示）——没人看就不浪费资源。
    /// </summary>
    [Theory]
    [InlineData(false, false, false, false, true, true)]
    [InlineData(false, false, false, false, false, false)]
    [InlineData(true, false, false, false, true, false)]
    [InlineData(false, true, false, false, true, false)]
    [InlineData(false, false, true, false, true, false)]
    [InlineData(false, false, false, true, true, false)]
    public void PreviewPublishPolicy_StopsWhenNobodyIsWatching(
        bool suppressed,
        bool disabledByUser,
        bool disposed,
        bool cameraSleeping,
        bool hasVisiblePreviewConsumer,
        bool expected)
    {
        Assert.Equal(
            expected,
            PreviewPublishPolicy.ShouldPublish(
                suppressed,
                disabledByUser,
                disposed,
                cameraSleeping,
                hasVisiblePreviewConsumer));
    }

    /// <summary>
    /// 界面可见时的空闲降档：刚操作过 → 满帧；1 分钟没人动 → 15fps；5 分钟没人动 → 4fps。
    /// 降档只在低于摄像头帧率时生效，摄像头本来只有 10fps 时不会被"提高"到 15fps。
    /// （没人看时不走这里，直接不发布，见 PreviewPublishPolicy。）
    /// </summary>
    [Theory]
    [InlineData(60, 0, 60)]
    [InlineData(60, 59, 60)]
    [InlineData(60, 60, PreviewFrameRatePolicy.ReducedFps)]
    [InlineData(60, 299, PreviewFrameRatePolicy.ReducedFps)]
    [InlineData(60, 300, PreviewFrameRatePolicy.LowFps)]
    [InlineData(60, 3600, PreviewFrameRatePolicy.LowFps)]
    [InlineData(0, 0, PreviewFrameRatePolicy.FallbackCameraFps)]
    [InlineData(10, 0, 10)]
    [InlineData(10, 60, 10)]
    public void PreviewFrameRatePolicy_TiersByIdle(
        int cameraFps,
        int idleSeconds,
        int expected)
    {
        Assert.Equal(
            expected,
            PreviewFrameRatePolicy.ResolveTargetFps(
                cameraFps,
                TimeSpan.FromSeconds(idleSeconds)));
    }
}
