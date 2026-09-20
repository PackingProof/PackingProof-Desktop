namespace ExpressPackingMonitoring.UI;

/// <summary>
/// 回放控制条的窄宽度策略：窗口拉窄时按钮只留图标，把宽度让给进度条和时间。
/// </summary>
internal static class PlaybackControlBarPolicy
{
    /// <summary>
    /// 低于这个宽度就切换成纯图标：图标+文字的两个按钮约占 200px，进度条最少要 120px，
    /// 时间标签约 130px，再加左右内边距，600 上下已经是进度条开始被挤扁的点。
    /// </summary>
    internal const double IconOnlyWidth = 620;

    internal static bool UseIconOnlyButtons(double availableWidth) =>
        availableWidth > 0 && availableWidth < IconOnlyWidth;
}

/// <summary>
/// 回放时间标签：只显示 分:秒（分钟不封顶，2 小时 5 分显示成 125:00）。
/// </summary>
internal static class PlaybackTimeLabelFormatter
{
    internal static string Format(long milliseconds)
    {
        if (milliseconds < 0)
            milliseconds = 0;

        TimeSpan time = TimeSpan.FromMilliseconds(milliseconds);
        int totalMinutes = (int)Math.Floor(time.TotalMinutes);
        return $"{totalMinutes:00}:{time.Seconds:00}";
    }

    internal static string FormatRange(long currentMilliseconds, long lengthMilliseconds) =>
        $"{Format(currentMilliseconds)} / {Format(lengthMilliseconds)}";
}
