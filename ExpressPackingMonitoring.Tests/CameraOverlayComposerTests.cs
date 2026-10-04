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

        Assert.True(ComposeWhole(main, secondary, rect));

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

        Assert.True(ComposeWhole(main, secondary, rect));

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

        Assert.True(ComposeWhole(main, secondary, rect));

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

        Assert.True(ComposeWhole(main, gray, rect));
    }

    [Fact]
    public void EmptyOrInvalidInputsLeaveFrameUntouched()
    {
        using var main = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(10, 20, 30));
        using var empty = new Mat();
        var rect = new CameraOverlayRect(100, 100, 320, 240);

        Assert.False(ComposeWhole(main, empty, rect));
        Assert.False(ComposeWhole(empty, main, rect));

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

        Assert.True(ComposeWhole(main, secondary, rect));

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

        Assert.False(ComposeWhole(main, secondary, new CameraOverlayRect(0, 0, 0, 0)));
        Assert.False(ComposeWhole(main, secondary, new CameraOverlayRect(100, 60, 40, 40)));

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
            Assert.True(ComposeWhole(reference, fresh, rect));
        }

        // 连续合成十帧：同一份叠加帧，第二次之后都命中缓存
        for (int i = 0; i < 10; i++)
            Assert.True(ComposeWhole(composed, overlay, rect));

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

        Assert.True(ComposeWhole(bottomRightFrame, overlay, bottomRight));
        Assert.True(ComposeWhole(topLeftFrame, overlay, topLeft));

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

    /// <summary>
    /// 圆角蒙版缓存到达上限时要挤掉最旧的那一份，绝不能把刚建好、正要贴上去的这一份释放掉。
    ///
    /// 现场表现：在主预览上拖画中画右下角把手改尺寸，副画面连同录像里的画中画一起消失，
    /// 日志每帧一条"叠加画面合成失败：Cannot access a disposed object. Object name: 'OpenCvSharp.Mat'."，
    /// 而且再也恢复不了——因为被释放的正是每帧都要用的那份蒙版。
    /// </summary>
    [Fact]
    public void MaskCacheEviction_NeverDisposesTheMaskInUse()
    {
        using var main = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(0, 0, 0));
        using var overlay = new Mat(960, 960, MatType.CV_8UC3, new Scalar(255, 255, 255));

        // 拖右下角把手改大小就是这个节奏：每帧换个宽度比例，裁一块出来再合成，
        // 落位尺寸每帧都不一样，很快把蒙版缓存撑过上限。
        for (int i = 0; i < 16; i++)
        {
            double widthRatio = 0.10 + (i * 0.02);
            var cropRect = new Rect(0, 0, overlay.Width, overlay.Height);
            CameraOverlayRect rect = CameraOverlayLayout
                .Resolve(1920, 1080, cropRect.Width, cropRect.Height, widthRatio, Margin, allowUpscale: false)!.Value;
            Assert.True(
                CameraOverlayComposer.TryCompose(main, overlay, cropRect, rect),
                $"拖动第 {i} 帧（宽度比例 {widthRatio:F2}）合成失败：蒙版缓存把正在用的那一份挤掉了");
        }

        // 尺寸定下来之后必须每帧都成功，而且真的贴进了主帧
        CameraOverlayRect steady = CameraOverlayLayout
            .Resolve(1920, 1080, overlay.Width, overlay.Height, WidthRatio, Margin, allowUpscale: false)!.Value;
        for (int i = 0; i < 8; i++)
            Assert.True(ComposeWhole(main, overlay, steady), $"定尺寸后第 {i} 帧合成失败");

        using var inside = new Mat(main, new Rect(steady.X + 12, steady.Y + 12, 24, 24));
        Assert.True(Cv2.Mean(inside).Val0 > 200, "尺寸稳定之后副画面没有贴上去");
    }

    /// <summary>
    /// 裁剪矩形必须真的作用到画中画内容上：画中画显示的是识别框框住的那一块，不是整幅叠加画面。
    /// 裁剪交给合成做之后（贴片缓存要按"叠加帧 + 裁剪矩形"做 key），这条得盯住。
    /// </summary>
    [Fact]
    public void CropRectSelectsTheOverlayContent()
    {
        using var main = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(0, 0, 0));
        using var overlay = new Mat(400, 400, MatType.CV_8UC3, new Scalar(0, 0, 0));
        // 左半幅白、右半幅黑：只裁右半，画中画里就该是黑的
        Cv2.Rectangle(overlay, new Rect(0, 0, 200, 400), new Scalar(255, 255, 255), thickness: -1);

        var cropRect = new Rect(200, 0, 200, 400);
        CameraOverlayRect rect = CameraOverlayLayout
            .Resolve(1920, 1080, cropRect.Width, cropRect.Height, WidthRatio, Margin, allowUpscale: false)!.Value;

        Assert.True(CameraOverlayComposer.TryCompose(main, overlay, cropRect, rect));

        // 往里 12px 采样：避开白色描边
        using var inside = new Mat(main, new Rect(rect.X + 12, rect.Y + 12, 24, 24));
        Assert.True(Cv2.Mean(inside).Val0 < 40, "裁剪矩形没有作用到画中画内容上");
    }

    /// <summary>
    /// 放大特写时小窗要能淡出：半透明那一下必须是"小窗颜色和主画面按比例混合"，
    /// 而不是整块消失或原样贴上去。
    /// </summary>
    [Fact]
    public void FadesOverlayByOpacityPercent()
    {
        CameraOverlayRect rect = CameraOverlayLayout
            .Resolve(1920, 1080, 640, 480, WidthRatio, Margin)!.Value;
        var sample = new Rect(rect.X + 12, rect.Y + 12, 24, 24);

        double SampleMean(int opacityPercent)
        {
            // 主画面纯黑、小窗纯白：混合结果的平均亮度就等于淡出后的不透明度
            using var main = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(0, 0, 0));
            using var secondary = new Mat(480, 640, MatType.CV_8UC3, new Scalar(255, 255, 255));
            Assert.True(CameraOverlayComposer.TryCompose(
                main,
                secondary,
                new Rect(0, 0, secondary.Width, secondary.Height),
                rect,
                opacityPercent));
            using var inside = new Mat(main, sample);
            return Cv2.Mean(inside).Val0;
        }

        Assert.True(SampleMean(100) > 200, "不淡出时小窗必须是实心的");
        Assert.InRange(SampleMean(50), 100, 155);
        Assert.True(SampleMean(0) < 40, "淡出到 0 时小窗必须完全看不见");

        // 全程淡出过程里每一帧都得能合成成功（蒙版缓存按档位走，不能把自己挤掉）
        for (int percent = 100; percent >= 0; percent -= 5)
            Assert.InRange(SampleMean(percent), 0, 255);
    }

    private static bool ComposeWhole(Mat frame, Mat overlay, CameraOverlayRect rect) =>
        CameraOverlayComposer.TryCompose(frame, overlay, new Rect(0, 0, overlay.Width, overlay.Height), rect);
}
