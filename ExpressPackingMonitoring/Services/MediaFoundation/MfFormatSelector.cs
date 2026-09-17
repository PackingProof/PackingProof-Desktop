namespace ExpressPackingMonitoring.Services.MediaFoundation;

/// <summary>
/// 从摄像头的原生格式清单里挑一个。
///
/// 挑选顺序体现三条取舍，都是为了避免在 CPU 上重复劳动：
///
/// 1. 优先原始 YUV（YUY2/NV12）。这样 YUV→RGB 由我们按正确系数做一次，
///    而不是让系统按 BT.601 解错再用逆矩阵掰回来（旧路径就是这样，
///    既损精度又白吃一个核心的 12%）。
/// 2. 其次 RGB24/RGB32。系统已经转好，虽然可能偏色，但至少不用解码器。
/// 3. 最后才是 MJPG 这类压缩格式，需要插解码器，CPU 开销最大。
///
/// 同一优先级内先按分辨率贴近目标、再按帧率贴近目标，
/// 分辨率差值权重远高于帧率：录像取证宁可帧率低一点也不能丢细节。
/// </summary>
internal static class MfFormatSelector
{
    /// <summary>格式家族的优先级，数字越小越好。</summary>
    private enum FormatRank
    {
        RawYuv = 0,
        PackedRgb = 1,
        Compressed = 2,
        Unusable = 3,
    }

    /// <summary>
    /// 选一个格式。清单为空、或全都不可用时返回 null，调用方应回退旧采集路径。
    /// </summary>
    internal static MfNativeFormat? Select(
        IReadOnlyList<MfNativeFormat>? formats,
        int targetWidth,
        int targetHeight,
        int targetFps)
    {
        if (formats == null || formats.Count == 0)
            return null;

        MfNativeFormat? best = null;
        long bestScore = long.MaxValue;
        foreach (MfNativeFormat format in formats)
        {
            FormatRank rank = RankOf(format);
            if (rank == FormatRank.Unusable)
                continue;

            long score = ScoreOf(format, rank, targetWidth, targetHeight, targetFps);
            if (score < bestScore)
            {
                bestScore = score;
                best = format;
            }
        }
        return best;
    }

    /// <summary>
    /// 打分，越小越好。家族优先级放在最高位，保证"原始 YUV 永远优先于压缩格式"，
    /// 不会被分辨率或帧率的差值翻盘。
    /// </summary>
    private static long ScoreOf(
        MfNativeFormat format,
        FormatRank rank,
        int targetWidth,
        int targetHeight,
        int targetFps)
    {
        long resolutionPenalty =
            Math.Abs(format.Width - Math.Max(1, targetWidth))
            + Math.Abs(format.Height - Math.Max(1, targetHeight));
        long fpsPenalty = targetFps > 0
            ? Math.Abs((long)Math.Round(format.FrameRate) - targetFps)
            : 0;

        // 分辨率权重 1000 倍于帧率：宁可 30fps 的 1080p，也不要 60fps 的 720p。
        return ((long)rank << 56) + resolutionPenalty * 1000 + fpsPenalty;
    }

    private static FormatRank RankOf(MfNativeFormat format)
    {
        if (format.Width <= 0 || format.Height <= 0)
            return FormatRank.Unusable;

        if (format.IsRawYuv)
            return FormatRank.RawYuv;

        if (format.Subtype == MfInterop.MFVideoFormat_RGB24
            || format.Subtype == MfInterop.MFVideoFormat_RGB32)
        {
            return FormatRank.PackedRgb;
        }

        if (format.Subtype == MfInterop.MFVideoFormat_MJPG)
            return FormatRank.Compressed;

        // 未知子类型不猜：我们没有对应的转换代码，拿到也处理不了。
        return FormatRank.Unusable;
    }
}
