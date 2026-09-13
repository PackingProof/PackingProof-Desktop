using System;
using System.Windows;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>悬浮小窗的四个预设停靠角落。内部预设，不开放给用户自定义坐标。</summary>
    public enum FloatingPreviewCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    /// <summary>
    /// 小窗的停靠位置规则：关闭时按窗口中心判断更靠近哪个角落并记下来，
    /// 下次打开直接贴到那个预设位置。只记角落不记坐标，换分辨率或换显示器也不会跑到屏幕外。
    /// </summary>
    internal static class FloatingPreviewPlacement
    {
        /// <summary>
        /// 离屏幕边缘的留白。顶部要避开窗口标题栏的最小化/最大化/关闭按钮，
        /// 所以比其它方向留得更宽，免得小窗压着这些按钮不好点。
        /// </summary>
        internal const double EdgeMargin = 24;
        internal const double TopEdgeMargin = 64;

        /// <summary>按窗口中心落在工作区的哪个象限，判断它更偏向哪个角落。</summary>
        public static FloatingPreviewCorner ResolveCorner(Rect window, Rect workArea)
        {
            double centerX = window.Left + window.Width / 2;
            double centerY = window.Top + window.Height / 2;

            bool isLeft = centerX < workArea.Left + workArea.Width / 2;
            bool isTop = centerY < workArea.Top + workArea.Height / 2;

            return (isLeft, isTop) switch
            {
                (true, true) => FloatingPreviewCorner.TopLeft,
                (false, true) => FloatingPreviewCorner.TopRight,
                (true, false) => FloatingPreviewCorner.BottomLeft,
                _ => FloatingPreviewCorner.BottomRight
            };
        }

        /// <summary>
        /// 算出指定角落的落点。窗口比工作区还大时一律贴左上，
        /// 保证标题栏和按钮始终留在屏幕内。
        /// </summary>
        public static Point ResolvePosition(
            FloatingPreviewCorner corner,
            Size windowSize,
            Rect workArea)
        {
            double left = corner is FloatingPreviewCorner.TopLeft or FloatingPreviewCorner.BottomLeft
                ? workArea.Left + EdgeMargin
                : workArea.Right - windowSize.Width - EdgeMargin;

            double top = corner is FloatingPreviewCorner.TopLeft or FloatingPreviewCorner.TopRight
                ? workArea.Top + TopEdgeMargin
                : workArea.Bottom - windowSize.Height - EdgeMargin;

            // 夹回工作区，窗口过大时至少保证左上角可见可拖。
            left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - windowSize.Width));
            top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - windowSize.Height));

            return new Point(left, top);
        }

        /// <summary>把配置里的角落名读回枚举，缺失或非法时退回右下角这个默认位置。</summary>
        public static FloatingPreviewCorner Parse(string? value) =>
            Enum.TryParse(value, ignoreCase: true, out FloatingPreviewCorner corner)
                ? corner
                : FloatingPreviewCorner.BottomRight;
    }
}
