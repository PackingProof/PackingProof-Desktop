using OpenCvSharp;

namespace ExpressPackingMonitoring.Vision;

/// <summary>视觉检测旋转框，坐标使用输入图像像素，角度为长轴与水平线夹角。</summary>
public readonly record struct VisionObbDetection(
    double CenterX,
    double CenterY,
    double Width,
    double Height,
    double AngleDegrees,
    float Score,
    int ClassId = 0);

public static class VisionObbGeometry
{
    public static Point2f[] ToCorners(in VisionObbDetection detection)
    {
        double radians = detection.AngleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double halfWidth = detection.Width / 2.0;
        double halfHeight = detection.Height / 2.0;
        var local = new (double X, double Y)[]
        {
            (-halfWidth, -halfHeight),
            (halfWidth, -halfHeight),
            (halfWidth, halfHeight),
            (-halfWidth, halfHeight)
        };
        var corners = new Point2f[4];
        for (int index = 0; index < corners.Length; index++)
        {
            corners[index] = new Point2f(
                (float)(detection.CenterX + local[index].X * cos - local[index].Y * sin),
                (float)(detection.CenterY + local[index].X * sin + local[index].Y * cos));
        }
        return corners;
    }

    public static bool Contains(
        in VisionObbDetection container,
        in VisionObbDetection contained,
        double tolerancePixels = 3)
    {
        if (tolerancePixels < 0 || !double.IsFinite(tolerancePixels))
            tolerancePixels = 0;
        Point[] outer = ToCorners(container)
            .Select(point => new Point(
                (int)Math.Round(point.X),
                (int)Math.Round(point.Y)))
            .ToArray();
        return ToCorners(contained).All(corner =>
            Cv2.PointPolygonTest(
                outer,
                new Point((int)Math.Round(corner.X), (int)Math.Round(corner.Y)),
                true) >= -tolerancePixels);
    }

    /// <summary>
    /// 条码被手/遮挡物吃掉一个角时，仍允许中心在面单内、至少三个角在面单内。
    /// 只用于解码候选筛选；绘制和智能跟随仍使用严格包含。
    /// </summary>
    public static bool ContainsWithCenterAndThreeCorners(
        in VisionObbDetection container,
        in VisionObbDetection contained,
        double tolerancePixels = 3)
    {
        if (tolerancePixels < 0 || !double.IsFinite(tolerancePixels))
            tolerancePixels = 0;
        Point[] outer = ToCorners(container)
            .Select(point => new Point(
                (int)Math.Round(point.X),
                (int)Math.Round(point.Y)))
            .ToArray();
        var center = new Point(
            (int)Math.Round(contained.CenterX),
            (int)Math.Round(contained.CenterY));
        if (Cv2.PointPolygonTest(outer, center, true) < -tolerancePixels)
            return false;

        int insideCorners = ToCorners(contained).Count(corner =>
            Cv2.PointPolygonTest(
                outer,
                new Point((int)Math.Round(corner.X), (int)Math.Round(corner.Y)),
                true) >= -tolerancePixels);
        return insideCorners >= 3;
    }
}

public static class VisionObbClassIds
{
    public const int Waybill = 0;
    public const int Barcode = 1;
}

public static class VisionObbClassPolicy
{
    public static bool IsWaybill(in VisionObbDetection detection) =>
        detection.ClassId == VisionObbClassIds.Waybill;

    public static bool IsBarcode(in VisionObbDetection detection) =>
        detection.ClassId == VisionObbClassIds.Barcode;
}

public static class VisionObbModelPolicy
{
    public const int InputSize = 640;
    public const float ScoreThreshold = 0.35f;
    public const float NmsThreshold = 0.45f;
    public const int MaxDetections = 12;
}

public sealed record VisionModelMetadata(
    IReadOnlyDictionary<string, string> CustomMetadata,
    string RuntimeVersion);

public readonly record struct VisionObbDetectorTiming(
    double PreprocessMs,
    double InferenceMs,
    double PostprocessMs);

public interface IVisionObbDetector : IDisposable
{
    string ExecutionProvider { get; }
    int ClassCount => 1;
    VisionObbDetectorTiming LastTiming => default;
    IReadOnlyList<VisionObbDetection> Detect(Mat frame);
}

public interface IVisionObbDetectorFactory
{
    bool IsAvailable { get; }
    IVisionObbDetector? TryLoad(string? modelPath, bool preferDirectMl = true);
    VisionModelMetadata? ReadModelMetadata(string modelPath);
}
