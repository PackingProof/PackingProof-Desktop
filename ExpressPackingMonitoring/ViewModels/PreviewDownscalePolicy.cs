namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 预览发布尺寸：按控件实际显示大小发布，而不是整帧 1920×1080。
    ///
    /// 每发布一帧的代价与像素数成正比：整帧克隆、UI 线程写位图、再把位图传上 GPU 缩放，
    /// 1080p 一帧就是约 6MB。预览控件通常只有几百像素宽，按显示尺寸发布能把这条链路
    /// 降到零头，满帧跑也不心疼；缩放在后台线程用 INTER_AREA 做，比 WPF 实时缩放更干净。
    /// </summary>
    internal static class PreviewDownscalePolicy
    {
        /// <summary>下限：再小就没有缩放的意义（也避免窗口很小时糊成一片）。</summary>
        internal const int MinimumWidth = 640;

        /// <summary>
        /// 算出该发布多大。返回 null 表示按原始尺寸发布（帧本来就比显示尺寸小）；
        /// 显示尺寸还没量到（窗口尚未布局）时按下限发布：宁可从便宜的一侧起步，
        /// 窗口一布局好就会上报真实宽度。
        /// 宽高都取偶数，避免下游按行对齐时出现奇数 stride。
        /// </summary>
        internal static (int Width, int Height)? ResolveTarget(int sourceWidth, int sourceHeight, int displayWidth)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0)
                return null;

            int target = Math.Clamp(displayWidth, MinimumWidth, sourceWidth);
            if (target >= sourceWidth)
                return null;

            int width = target - (target % 2);
            int height = (int)Math.Round(sourceHeight * (double)width / sourceWidth);
            height = Math.Max(2, height - (height % 2));
            return (width, height);
        }
    }
}
