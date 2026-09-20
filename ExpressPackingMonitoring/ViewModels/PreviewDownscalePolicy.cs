namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 预览发布尺寸：按控件实际显示大小发布，而不是整帧 1920×1080。
    ///
    /// 每发布一帧的代价与像素数成正比：整帧克隆、UI 线程写位图、再把位图传上 GPU 缩放，
    /// 1080p 一帧就是约 6MB。预览控件通常只有几百像素宽，按显示尺寸发布能把这条链路
    /// 降低搬运开销；后台按缩放倍率选择双三次或面积采样，GPU 与 CPU 回退保持一致。
    ///
    /// 发布尺寸必须与源**严格同比例**。之前宽向下取偶、再把算出的高向下取偶，两次单向取整
    /// 让发布帧比源宽出 0.2%~0.4%：画面被横向拉伸，镜像套镜像时每层再乘一次，内层内容
    /// 每帧向外爬一点，肉眼就是"一直在左右抖"。小窗还会拿这个比例去改自身高度
    /// （见 FloatingPreviewWindow.SyncWindowToFrameAspectRatio），于是窗口几何也被取整残差带跑。
    /// </summary>
    internal static class PreviewDownscalePolicy
    {
        /// <summary>下限：再小就没有缩放的意义（也避免窗口很小时糊成一片）。</summary>
        internal const int MinimumWidth = 640;

        /// <summary>
        /// 允许的最大比例步长。16:9、4:3 这些常规比例约简后步长只有 16 和 4，按步长吸附
        /// 就能拿到严格同比例的尺寸；步长比这还大的说明是 1281×721 这类约简不掉的怪尺寸，
        /// 硬吸附会一步跨到原始尺寸，那时改走四舍五入分支。
        /// </summary>
        internal const int MaximumAspectStep = 64;

        /// <summary>
        /// 算出该发布多大。返回 null 表示按原始尺寸发布。
        ///
        /// 显示尺寸还没量到（窗口尚未布局）时按**原始尺寸**发布：宁可贵一点也绝不能被放大 ——
        /// 发布得比控件小，WPF 就会插值放大，画面立刻发糊，这正是镜像套镜像看着一层比一层糊
        /// 的来源之一。等窗口布局好上报真实宽度后，再按显示尺寸省开销。
        ///
        /// 吸附一律向上取步，理由同上：宁可比控件大几像素（多余部分被 WPF 缩掉，不损画质），
        /// 也不能比控件小。顺带的好处是尺寸按步长量化，控件宽度变动一两像素不会再让
        /// WriteableBitmap 反复重建。
        /// </summary>
        internal static (int Width, int Height)? ResolveTarget(int sourceWidth, int sourceHeight, int displayWidth)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0 || displayWidth <= 0)
                return null;

            int target = Math.Clamp(displayWidth, MinimumWidth, sourceWidth);
            if (target >= sourceWidth)
                return null;

            int divisor = GreatestCommonDivisor(sourceWidth, sourceHeight);
            int aspectWidth = sourceWidth / divisor;
            int aspectHeight = sourceHeight / divisor;
            if (aspectWidth <= MaximumAspectStep)
            {
                // 宽高同时是比例步长的整数倍，比例与源完全一致，没有任何取整残差。
                int steps = Math.Max(1, CeilingDivide(target, aspectWidth));
                int snappedWidth = steps * aspectWidth;
                return snappedWidth >= sourceWidth ? null : (snappedWidth, steps * aspectHeight);
            }

            // 约简不掉的怪尺寸：宽按控件宽度、高四舍五入。残差 ≤0.5px 且不带单向偏移，
            // 比原先的双向下取整小一个数量级。
            int height = Math.Max(1, (int)Math.Round(sourceHeight * (double)target / sourceWidth));
            return (target, height);
        }

        private static int CeilingDivide(int value, int divisor) => (value + divisor - 1) / divisor;

        private static int GreatestCommonDivisor(int left, int right)
        {
            while (right != 0)
                (left, right) = (right, left % right);

            return left;
        }
    }

    /// <summary>
    /// 是否要发布预览帧（硬停条件）。
    ///
    /// 停止条件：拖动窗口、用户主动关闭实时预览、已释放、摄像头休眠，以及**没有可见预览消费方**
    /// （主界面最小化或隐藏、且小窗也没显示）。最后一条是 issue #28 的核心：没人看就不发布，
    /// 不再走"尺寸未知按原始尺寸发布"那条最费资源的路径。
    /// </summary>
    internal static class PreviewPublishPolicy
    {
        public static bool ShouldPublish(
            bool suppressedByWindowState,
            bool disabledByUser,
            bool disposed,
            bool cameraSleeping,
            bool hasVisiblePreviewConsumer)
            => !suppressedByWindowState
            && !disabledByUser
            && !disposed
            && !cameraSleeping
            && hasVisiblePreviewConsumer;
    }
}
