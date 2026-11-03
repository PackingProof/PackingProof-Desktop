using ExpressPackingMonitoring.Vision;
using OpenCvSharp;
using WpfRect = System.Windows.Rect;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 根据本次识别选中的单个 OBB 面单计算实际放大裁切框。
/// 裁切框保持原始画面宽高比，完整包住旋转面单并预留稳定边距。
/// </summary>
internal static class VisionSmartZoomPolicy
{
    internal const double PaddingRatio = 0.10;
    internal const double MinPadding = 24;
    internal const double TargetSwitchMargin = 0.05;

    internal static VisionObbDetection? SelectTarget(
        IReadOnlyList<VisionObbDetection> detections,
        VisionObbDetection? decodedSelection,
        int frameWidth,
        int frameHeight,
        VisionObbDetection? previousTarget = null)
    {
        if (decodedSelection is { } decoded)
            return decoded;
        if (detections.Count == 0)
            return null;
        if (detections.Count == 1)
            return detections[0];
        if (frameWidth <= 0 || frameHeight <= 0)
            return null;

        var frame = new OpenCvSharp.Rect(0, 0, frameWidth, frameHeight);
        double maxArea = detections.Max(detection =>
            Math.Max(1, detection.Width * detection.Height));
        VisionObbDetection best = detections
            .OrderByDescending(detection =>
                CameraBarcodeObbDecoder.CalculateScore(
                    detection,
                    frame,
                    maxArea))
            .First();
        double bestScore = CameraBarcodeObbDecoder.CalculateScore(
            best,
            frame,
            maxArea);

        if (previousTarget is { } previous)
        {
            VisionObbDetection? previousMatch = null;
            double nearestDistance = double.PositiveInfinity;
            foreach (VisionObbDetection detection in detections)
            {
                double distance = Math.Sqrt(
                    Math.Pow(detection.CenterX - previous.CenterX, 2)
                    + Math.Pow(detection.CenterY - previous.CenterY, 2));
                double tolerance = Math.Max(
                    24,
                    Math.Min(detection.Width, detection.Height) * 0.25);
                if (distance <= tolerance && distance < nearestDistance)
                {
                    previousMatch = detection;
                    nearestDistance = distance;
                }
            }

            if (previousMatch is { } retained)
            {
                double previousScore = CameraBarcodeObbDecoder.CalculateScore(
                    retained,
                    frame,
                    maxArea);
                if (previousScore >= bestScore - TargetSwitchMargin)
                    return retained;
            }
        }

        return best;
    }

    internal static WpfRect ResolveTargetRect(
        in VisionObbDetection detection,
        int frameWidth,
        int frameHeight)
    {
        if (frameWidth <= 0
            || frameHeight <= 0
            || !double.IsFinite(detection.Width)
            || !double.IsFinite(detection.Height)
            || detection.Width <= 0
            || detection.Height <= 0)
        {
            return WpfRect.Empty;
        }

        Point2f[] corners = VisionObbGeometry.ToCorners(detection);
        double minX = corners.Min(point => point.X);
        double minY = corners.Min(point => point.Y);
        double maxX = corners.Max(point => point.X);
        double maxY = corners.Max(point => point.Y);
        if (!double.IsFinite(minX)
            || !double.IsFinite(minY)
            || !double.IsFinite(maxX)
            || !double.IsFinite(maxY))
        {
            return WpfRect.Empty;
        }

        double rotatedWidth = maxX - minX;
        double rotatedHeight = maxY - minY;
        if (rotatedWidth <= 0 || rotatedHeight <= 0)
            return WpfRect.Empty;

        double padding = Math.Max(
            MinPadding,
            Math.Min(rotatedWidth, rotatedHeight) * PaddingRatio);
        double desiredLeft = minX - padding;
        double desiredTop = minY - padding;
        double desiredRight = maxX + padding;
        double desiredBottom = maxY + padding;

        double frameAspect = frameWidth / (double)frameHeight;
        double cropWidth = Math.Max(
            desiredRight - desiredLeft,
            (desiredBottom - desiredTop) * frameAspect);
        double cropHeight = cropWidth / frameAspect;
        if (cropWidth >= frameWidth || cropHeight >= frameHeight)
            return new WpfRect(0, 0, frameWidth, frameHeight);

        double centerX = (desiredLeft + desiredRight) / 2.0;
        double centerY = (desiredTop + desiredBottom) / 2.0;
        double left = Math.Clamp(centerX - (cropWidth / 2.0), 0, frameWidth - cropWidth);
        double top = Math.Clamp(centerY - (cropHeight / 2.0), 0, frameHeight - cropHeight);
        return new WpfRect(left, top, cropWidth, cropHeight);
    }
}
