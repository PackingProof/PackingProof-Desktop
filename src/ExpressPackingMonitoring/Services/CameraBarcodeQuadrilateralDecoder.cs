using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 普通 ZXing 与 OBB 都失败后的轻量兜底：用轮廓找面单矩形，透视裁正后
/// 先直接解码，再按 Hough 主方向做 ±5° 修正。不加载模型，只处理取景框内画面。
/// </summary>
internal sealed class CameraBarcodeQuadrilateralDecoder : IDisposable
{
    private const int MaxWorkingDimension = 900;
    private const int MaxCandidates = 8;
    private const double MinimumAreaRatio = 0.02;

    private readonly CameraBarcodeFrameDecoder _standardDecoder;
    private bool _disposed;

    internal CameraBarcodeQuadrilateralDecoder(CameraBarcodeFrameDecoder standardDecoder)
    {
        _standardDecoder = standardDecoder ?? throw new ArgumentNullException(nameof(standardDecoder));
    }

    internal CameraBarcodeGeometry? LastGeometry { get; private set; }

    internal string? Decode(
        Mat frame,
        Func<string, bool>? isValid,
        CameraBarcodeGuideGeometry geometry)
    {
        LastGeometry = null;
        if (_disposed || frame == null || frame.IsDisposed || frame.Empty())
            return null;

        Rect guide = CameraBarcodeGuideLayout.ToFrameRect(
            frame.Width,
            frame.Height,
            geometry);
        if (guide.Width <= 0 || guide.Height <= 0)
            return null;

        using Mat guideImage = new(frame, guide);
        foreach (QuadrilateralCandidate candidate in FindCandidates(guideImage))
        {
            using Mat crop = WarpCandidate(guideImage, candidate);
            if (crop.Empty())
                continue;
            string? decoded = _standardDecoder.DecodeDetached(crop, isValid);
            if (IsAccepted(decoded, isValid))
            {
                SetGeometry(candidate, guide);
                return decoded;
            }

            double estimatedAngle = EstimateSkewAngle(crop);
            foreach (double offset in new[] { -5.0, 0.0, 5.0 })
            {
                using Mat corrected = Rotate(crop, estimatedAngle + offset);
                decoded = _standardDecoder.DecodeDetached(corrected, isValid);
                if (IsAccepted(decoded, isValid))
                {
                    SetGeometry(candidate, guide);
                    return decoded;
                }
            }
        }
        return null;
    }

    private static bool IsAccepted(string? decoded, Func<string, bool>? isValid)
    {
        if (string.IsNullOrWhiteSpace(decoded))
            return false;
        if (isValid is null)
            return true;
        try
        {
            return isValid(decoded);
        }
        catch
        {
            return false;
        }
    }

    private void SetGeometry(QuadrilateralCandidate candidate, Rect guide)
    {
        Rect bounds = candidate.Bounds;
        LastGeometry = new CameraBarcodeGeometry(
            guide.X + bounds.X,
            guide.Y + bounds.Y,
            bounds.Width,
            bounds.Height);
    }

    private static IReadOnlyList<QuadrilateralCandidate> FindCandidates(Mat source)
    {
        using Mat gray = new();
        if (source.Channels() == 1)
            source.CopyTo(gray);
        else
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);

        double scale = Math.Min(
            1.0,
            MaxWorkingDimension / (double)Math.Max(gray.Width, gray.Height));
        using Mat working = new();
        if (scale < 0.999)
            Cv2.Resize(gray, working, new Size(), scale, scale, InterpolationFlags.Area);
        else
            gray.CopyTo(working);

        using Mat blurred = new();
        using Mat edges = new();
        using Mat closed = new();
        Cv2.GaussianBlur(working, blurred, new Size(5, 5), 0);
        Cv2.Canny(blurred, edges, 40, 120);
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.MorphologyEx(edges, closed, MorphTypes.Close, kernel);

