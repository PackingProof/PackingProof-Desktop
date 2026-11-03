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
}

public static class VisionObbModelPolicy
{
    public const int InputSize = 640;
    public const float ScoreThreshold = 0.35f;
    public const float NmsThreshold = 0.45f;
    public const int MaxDetections = 6;
}

public sealed record VisionModelMetadata(
    IReadOnlyDictionary<string, string> CustomMetadata,
    string RuntimeVersion);

public interface IVisionObbDetector : IDisposable
{
    string ExecutionProvider { get; }
    IReadOnlyList<VisionObbDetection> Detect(Mat frame);
}

public interface IVisionObbDetectorFactory
{
    bool IsAvailable { get; }
    IVisionObbDetector? TryLoad(string? modelPath, bool preferDirectMl = true);
    VisionModelMetadata? ReadModelMetadata(string modelPath);
}
