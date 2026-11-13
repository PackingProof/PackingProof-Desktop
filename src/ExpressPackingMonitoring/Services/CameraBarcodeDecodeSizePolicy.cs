namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 按旋转框的实际像素跨度判断是否值得进入 ZXing。
/// 长条面单或透视裁切即使短边偏小，对角线仍可能保留足够的条码像素。
/// </summary>
internal static class CameraBarcodeDecodeSizePolicy
{
    internal const double OneDimensionalMinDiagonal = 90;
    internal const double QrCodeMinDiagonal = 113;
    internal const double Reference1080pDiagonalPixels = 2202.9071700822983;
    internal const double MinimumWaybillDiagonalAt1080P = 300;
    internal const double MinimumObbFrameDiagonalRatio =
        MinimumWaybillDiagonalAt1080P / Reference1080pDiagonalPixels;
    internal const double OneDimensionalMinFrameDiagonalRatio =
        MinimumObbFrameDiagonalRatio;
    internal const double QrCodeMinFrameDiagonalRatio =
        MinimumObbFrameDiagonalRatio;

    internal static bool ShouldDecode(int width, int height, bool qrCodeEnabled)
    {
        if (width <= 0 || height <= 0)
            return false;

        double diagonal = DiagonalPixels(width, height);
        return qrCodeEnabled
            ? diagonal >= QrCodeMinDiagonal
            : diagonal >= OneDimensionalMinDiagonal;
    }

    internal static bool ShouldDecode(
        int width,
        int height,
        int frameWidth,
        int frameHeight,
        bool qrCodeEnabled)
    {
        if (!ShouldDecode(width, height, qrCodeEnabled))
            return false;

        double ratio = FrameDiagonalRatio(
            width,
            height,
            frameWidth,
            frameHeight);
        return ratio >= (qrCodeEnabled
            ? QrCodeMinFrameDiagonalRatio
            : OneDimensionalMinFrameDiagonalRatio);
    }

    internal static double DiagonalPixels(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return 0;
        return Math.Sqrt(((double)width * width) + ((double)height * height));
    }

    internal static double FrameDiagonalRatio(
        int width,
        int height,
        int frameWidth,
        int frameHeight)
    {
        double frameDiagonal = DiagonalPixels(frameWidth, frameHeight);
        return frameDiagonal <= 0
            ? 0
            : DiagonalPixels(width, height) / frameDiagonal;
    }
}