        Cv2.FindContours(
            closed,
            out Point[][] contours,
            out _,
            RetrievalModes.List,
            ContourApproximationModes.ApproxSimple);

        double minimumArea = working.Width * working.Height * MinimumAreaRatio;
        var candidates = new List<(Point[] Quad, double Area)>();
        foreach (Point[] contour in contours)
        {
            double area = Cv2.ContourArea(contour);
            if (area < minimumArea)
                continue;

            double perimeter = Cv2.ArcLength(contour, true);
            Point[] approx = Cv2.ApproxPolyDP(contour, perimeter * 0.025, true);
            Rect bounds = Cv2.BoundingRect(approx);
            double aspect = bounds.Width / (double)Math.Max(1, bounds.Height);
            if (aspect is < 0.2 or > 5.0)
                continue;

            if (approx.Length == 4 && Cv2.IsContourConvex(approx))
            {
                candidates.Add((approx, area));
                continue;
            }

            RotatedRect rotated = Cv2.MinAreaRect(contour);
            Point2f[] box = Cv2.BoxPoints(rotated);
            candidates.Add((
                box.Select(point => new Point(
                    (int)Math.Round(point.X),
                    (int)Math.Round(point.Y))).ToArray(),
                area));
        }

        var distinct = new List<(Point[] Quad, double Area)>();
        foreach ((Point[] quad, double area) in candidates.OrderByDescending(item => item.Area))
        {
            double centerX = quad.Average(point => point.X);
            double centerY = quad.Average(point => point.Y);
            if (distinct.Any(item =>
            {
                double existingX = item.Quad.Average(point => point.X);
                double existingY = item.Quad.Average(point => point.Y);
                return Math.Abs(existingX - centerX) < 12 &&
                    Math.Abs(existingY - centerY) < 12;
            }))
            {
                continue;
            }

            distinct.Add((quad, area));
            if (distinct.Count >= MaxCandidates)
                break;
        }

