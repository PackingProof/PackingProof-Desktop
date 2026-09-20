namespace ExpressPackingMonitoring.UI;

/// <summary>按录像分辨率算出来的窗口尺寸。</summary>
internal readonly record struct PlaybackWindowSize(double Width, double Height);

/// <summary>
/// 回放窗口尺寸：视频区严格贴住录像的宽高比，窗口再补上左侧列表、标题和控制条这些固定占位，
/// 这样打开窗口（以及切换到另一段录像）都不会再出现上下黑边。
///
/// 宽度以调用方给的期望宽度为准（默认窗口宽度，或用户当前宽度），只按比例换算高度：
/// 竖屏录像（手机拍的）算出来会比窗口下限还窄，这时按宽度顶住下限、高度按同一比例回算；
/// 再顶不下就夹进可用工作区——宁可留一点黑边（居中显示），也不能把窗口放到屏幕外。
/// </summary>
internal static class PlaybackWindowFitPolicy
{
    internal static PlaybackWindowSize? Calculate(
        int videoWidth,
        int videoHeight,
        double chromeWidth,
        double chromeHeight,
        double preferredWindowWidth,
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

        if (windowHeight > workAreaHeight)
        {
            // 高度顶到工作区：按比例缩回去，宽度跟着变窄（竖屏就是这一支）
            windowHeight = workAreaHeight;
            windowWidth = Math.Clamp(
                chromeWidth + (windowHeight - chromeHeight) * aspect,
                effectiveMinWidth,
                workAreaWidth);
        }
        else if (windowHeight < effectiveMinHeight)
        {
            // 太扁的录像（超宽比例）会把窗口压到下限以下：顶住下限，宽度按比例放大
            windowHeight = effectiveMinHeight;
            windowWidth = Math.Clamp(
                chromeWidth + (windowHeight - chromeHeight) * aspect,
                effectiveMinWidth,
                workAreaWidth);
        }

        return new PlaybackWindowSize(
            Math.Clamp(windowWidth, effectiveMinWidth, workAreaWidth),
            Math.Clamp(windowHeight, effectiveMinHeight, workAreaHeight));
    }
}
