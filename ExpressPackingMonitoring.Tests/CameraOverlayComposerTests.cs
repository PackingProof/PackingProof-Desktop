using ExpressPackingMonitoring.ViewModels;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 副画面合成必须真的写进主帧的右下角、不能动其它区域，也不能在输入异常时破坏主帧。
/// 用纯 OpenCV 的假帧验证，不需要摄像头。
/// </summary>
public sealed class CameraOverlayComposerTests
{
    private const double WidthRatio = 0.25;
    private const int Margin = 16;

    [Fact]
    public void ComposesOverlayIntoBottomRightCornerOnly()
    {
        using var main = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(0, 0, 0));
        using var secondary = new Mat(480, 640, MatType.CV_8UC3, new Scalar(255, 255, 255));

        CameraOverlayRect rect = CameraOverlayLayout
            .Resolve(1920, 1080, 640, 480, WidthRatio, Margin)!.Value;

        Assert.True(CameraOverlayComposer.TryCompose(main, secondary, rect));

        using var inside = new Mat(main, new Rect(rect.X + 12, rect.Y + 12, 24, 24));
        Assert.True(Cv2.Mean(inside).Val0 > 200, "副画面没有画到右下角");

        using var topLeft = new Mat(main, new Rect(12, 12, 24, 24));
        Assert.True(Cv2.Mean(topLeft).Val0 < 40, "叠加污染了右上/左上区域");

