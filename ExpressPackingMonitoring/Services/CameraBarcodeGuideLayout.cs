using System.Windows;

namespace ExpressPackingMonitoring.Services;

/// <summary>识别框四角的拖拽把手</summary>
public enum CameraBarcodeGuideHandle
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>
/// 识别框的显示换算。主界面拖动识别框、设置里的滑块和识别实际使用的取景矩形共用同一套比例定义：
/// 宽高按画面宽高的比例，偏移按四周留白的比例（0 表示居中，±1 表示贴边）。
///
/// 存下来的几何一律按<b>摄像头原生画面坐标</b>理解（即还没按配置旋转的那一帧），
/// 显示、拖动回写、裁剪与识别都先按当前旋转角度换算到"旋转后的帧坐标"再用，
/// 否则改旋转之后框会指向另一块区域（90° 时宽高比例还会整个颠倒）。
/// </summary>
public static class CameraBarcodeGuideLayout
{
    /// <summary>与设置里的滑块、配置归一化保持一致的最小识别框比例</summary>
    public const double MinRatio = 0.3;

    /// <summary>识别框最大到整幅画面</summary>
    public const double MaxRatio = 1.0;

    /// <summary>
    /// 把原生画面坐标里的几何换算到"按 <paramref name="degrees"/> 旋转之后的画面坐标"。
    ///
    /// 比例几何与像素尺寸无关，换算只在归一化后的 [0,1]² 里做一次坐标旋转：
    /// 顺时针 90° 时宽高比例互换、偏移整体转 90°；180°/270° 同理。
    /// 旋转不改变比例取值范围，所以换算前后都不会被 30%~100% 的夹紧规则改形。
    /// </summary>
    public static CameraBarcodeGuideGeometry Rotate(CameraBarcodeGuideGeometry geometry, int degrees) =>
        CameraFrameOrientation.NormalizeDegrees(degrees) switch
        {
            90 => new CameraBarcodeGuideGeometry(
                geometry.HeightRatio,
                geometry.WidthRatio,
                -geometry.OffsetY,
                geometry.OffsetX),
            180 => new CameraBarcodeGuideGeometry(
                geometry.WidthRatio,
                geometry.HeightRatio,
                -geometry.OffsetX,
                -geometry.OffsetY),
            270 => new CameraBarcodeGuideGeometry(
                geometry.HeightRatio,
                geometry.WidthRatio,
                geometry.OffsetY,
                -geometry.OffsetX),
            _ => geometry,
        };

    /// <summary>把"旋转后的帧坐标"里的几何还原回原生画面坐标：写回配置时用。</summary>
    public static CameraBarcodeGuideGeometry RotateInverse(
        CameraBarcodeGuideGeometry geometry,
        int degrees) =>
        Rotate(geometry, (360 - CameraFrameOrientation.NormalizeDegrees(degrees)) % 360);

    /// <summary>
    /// 把放大取景框规范成"和画面同长宽比"的一块（宽高比例相等、偏移按画面留白）。
    ///
    /// 放大裁的本来就是"以框中心为中心、按画面比例"的一大块，框却是任意形状：
    /// 框不方的时候，放出来的画面就会比框多出一圈，"框住的地方填满画面"自然对不上。
    /// 规范之后框住的那块 = 放大后填满画面，不多不少，也不会拉伸变形
    /// （同长宽比裁出来再放大，就是等比放大）。尺寸取两轴里较大的比例、中心保持不变，
    /// 所以不会把用户已经调好的放大位置挪走。
    /// </summary>
    public static CameraBarcodeGuideGeometry NormalizeToFrameAspect(CameraBarcodeGuideGeometry geometry)
    {
        double widthRatio = Clamp(geometry.WidthRatio, MinRatio, MaxRatio);
        double heightRatio = Clamp(geometry.HeightRatio, MinRatio, MaxRatio);

        // 已经是画面比例：原样返回，避免多算一遍浮点把已经摆好的框挪出零点几个像素。
        if (Math.Abs(widthRatio - heightRatio) < 1e-9)
        {
            return new CameraBarcodeGuideGeometry(
                widthRatio,
                heightRatio,
                Clamp(geometry.OffsetX, -1, 1),
                Clamp(geometry.OffsetY, -1, 1));
        }

        double ratio = Math.Max(widthRatio, heightRatio);

        // 先求出原来的框中心（按各自的宽高留白算），规范时中心不动。
        double marginX = (1 - widthRatio) / 2.0;
        double marginY = (1 - heightRatio) / 2.0;
        double centerX = (marginX * (1 + Clamp(geometry.OffsetX, -1, 1))) + (widthRatio / 2.0);
        double centerY = (marginY * (1 + Clamp(geometry.OffsetY, -1, 1))) + (heightRatio / 2.0);

        double margin = (1 - ratio) / 2.0;
        double offsetX = margin > 1e-9
            ? (centerX - (ratio / 2.0) - margin) / margin
            : 0;
        double offsetY = margin > 1e-9
            ? (centerY - (ratio / 2.0) - margin) / margin
            : 0;

        return new CameraBarcodeGuideGeometry(
            ratio,
            ratio,
            Clamp(offsetX, -1, 1),
            Clamp(offsetY, -1, 1));
    }

