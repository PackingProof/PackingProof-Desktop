using System.Windows;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 固定版「面单放大」的裁剪换算：放大位置由主画面上的放大取景框决定，
/// 与识别结果、识别来源都无关 —— 所以识别来源选副画面时也不会再把副摄坐标系里的数值
/// 直接套到主画面上（那正是"放大位置跑到别处"的根因）。
///
/// 倍率完全由框决定：框住多大就放大铺满多大（画面尺寸 / 框尺寸），再没有第二个倍率参数；
/// 裁剪以框中心为中心，并夹进画面边界。
/// </summary>
internal static class ZoomCropPolicy
{
    /// <summary>
    /// 是否执行扫码放大。解锁识别框是在调整取景范围、进放大框编辑是在摆放大位置，
    /// 这两种状态下预览必须保持整帧，放大让位。
    /// </summary>
    internal static bool ShouldApplyZoom(bool zoomEnabled, bool guideLocked, bool editingZoomGuide) =>
        zoomEnabled && guideLocked && !editingZoomGuide;

    /// <summary>
    /// 本次放大的实际倍率：完全由放大取景框决定，框越小放大越近，最小 1 倍（不缩小）。
    /// 框无效（没算出来）时按 1 倍处理：预览保持整帧，绝不放大成一块看不清的糊图。
    /// </summary>
    internal static double ResolveScale(int frameWidth, int frameHeight, Rect zoomBox)
    {
        if (frameWidth <= 0 || frameHeight <= 0
            || zoomBox.IsEmpty || zoomBox.Width <= 0 || zoomBox.Height <= 0)
        {
            return 1.0;
        }

        double boxScale = Math.Min(frameWidth / zoomBox.Width, frameHeight / zoomBox.Height);
        return Math.Max(1.0, boxScale);
    }

    /// <summary>
    /// 放大过程中裁剪窗口的中心：从整帧中心平滑滑到放大取景框中心。
    /// <paramref name="progress"/> 传已缓动过的进度（0 = 还没开始平移，1 = 已到位），
    /// 与倍率共用同一条曲线，缩放和平移才会同时起步、同时停稳。
    /// </summary>
    internal static Point ResolvePanCenter(
        int frameWidth,
        int frameHeight,
        Rect zoomBox,
        double progress)
    {
        double startX = frameWidth / 2.0;
        double startY = frameHeight / 2.0;
        if (zoomBox.IsEmpty || zoomBox.Width <= 0 || zoomBox.Height <= 0)
            return new Point(startX, startY);

        double eased = Math.Clamp(progress, 0.0, 1.0);
        double targetX = zoomBox.X + (zoomBox.Width / 2.0);
        double targetY = zoomBox.Y + (zoomBox.Height / 2.0);
        return new Point(
            startX + ((targetX - startX) * eased),
            startY + ((targetY - startY) * eased));
    }

    /// <summary>
    /// 以放大取景框中心为中心的裁剪矩形（画面像素坐标），越界时夹回画面内。
    /// 框为空时按画面中心裁，保证任何情况下都不会算出越界或负尺寸的 ROI。
    /// </summary>
    internal static Rect CreateCropRect(
        int frameWidth,
        int frameHeight,
        double scale,
        Rect zoomBox)
    {
        double centerX = zoomBox.IsEmpty ? frameWidth / 2.0 : zoomBox.X + (zoomBox.Width / 2.0);
        double centerY = zoomBox.IsEmpty ? frameHeight / 2.0 : zoomBox.Y + (zoomBox.Height / 2.0);
        return CreateCropRect(frameWidth, frameHeight, scale, centerX, centerY);
    }

    /// <summary>
    /// 以指定中心裁剪（画面像素坐标）：放大动画里中心是逐帧插值出来的，
    /// 所以这里必须能显式给中心，而不是每次都回到取景框中心。
    /// </summary>
    internal static Rect CreateCropRect(
        int frameWidth,
        int frameHeight,
        double scale,
        double centerX,
        double centerY)
    {
        if (frameWidth <= 0 || frameHeight <= 0)
            return Rect.Empty;

        double safeScale = Math.Max(1.0, scale);
        double width = Math.Clamp(frameWidth / safeScale, 1, frameWidth);
        double height = Math.Clamp(frameHeight / safeScale, 1, frameHeight);
        double left = Math.Clamp(centerX - (width / 2.0), 0, frameWidth - width);
        double top = Math.Clamp(centerY - (height / 2.0), 0, frameHeight - height);
        return new Rect(left, top, width, height);
    }
}
