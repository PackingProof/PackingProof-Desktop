using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

internal static class SmartZoomPolicy
{
    internal const double BarcodeSafetyMargin = 1.15;

    internal static double GetBoundedScale(
        int frameWidth,
        int frameHeight,
        double requestedMaxScale,
        CameraBarcodeGeometry? barcodeBounds)
    {
        double requested = Math.Max(1.0, requestedMaxScale);
        if (barcodeBounds == null || barcodeBounds.Width <= 0 || barcodeBounds.Height <= 0)
            return requested;

        double widthLimit = frameWidth / (barcodeBounds.Width * BarcodeSafetyMargin);
        double heightLimit = frameHeight / (barcodeBounds.Height * BarcodeSafetyMargin);
        return Math.Max(1.0, Math.Min(requested, Math.Min(widthLimit, heightLimit)));
    }

    internal static Rect CreateCropRect(
        int frameWidth,
        int frameHeight,
        double scale,
        CameraBarcodeGeometry? barcodeBounds)
    {
        int width = Math.Clamp((int)Math.Round(frameWidth / Math.Max(1.0, scale)), 1, frameWidth);
        int height = Math.Clamp((int)Math.Round(frameHeight / Math.Max(1.0, scale)), 1, frameHeight);
        double centerX = barcodeBounds?.CenterX ?? frameWidth / 2.0;
        double centerY = barcodeBounds?.CenterY ?? frameHeight / 2.0;
        int left = Math.Clamp((int)Math.Round(centerX - width / 2.0), 0, frameWidth - width);
        int top = Math.Clamp((int)Math.Round(centerY - height / 2.0), 0, frameHeight - height);
        return new Rect(left, top, width, height);
    }
}
