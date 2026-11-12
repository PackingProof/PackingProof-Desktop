namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 解码分辨率档位。
///
/// 现有解码链路只会把图缩小（PrepareDecodeSize 里 scale 取 Math.Min(1, …)），
/// 4K 画面缩到 1440 边长时缩掉 2.6 倍，面单上的条码条纹从 6 像素
/// 变成 2 像素，ZXing 采不到信息 —— 现场表现就是"面单远了就认不出来"。
///
/// 这里给出几档更细的分辨率，让常规解码失败后可以按档位重试：
/// 每一档都还是"只缩不放"，但缩得少一点，条码条纹保留得更多。
/// 档位只在原图真的更大时才有意义，1080p 摄像头用不上高档位。
/// </summary>
internal static class CameraBarcodeUpscalePolicy
{
    /// <summary>候选档位（解码图的最长边上限，像素）。</summary>
    internal static readonly int[] DefaultTierDimensions = [1440, 2160, 2880];

    /// <summary>
    /// 返回比基础档更细、且原图确实能撑得起来的档位。
    /// </summary>
    /// <param name="width">原图宽（像素）。</param>
    /// <param name="height">原图高（像素）。</param>
    /// <param name="baseDimension">常规解码使用的边长上限。</param>
    internal static IReadOnlyList<int> BuildTierDimensions(
        int width,
        int height,
        int baseDimension)
    {
        if (width <= 0 || height <= 0 || baseDimension <= 0)
            return [];

        int longest = Math.Max(width, height);
        var tiers = new List<int>();
        foreach (int dimension in DefaultTierDimensions)
        {
            // 不比基础档细：那一档常规解码已经用过了。
            if (dimension <= baseDimension)
                continue;
            // 原图还没这么大，放大只是把同一张图反复缩放，没有额外信息。
            if (dimension >= longest)
                continue;
            tiers.Add(dimension);
        }
        return tiers;
    }

    /// <summary>
    /// 某档位对应的像素上限，和解码器"边长 1440 / 像素 120 万"的配比一致。
    /// </summary>
    internal static long PixelsForDimension(int dimension)
    {
        double baseRatio = 1_200_000d / (1440d * 1440d);
        return (long)Math.Round(dimension * dimension * baseRatio);
    }
}