        using var bottomLeft = new Mat(main, new Rect(12, 1080 - 60, 24, 24));
        Assert.True(Cv2.Mean(bottomLeft).Val0 < 40, "叠加污染了左下区域");
    }

    /// <summary>副画面必须按自身比例缩放，不能被主画面比例拉变形。</summary>
    [Fact]
    public void ScalesOverlayToItsOwnAspect()
    {
        using var main = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(0, 0, 0));
        // 4:3 副画面
        using var secondary = new Mat(480, 640, MatType.CV_8UC3, new Scalar(255, 255, 255));

        CameraOverlayRect rect = CameraOverlayLayout
            .Resolve(1920, 1080, 640, 480, WidthRatio, Margin)!.Value;

        Assert.True(CameraOverlayComposer.TryCompose(main, secondary, rect));

        Assert.Equal(4.0 / 3.0, (double)rect.Width / rect.Height, precision: 2);
    }

    /// <summary>
    /// 小窗边框要是圆角，和识别框的圆角矩形对应：
    /// 直边中段有白边，直角位置不画（否则看起来就是生硬的方角）。
    /// </summary>
    [Fact]
    public void OverlayBorderIsRounded()
    {
        using var main = new Mat(800, 1280, MatType.CV_8UC3, new Scalar(0, 0, 0));
        // 小窗用纯白，方便区分"贴上去的内容"和"被圆角裁掉后露出的主画面"
        using var secondary = new Mat(400, 600, MatType.CV_8UC3, new Scalar(255, 255, 255));

        CameraOverlayRect rect = CameraOverlayLayout
            .Resolve(1280, 800, 600, 400, widthRatio: 0.5, margin: 16)!.Value;

        Assert.True(CameraOverlayComposer.TryCompose(main, secondary, rect));

        // 顶边中段：小窗内容 + 内侧边框，必须是白的
        int middleX = rect.X + (rect.Width / 2);
        Vec3b edge = main.At<Vec3b>(rect.Y + 1, middleX);
        Assert.True(edge.Item0 > 200 && edge.Item1 > 200 && edge.Item2 > 200, "小窗边框没有画出来");

        // 四个角必须按圆角裁掉，露出主画面原本的内容，而不是小窗的方角。
        // 半径只有几像素，取角上 3x3 小片看均值：整块都贴上的话均值会接近 255。
        foreach ((int x, int y) in new[]
                 {
                     (rect.X, rect.Y),
                     (rect.X + rect.Width - 3, rect.Y),
                     (rect.X, rect.Y + rect.Height - 3),
                     (rect.X + rect.Width - 3, rect.Y + rect.Height - 3)
                 })
        {
            using var patch = new Mat(main, new Rect(x, y, 3, 3));
            double mean = Cv2.Mean(patch).Val0;
            Assert.True(
                mean < 200,
                $"小窗 {(x - rect.X)},{y - rect.Y} 这个角没有按圆角裁掉（均值 {mean:F0}）");
        }
    }

    /// <summary>灰度副画面（某些后端/网络流会给单通道）必须能合成，而不是抛异常。</summary>
    [Fact]
    public void SingleChannelOverlayIsConverted()
    {
        using var main = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(0, 0, 0));
        using var gray = new Mat(480, 640, MatType.CV_8UC1, new Scalar(255));

        CameraOverlayRect rect = CameraOverlayLayout
            .Resolve(1280, 720, 640, 480, WidthRatio, Margin)!.Value;

        Assert.True(CameraOverlayComposer.TryCompose(main, gray, rect));
    }

    [Fact]
    public void EmptyOrInvalidInputsLeaveFrameUntouched()
    {
        using var main = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(10, 20, 30));
        using var empty = new Mat();
        var rect = new CameraOverlayRect(100, 100, 320, 240);

        Assert.False(CameraOverlayComposer.TryCompose(main, empty, rect));
        Assert.False(CameraOverlayComposer.TryCompose(empty, main, rect));

        // 主帧仍保持原样（没有副画面没有任何副作用）
        Assert.Equal(10, Cv2.Mean(main).Val0, precision: 3);
    }

    /// <summary>
    /// 贴角规则必须作用到真正的合成上：第三路按右上角算出来，就必须画在右上角。
    /// 合并前这里会画回右下角，界面拖动框按右上摆、画面却在右下，看起来就是"框和画面对不上"。
    /// </summary>
    [Fact]
    public void ComposesOverlayIntoTheAnchoredCorner()
    {
        using var main = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(0, 0, 0));
        using var secondary = new Mat(480, 640, MatType.CV_8UC3, new Scalar(255, 255, 255));

        CameraOverlayRect rect = CameraOverlayLayout.Resolve(
            1920, 1080, 640, 480, WidthRatio, Margin,
            anchor: CameraOverlayAnchor.TopRight)!.Value;

        Assert.True(CameraOverlayComposer.TryCompose(main, secondary, rect));

        using var inside = new Mat(main, new Rect(rect.X + 12, rect.Y + 12, 24, 24));
        Assert.True(Cv2.Mean(inside).Val0 > 200, "副画面没有画到右上角");

        using var bottomRight = new Mat(main, new Rect(1920 - 60, 1080 - 60, 24, 24));
        Assert.True(Cv2.Mean(bottomRight).Val0 < 40, "画面仍然画在右下角：贴角规则没有作用到合成上");
    }

    /// <summary>落位越界或宽高非法时返回 false，不能画出一个越界或 0 宽的矩形。</summary>
    [Fact]
    public void InvalidRectIsRejected()
    {
        using var main = new Mat(80, 120, MatType.CV_8UC3, new Scalar(0, 0, 0));
        using var secondary = new Mat(480, 640, MatType.CV_8UC3, new Scalar(255, 255, 255));

        Assert.False(CameraOverlayComposer.TryCompose(main, secondary, new CameraOverlayRect(0, 0, 0, 0)));
        Assert.False(CameraOverlayComposer.TryCompose(main, secondary, new CameraOverlayRect(100, 60, 40, 40)));

        // 主帧仍保持原样
        Assert.Equal(0, Cv2.Mean(main).Val0, precision: 3);
    }

    /// <summary>
    /// 同一份叠加帧连续合成（预录回灌就是这样）会走贴片缓存，结果必须和逐帧重算一字不差：
    /// 缓存的是"已经缩放、圆角、描边好的贴片"，不是跳过绘制。
    /// </summary>
    [Fact]
    public void RepeatedComposeWithSameOverlay_ReusesPatchWithoutChangingPixels()
    {
        using var reference = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(0, 0, 0));
        using var composed = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(0, 0, 0));
        using var overlay = new Mat(480, 640, MatType.CV_8UC3, new Scalar(255, 255, 255));
        CameraOverlayRect rect = CameraOverlayLayout
            .Resolve(1920, 1080, 640, 480, WidthRatio, Margin)!.Value;

        // 参考结果：每次换一份叠加帧副本，逼它每次重新算贴片
        for (int i = 0; i < 2; i++)
        {
            using var fresh = overlay.Clone();
            Assert.True(CameraOverlayComposer.TryCompose(reference, fresh, rect));
        }

        // 连续合成十帧：同一份叠加帧，第二次之后都命中缓存
        for (int i = 0; i < 10; i++)
            Assert.True(CameraOverlayComposer.TryCompose(composed, overlay, rect));

        using Mat difference = new();
        Cv2.Absdiff(reference, composed, difference);
        Assert.Equal(0.0, Cv2.Mean(difference).Val0);
        Assert.Equal(0.0, Cv2.Mean(difference).Val1);
        Assert.Equal(0.0, Cv2.Mean(difference).Val2);
    }

    /// <summary>缓存按落位区分：换了落位必须重新做贴片，不能拿上一份尺寸去贴。</summary>
    [Fact]
    public void DifferentRects_DoNotShareCachedPatches()
    {
        using var overlay = new Mat(480, 640, MatType.CV_8UC3, new Scalar(255, 255, 255));
        using var bottomRightFrame = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(0, 0, 0));
        using var topLeftFrame = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(0, 0, 0));

        CameraOverlayRect bottomRight = CameraOverlayLayout
            .Resolve(1920, 1080, 640, 480, WidthRatio, Margin, anchor: CameraOverlayAnchor.BottomRight)!.Value;
        CameraOverlayRect topLeft = CameraOverlayLayout
            .Resolve(1920, 1080, 640, 480, WidthRatio, Margin, anchor: CameraOverlayAnchor.TopLeft)!.Value;

        Assert.True(CameraOverlayComposer.TryCompose(bottomRightFrame, overlay, bottomRight));
        Assert.True(CameraOverlayComposer.TryCompose(topLeftFrame, overlay, topLeft));

        // 每张主帧上只有自己那个落位被画到
        using var bottomRightArea = new Mat(
            bottomRightFrame,
            new Rect(bottomRight.X + 12, bottomRight.Y + 12, 24, 24));
        using var topLeftAreaOfBottomRightFrame = new Mat(
            bottomRightFrame,
            new Rect(topLeft.X + 12, topLeft.Y + 12, 24, 24));
        using var topLeftArea = new Mat(topLeftFrame, new Rect(topLeft.X + 12, topLeft.Y + 12, 24, 24));
        using var bottomRightAreaOfTopLeftFrame = new Mat(
            topLeftFrame,
            new Rect(bottomRight.X + 12, bottomRight.Y + 12, 24, 24));

        Assert.True(Cv2.Mean(bottomRightArea).Val0 > 200, "右下角那份没贴上");
        Assert.True(Cv2.Mean(topLeftAreaOfBottomRightFrame).Val0 < 40, "右上角被别的贴片污染");
        Assert.True(Cv2.Mean(topLeftArea).Val0 > 200, "左上角那份没贴上");
        Assert.True(Cv2.Mean(bottomRightAreaOfTopLeftFrame).Val0 < 40, "右下角被别的贴片污染");
    }
}
