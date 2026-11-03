namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 按旋转框的实际像素跨度判断是否值得进入 ZXing。
/// 长条面单或透视裁切即使短边偏小，对角线仍可能保留足够的条码像素。
/// </summary>
internal static class CameraBarcodeDecodeSizePolicy
{
    internal const double OneDimensionalMinDiagonal = 90;
    internal const double QrCodeMinDiagonal = 113;

    internal static bool ShouldDecode(int width, int height, bool qrCodeEnabled)
    {
        if (width <= 0 || height <= 0)
            return false;

        double diagonal = Math.Sqrt(
            ((double)width * width) + ((double)height * height));
        return qrCodeEnabled
            ? diagonal >= QrCodeMinDiagonal
            : diagonal >= OneDimensionalMinDiagonal;
    }
}
