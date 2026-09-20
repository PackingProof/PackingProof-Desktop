namespace ExpressPackingMonitoring.UI;

/// <summary>按录像分辨率算出来的窗口尺寸。</summary>
internal readonly record struct PlaybackWindowSize(double Width, double Height);

/// <summary>
/// 回放窗口尺寸：视频区严格贴住录像的宽高比，窗口再补上左侧列表、标题和控制条这些固定占位，
/// 这样打开窗口（以及切换到另一段录像）都不会再出现上下黑边。
///
/// 以**窗口高度**为主：高度取调用方给的期望高度（默认或当前高度，夹在工作区里），宽度由录像宽高比推出来。
/// 按宽度优先算的话，16:9 会把窗口压成又宽又扁的一条（现场反馈"16:9 太扁"）；
/// 竖屏（9:16）按比例算出来的宽度会低于窗口下限，这时宽度顶住下限，
/// 竖直方向多出来的空间由 LibVLC 居中留边 —— 绝不把窗口拉长到屏幕外（那样连进度条都看不见）。
/// </summary>
internal static class PlaybackWindowFitPolicy
{
    internal static PlaybackWindowSize? Calculate(
        int videoWidth,
        int videoHeight,
        double chromeWidth,
        double chromeHeight,
        double preferredWindowHeight,
        double workAreaWidth,
        double workAreaHeight,
        double minWindowWidth,
        double minWindowHeight)
    {
        if (videoWidth <= 0 || videoHeight <= 0)
            return null;
        if (workAreaWidth <= 0 || workAreaHeight <= 0)
            return null;

        chromeWidth = Math.Max(0, chromeWidth);
        chromeHeight = Math.Max(0, chromeHeight);
        minWindowWidth = Math.Max(0, minWindowWidth);
        minWindowHeight = Math.Max(0, minWindowHeight);

        double effectiveMinWidth = Math.Min(minWindowWidth, workAreaWidth);
        double effectiveMinHeight = Math.Min(minWindowHeight, workAreaHeight);
        double aspect = videoWidth / (double)videoHeight;

        double windowHeight = Math.Clamp(
            preferredWindowHeight > 0 ? preferredWindowHeight : workAreaHeight,
            effectiveMinHeight,
            workAreaHeight);
        double videoAreaHeight = Math.Max(1, windowHeight - chromeHeight);
        double windowWidth = chromeWidth + videoAreaHeight * aspect;

        if (windowWidth > workAreaWidth)
        {
            // 超宽比例（21:9 / 32:9）先把宽度夹进工作区，再按比例回算高度
            windowWidth = workAreaWidth;
            windowHeight = Math.Clamp(
                chromeHeight + Math.Max(1, windowWidth - chromeWidth) / aspect,
                effectiveMinHeight,
                workAreaHeight);
        }
        else if (windowWidth < effectiveMinWidth)
        {
            // 竖屏：宽度顶住窗口下限，竖直方向由播放器居中留边
            windowWidth = effectiveMinWidth;
        }

        return new PlaybackWindowSize(
            Math.Clamp(windowWidth, effectiveMinWidth, workAreaWidth),
            Math.Clamp(windowHeight, effectiveMinHeight, workAreaHeight));
    }
}