    /// <summary>Stretch=Uniform 时画面在预览控件里实际占据的矩形，含上下或左右黑边</summary>
    public static Rect GetVideoRect(
        double sourceWidth,
        double sourceHeight,
        double displayWidth,
        double displayHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || displayWidth <= 0 || displayHeight <= 0)
            return Rect.Empty;

        double scale = Math.Min(displayWidth / sourceWidth, displayHeight / sourceHeight);
        double width = sourceWidth * scale;
        double height = sourceHeight * scale;
        return new Rect(
            (displayWidth - width) / 2.0,
            (displayHeight - height) / 2.0,
            width,
            height);
    }

    /// <summary>把比例几何换算成预览里的矩形，结果与识别取景矩形同源</summary>
    public static Rect ToDisplayRect(CameraBarcodeGuideGeometry geometry, Rect videoRect)
    {
        if (videoRect.IsEmpty || videoRect.Width <= 0 || videoRect.Height <= 0)
            return Rect.Empty;

        double widthRatio = Clamp(geometry.WidthRatio, MinRatio, MaxRatio);
        double heightRatio = Clamp(geometry.HeightRatio, MinRatio, MaxRatio);
        double width = videoRect.Width * widthRatio;
        double height = videoRect.Height * heightRatio;
        double marginX = (videoRect.Width - width) / 2.0;
        double marginY = (videoRect.Height - height) / 2.0;
        return new Rect(
            videoRect.X + marginX * (1 + Clamp(geometry.OffsetX, -1, 1)),
            videoRect.Y + marginY * (1 + Clamp(geometry.OffsetY, -1, 1)),
            width,
            height);
    }

    /// <summary>把预览里的矩形换算回比例几何，超出的部分收回画面内</summary>
    public static CameraBarcodeGuideGeometry FromDisplayRect(Rect rect, Rect videoRect)
    {
        if (videoRect.IsEmpty || videoRect.Width <= 0 || videoRect.Height <= 0)
            return CameraBarcodeGuideGeometry.Default;

        double width = Clamp(rect.Width, videoRect.Width * MinRatio, videoRect.Width);
        double height = Clamp(rect.Height, videoRect.Height * MinRatio, videoRect.Height);
        double left = Clamp(rect.X, videoRect.Left, videoRect.Right - width);
        double top = Clamp(rect.Y, videoRect.Top, videoRect.Bottom - height);

        double marginX = (videoRect.Width - width) / 2.0;
        double marginY = (videoRect.Height - height) / 2.0;
        double offsetX = marginX > 0.5
            ? (left - videoRect.X - marginX) / marginX
            : 0;
        double offsetY = marginY > 0.5
            ? (top - videoRect.Y - marginY) / marginY
            : 0;

        return new CameraBarcodeGuideGeometry(
            width / videoRect.Width,
            height / videoRect.Height,
            Clamp(offsetX, -1, 1),
            Clamp(offsetY, -1, 1));
    }

    /// <summary>整体平移识别框，超出画面的部分停在画面边缘</summary>
    public static CameraBarcodeGuideGeometry Move(
        CameraBarcodeGuideGeometry geometry,
        Rect videoRect,
        double deltaX,
        double deltaY)
    {
        Rect rect = ToDisplayRect(geometry, videoRect);
        if (rect.IsEmpty)
            return geometry;

        rect.Offset(deltaX, deltaY);
        return FromDisplayRect(rect, videoRect);
    }

    /// <summary>拖动把手改变识别框大小，对角的边保持不动</summary>
    public static CameraBarcodeGuideGeometry Resize(
        CameraBarcodeGuideGeometry geometry,
        Rect videoRect,
        CameraBarcodeGuideHandle handle,
        double deltaX,
        double deltaY,
        bool keepFrameAspect = false)
    {
        Rect rect = ToDisplayRect(geometry, videoRect);
        if (rect.IsEmpty)
            return geometry;

        bool movesLeft = handle is CameraBarcodeGuideHandle.TopLeft or CameraBarcodeGuideHandle.BottomLeft;
        bool movesTop = handle is CameraBarcodeGuideHandle.TopLeft or CameraBarcodeGuideHandle.TopRight;
        double left = rect.Left;
        double top = rect.Top;
        double right = rect.Right;
        double bottom = rect.Bottom;

        if (movesLeft)
            left = Clamp(left + deltaX, videoRect.Left, videoRect.Right);
        else
            right = Clamp(right + deltaX, videoRect.Left, videoRect.Right);

        if (movesTop)
            top = Clamp(top + deltaY, videoRect.Top, videoRect.Bottom);
        else
            bottom = Clamp(bottom + deltaY, videoRect.Top, videoRect.Bottom);

        // 把手推过对角边时保持最小尺寸，避免把识别框拖成一条线
        double minWidth = videoRect.Width * MinRatio;
        double minHeight = videoRect.Height * MinRatio;
        if (right - left < minWidth)
        {
            if (movesLeft)
                left = Math.Max(videoRect.Left, right - minWidth);
            else
                right = Math.Min(videoRect.Right, left + minWidth);
        }
        if (bottom - top < minHeight)
        {
            if (movesTop)
                top = Math.Max(videoRect.Top, bottom - minHeight);
            else
                bottom = Math.Min(videoRect.Bottom, top + minHeight);
        }

        var resized = new Rect(left, top, right - left, bottom - top);
        if (keepFrameAspect)
            resized = SnapToFrameAspect(resized, videoRect, handle);

        return FromDisplayRect(resized, videoRect);
    }

    /// <summary>
    /// 把拖动出来的矩形收成"和画面同长宽比"的一块：被拖动把手的对角保持不动，
    /// 尺寸取两个方向变化的平均（都按画面比例折算），再夹进画面。
    ///
    /// 不能"哪一轴动得多就听哪一轴"：横竖增量接近时，这一下按横、那一下按竖，
    /// 框会在两个尺寸之间来回跳（拖起来一直抽搐）。取平均之后，每一步的缩放量都由
    /// 这一步的拖动量唯一决定，只往一边拖也会整体缩放，手感是连续、可预期的。
    /// </summary>
    private static Rect SnapToFrameAspect(
        Rect resized,
        Rect videoRect,
        CameraBarcodeGuideHandle handle)
    {
        double ratio = (
            (resized.Width / videoRect.Width)
            + (resized.Height / videoRect.Height)) / 2.0;
        ratio = Clamp(ratio, MinRatio, MaxRatio);

        bool movesLeft = handle is CameraBarcodeGuideHandle.TopLeft or CameraBarcodeGuideHandle.BottomLeft;
        bool movesTop = handle is CameraBarcodeGuideHandle.TopLeft or CameraBarcodeGuideHandle.TopRight;
        double anchorX = movesLeft ? resized.Right : resized.Left;
        double anchorY = movesTop ? resized.Bottom : resized.Top;

        // 固定角在画面里能撑出的最大边长：贴着边就收一点，仍然保持画面比例。
        double availableWidth = movesLeft ? anchorX - videoRect.Left : videoRect.Right - anchorX;
        double availableHeight = movesTop ? anchorY - videoRect.Top : videoRect.Bottom - anchorY;
        double limit = Math.Min(
            availableWidth / videoRect.Width,
            availableHeight / videoRect.Height);
        if (limit > 0)
            ratio = Math.Min(ratio, limit);
        ratio = Clamp(ratio, MinRatio, MaxRatio);

        double width = ratio * videoRect.Width;
        double height = ratio * videoRect.Height;
        return new Rect(
            movesLeft ? anchorX - width : anchorX,
            movesTop ? anchorY - height : anchorY,
            width,
            height);
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}
