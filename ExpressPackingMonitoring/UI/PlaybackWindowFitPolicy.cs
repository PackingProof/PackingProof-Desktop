namespace ExpressPackingMonitoring.UI;

/// <summary>按录像分辨率算出来的窗口尺寸。</summary>
internal readonly record struct PlaybackWindowSize(double Width, double Height);

/// <summary>
/// 回放窗口尺寸：视频区严格贴住录像的宽高比，窗口再补上左侧列表、标题和控制条这些固定占位，
/// 这样打开窗口（以及切换到另一段录像）都不会再出现上下黑边。
///
/// 宽度以调用方给的期望宽度为准（默认窗口宽度，或用户当前宽度），高度按比例换算，
/// 但**只在窗口下限到"期望高度/工作区高度"之间**取值：宽高比撑不满时宁可让 LibVLC 居中留边，
/// 也不把窗口拉长/拉宽 —— 上一次的写法在竖屏录像（9:16）上会把窗口顶到接近满屏高，
/// 窗口位置却没跟着上移，下半截连进度条一起跑到屏幕外（现场反馈）。
/// </summary>
internal static class PlaybackWindowFitPolicy
{
    internal static PlaybackWindowSize? Calculate(
        int videoWidth,
        int videoHeight,
        double chromeWidth,
        double chromeHeight,
        double preferredWindowWidth,
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

        double windowWidth = Math.Clamp(preferredWindowWidth, effectiveMinWidth, workAreaWidth);
        double videoAreaWidth = Math.Max(1, windowWidth - chromeWidth);
        double windowHeight = chromeHeight + videoAreaWidth / aspect;

        // 不主动把窗口变得比现在更高：竖屏录像就是这一条把它挡在屏幕内的。
        double maximumHeight = Math.Clamp(
            preferredWindowHeight > 0 ? preferredWindowHeight : workAreaHeight,
            effectiveMinHeight,
            workAreaHeight);
        windowHeight = Math.Clamp(windowHeight, effectiveMinHeight, maximumHeight);

        return new PlaybackWindowSize(
            Math.Clamp(windowWidth, effectiveMinWidth, workAreaWidth),
            Math.Clamp(windowHeight, effectiveMinHeight, workAreaHeight));
    }
}