        double inverseScale = 1.0 / scale;
        return distinct
            .Select(item => CreateCandidate(item.Quad, inverseScale, source.Size()))
            .Where(candidate => candidate is not null)
            .Cast<QuadrilateralCandidate>()
            .OrderByDescending(candidate => candidate.Bounds.Width * candidate.Bounds.Height)
            .ToArray();
    }

    private static QuadrilateralCandidate? CreateCandidate(
        Point[] quad,
        double inverseScale,
        Size sourceSize)
    {
        Point2f[] scaled = quad
            .Select(point => new Point2f(
                (float)(point.X * inverseScale),
                (float)(point.Y * inverseScale)))
            .OrderBy(point => point.X + point.Y)
            .ToArray();
        if (scaled.Length != 4)
            return null;

        Point2f topLeft = scaled[0];
        Point2f bottomRight = scaled[3];
        Point2f topRight = scaled[1].X >= scaled[2].X ? scaled[1] : scaled[2];
        Point2f bottomLeft = scaled[1].X < scaled[2].X ? scaled[1] : scaled[2];
        Point2f[] ordered = [topLeft, topRight, bottomRight, bottomLeft];
        int width = (int)Math.Round(Math.Max(
            Distance(ordered[0], ordered[1]),
            Distance(ordered[3], ordered[2])));
        int height = (int)Math.Round(Math.Max(
            Distance(ordered[0], ordered[3]),
            Distance(ordered[1], ordered[2])));
        if (width < 40 || height < 40)
            return null;

        int minX = Math.Clamp(
            (int)Math.Floor(ordered.Min(point => point.X)),
            0,
            Math.Max(0, sourceSize.Width - 1));
        int minY = Math.Clamp(
            (int)Math.Floor(ordered.Min(point => point.Y)),
            0,
            Math.Max(0, sourceSize.Height - 1));
        int maxX = Math.Clamp(
            (int)Math.Ceiling(ordered.Max(point => point.X)),
            minX + 1,
            sourceSize.Width);
        int maxY = Math.Clamp(
            (int)Math.Ceiling(ordered.Max(point => point.Y)),
            minY + 1,
            sourceSize.Height);
        return new QuadrilateralCandidate(
            new Rect(minX, minY, maxX - minX, maxY - minY),
            ordered,
            width,
            height);
    }

    private static Mat WarpCandidate(
        Mat source,
        QuadrilateralCandidate candidate)
    {
        Point2f[] destination =
        [
            new(0, 0),
            new(candidate.Width - 1, 0),
            new(candidate.Width - 1, candidate.Height - 1),
            new(0, candidate.Height - 1)
        ];
        using Mat transform = Cv2.GetPerspectiveTransform(
            candidate.Corners,
            destination);
        var crop = new Mat();
        Cv2.WarpPerspective(
            source,
            crop,
            transform,
            new Size(candidate.Width, candidate.Height),
            InterpolationFlags.Cubic,
            BorderTypes.Constant,
            Scalar.White);
        return crop;
    }

    private static double Distance(Point2f left, Point2f right)
    {
        double dx = left.X - right.X;
        double dy = left.Y - right.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double EstimateSkewAngle(Mat source)
    {
        using Mat gray = new();
        if (source.Channels() == 1)
            source.CopyTo(gray);
        else
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);

        double scale = Math.Min(1.0, 1000.0 / Math.Max(gray.Width, gray.Height));
        using Mat working = new();
        if (scale < 0.999)
            Cv2.Resize(gray, working, new Size(), scale, scale, InterpolationFlags.Area);
        else
            gray.CopyTo(working);

        using Mat blurred = new();
        using Mat edges = new();
        Cv2.GaussianBlur(working, blurred, new Size(3, 3), 0);
        Cv2.Canny(blurred, edges, 50, 150);
        LineSegmentPoint[] lines = Cv2.HoughLinesP(
            edges,
            1,
            Math.PI / 180.0,
            40,
            Math.Max(30, Math.Min(working.Width, working.Height) / 20.0),
            12);

        double[] histogram = new double[90];
        foreach (LineSegmentPoint line in lines)
        {
            double dx = line.P2.X - line.P1.X;
            double dy = line.P2.Y - line.P1.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 20)
                continue;

            double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
            angle = (angle % 90.0 + 90.0) % 90.0;
            double weight = length * Math.Min(length, 180.0);
            int center = (int)Math.Round(angle) % 90;
            for (int offset = -2; offset <= 2; offset++)
            {
                int index = (center + offset + 90) % 90;
                histogram[index] += weight * (3 - Math.Abs(offset)) / 3.0;
            }
        }

        int best = 0;
        for (int index = 1; index < histogram.Length; index++)
        {
            if (histogram[index] > histogram[best])
                best = index;
        }
        return best > 45 ? best - 90 : best;
    }

    private static Mat Rotate(Mat source, double angle)
    {
        double radians = angle * Math.PI / 180.0;
        double cosine = Math.Abs(Math.Cos(radians));
        double sine = Math.Abs(Math.Sin(radians));
        int width = (int)Math.Ceiling(source.Width * cosine + source.Height * sine);
        int height = (int)Math.Ceiling(source.Width * sine + source.Height * cosine);
        var center = new Point2f(source.Width / 2.0f, source.Height / 2.0f);
        using Mat matrix = Cv2.GetRotationMatrix2D(center, angle, 1.0);
        matrix.Set(0, 2, matrix.At<double>(0, 2) + (width - source.Width) / 2.0);
        matrix.Set(1, 2, matrix.At<double>(1, 2) + (height - source.Height) / 2.0);
        var rotated = new Mat();
        Cv2.WarpAffine(
            source,
            rotated,
            matrix,
            new Size(width, height),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            Scalar.White);
        return rotated;
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private sealed record QuadrilateralCandidate(
        Rect Bounds,
        Point2f[] Corners,
        int Width,
        int Height);
}
