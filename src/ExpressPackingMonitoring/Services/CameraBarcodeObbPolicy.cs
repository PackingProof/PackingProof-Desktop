using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 旋转框的几何计算：角度归一化 + 重叠抑制。
///
/// 单独拆出来是为了能脱离模型单独测：模型文件和 ONNX Runtime 都不随仓库
/// 分发，端到端测试在 CI 上跑不了，但这部分逻辑必须始终有人盯着。
/// </summary>
internal static class CameraBarcodeObbPolicy
{
    /// <summary>
    /// 把角度折到「长轴与水平线的夹角」，范围 (-45, 45]。
    ///
    /// 摆正只关心条纹方向模 90 的结果：长轴和短轴差 90 度是同一个框，
    /// 而超过 45 度的时候走另一头更近（转 30 度和转 -60 度摆出来一样，
    /// 但转 30 度插值损失更小）。
    /// </summary>
    internal static double NormalizeAxisAngle(double degrees)
    {
        double angle = degrees % 90.0;
        if (angle > 45)
            angle -= 90;
        else if (angle < -45)
            angle += 90;
        return angle;
    }

    /// <summary>
    /// 贪心抑制重叠框，保留分数高的。
    /// </summary>
    internal static List<LabelObbDetection> SuppressOverlaps(
        IReadOnlyList<LabelObbDetection> detections,
        float iouThreshold)
    {
        var ordered = new List<LabelObbDetection>(detections);
        ordered.Sort((a, b) => b.Score.CompareTo(a.Score));

        var kept = new List<LabelObbDetection>();
        foreach (LabelObbDetection candidate in ordered)
        {
            bool overlapped = false;
            foreach (LabelObbDetection accepted in kept)
            {
                if (IntersectionOverUnion(candidate, accepted) > iouThreshold)
                {
                    overlapped = true;
                    break;
                }
            }
            if (!overlapped)
                kept.Add(candidate);
        }
        return kept;
    }

    /// <summary>两个旋转框的 IoU（交并比）。</summary>
    internal static double IntersectionOverUnion(in LabelObbDetection a, in LabelObbDetection b)
    {
        Point2f[] polygonA = ToCorners(a);
        Point2f[] polygonB = ToCorners(b);

        double intersection = PolygonArea(ClipToConvex(polygonA, polygonB));
        if (intersection <= 0)
            return 0;

        double union = PolygonArea(polygonA) + PolygonArea(polygonB) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    /// <summary>旋转框的四个角点（图像坐标，y 向下）。</summary>
    internal static Point2f[] ToCorners(in LabelObbDetection detection)
    {
        double radians = detection.AngleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double halfWidth = detection.Width / 2.0;
        double halfHeight = detection.Height / 2.0;

        var corners = new Point2f[4];
        var local = new (double X, double Y)[]
        {
            (-halfWidth, -halfHeight),
            (halfWidth, -halfHeight),
            (halfWidth, halfHeight),
            (-halfWidth, halfHeight)
        };
        for (int i = 0; i < 4; i++)
        {
            corners[i] = new Point2f(
                (float)(detection.CenterX + local[i].X * cos - local[i].Y * sin),
                (float)(detection.CenterY + local[i].X * sin + local[i].Y * cos));
        }
        return corners;
    }

    /// <summary>
    /// 用凸多边形 clip 裁剪 subject（Sutherland–Hodgman）。
    /// 两个旋转框都是凸的，所以这个裁剪的结果就是交集多边形。
    /// </summary>
    private static List<Point2f> ClipToConvex(Point2f[] subject, Point2f[] clip)
    {
        var output = new List<Point2f>(subject);
        for (int i = 0; i < clip.Length && output.Count > 0; i++)
        {
            Point2f edgeStart = clip[i];
            Point2f edgeEnd = clip[(i + 1) % clip.Length];
            var input = output;
            output = new List<Point2f>(input.Count + 4);

            for (int j = 0; j < input.Count; j++)
            {
                Point2f current = input[j];
                Point2f previous = input[(j + input.Count - 1) % input.Count];
                bool currentInside = IsInside(edgeStart, edgeEnd, current);
                bool previousInside = IsInside(edgeStart, edgeEnd, previous);

                if (currentInside)
                {
                    if (!previousInside)
                        output.Add(Intersect(previous, current, edgeStart, edgeEnd));
                    output.Add(current);
                }
                else if (previousInside)
                {
                    output.Add(Intersect(previous, current, edgeStart, edgeEnd));
                }
            }
        }
        return output;
    }

    private static bool IsInside(Point2f edgeStart, Point2f edgeEnd, Point2f point)
    {
        // 角点是顺时针给的（图像坐标系 y 向下），内部点在这条边的右侧。
        double cross = (edgeEnd.X - edgeStart.X) * (point.Y - edgeStart.Y)
                       - (edgeEnd.Y - edgeStart.Y) * (point.X - edgeStart.X);
        return cross >= 0;
    }

    private static Point2f Intersect(Point2f a, Point2f b, Point2f c, Point2f d)
    {
        double x1 = a.X, y1 = a.Y, x2 = b.X, y2 = b.Y;
        double x3 = c.X, y3 = c.Y, x4 = d.X, y4 = d.Y;
        double denominator = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
        if (Math.Abs(denominator) < 1e-9)
            return a;

        double t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / denominator;
        return new Point2f((float)(x1 + t * (x2 - x1)), (float)(y1 + t * (y2 - y1)));
    }

    private static double PolygonArea(IReadOnlyList<Point2f> polygon)
    {
        if (polygon.Count < 3)
            return 0;
        double area = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            Point2f current = polygon[i];
            Point2f next = polygon[(i + 1) % polygon.Count];
            area += (double)current.X * next.Y - (double)next.X * current.Y;
        }
        return Math.Abs(area) / 2.0;
    }
}
