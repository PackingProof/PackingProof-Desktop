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
/// </summary>
public static class CameraBarcodeGuideLayout
{
    /// <summary>与设置里的滑块、配置归一化保持一致的最小识别框比例</summary>
    public const double MinRatio = 0.3;

    /// <summary>识别框最大到整幅画面</summary>
    public const double MaxRatio = 1.0;

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
        double deltaY)
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

        return FromDisplayRect(new Rect(left, top, right - left, bottom - top), videoRect);
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}
