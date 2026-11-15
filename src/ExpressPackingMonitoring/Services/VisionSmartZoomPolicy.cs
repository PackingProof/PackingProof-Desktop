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
        VisionObbDetection[] waybills = detections
            .Where(detection => VisionObbClassPolicy.IsWaybill(detection))
            .ToArray();
        if (decodedSelection is { } decoded)
        {
            if (VisionObbClassPolicy.IsWaybill(decoded))
                return decoded;
            if (VisionObbClassPolicy.IsBarcode(decoded))
            {
                VisionObbDetection? containing = FindContainingWaybill(
                    waybills,
                    decoded);
                if (containing is { } mapped)
                    return mapped;
            }
        }
        if (waybills.Length == 0)
            return null;
        if (waybills.Length == 1)
            return waybills[0];
        if (frameWidth <= 0 || frameHeight <= 0)
            return null;

        var frame = new OpenCvSharp.Rect(0, 0, frameWidth, frameHeight);
        double maxArea = waybills.Max(detection =>
            Math.Max(1, detection.Width * detection.Height));
        VisionObbDetection best = waybills
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
            foreach (VisionObbDetection detection in waybills)
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

    /// <summary>
    /// 把解码选中的 Barcode 映射回包住它的 Waybill，供界面选框和放大共用。
    /// 双类别模型返回的是 Barcode 检测，界面必须用面单框显示选中状态，
    /// 否则会退回只给取景框上色，看不出命中的是哪一张面单。
    /// </summary>
    internal static VisionObbDetection? ResolveWaybillSelection(
        IReadOnlyList<VisionObbDetection> detections,
        VisionObbDetection? selected)
    {
        if (selected is not { } value)
            return null;
        if (VisionObbClassPolicy.IsWaybill(value))
            return value;
        return VisionObbClassPolicy.IsBarcode(value)
            ? FindContainingWaybill(detections, value)
            : null;
    }

    internal static VisionObbDetection? FindContainingWaybill(
        IEnumerable<VisionObbDetection> detections,
        VisionObbDetection barcode) =>
        detections
            .Where(detection => VisionObbClassPolicy.IsWaybill(detection))
            .Where(waybill => VisionObbGeometry.Contains(waybill, barcode))
            .OrderBy(waybill => Math.Max(1, waybill.Width * waybill.Height))
            .Cast<VisionObbDetection?>()
            .FirstOrDefault();

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
