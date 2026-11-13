using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Vision;
using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

internal sealed record DecodedObbBarcodeCandidate(
    string Code,
    VisionObbDetection Detection);

/// <summary>
/// 共享 OBB 检测器：倾斜识别或智能跟随任一开启都会提供检测结果；
/// 只有倾斜识别消费者需要时才继续执行 ZXing 解码。
/// </summary>
internal sealed class CameraBarcodeObbDecoder : IDisposable
{
    internal const double AreaWeight = 0.45;
    internal const double ConfidenceWeight = 0.35;
    internal const double CenterWeight = 0.20;
    internal const double DirectCropMaxResidualAngleDegrees = 10;
    internal const double CropPaddingRatio = 0.04;
    private const long ObbDiagnosticsMinIntervalMs = 1500;

    private readonly CameraBarcodeFrameDecoder _standardDecoder;
    private readonly Func<(bool Enabled, string Path)> _settingsProvider;
    private readonly Func<bool> _decodeBarcodesProvider;
    private readonly IVisionObbDetectorFactory _detectorFactory;
    private IVisionObbDetector? _detector;
    private string? _loadedPath;
    private string? _unavailableModelPath;
    private bool _missingModelLogged;
    private bool _disposed;
    private long _lastObbDiagnosticsAtMs;
    private string _lastObbDiagnosticsSignature = "";

    internal CameraBarcodeObbDecoder(
        CameraBarcodeFrameDecoder standardDecoder,
        Func<(bool Enabled, string Path)>? settingsProvider = null,
        Func<bool>? decodeBarcodesProvider = null,
        IVisionObbDetectorFactory? detectorFactory = null)
    {
        _standardDecoder = standardDecoder;
        _settingsProvider = settingsProvider ?? (() => (false, ""));
        _decodeBarcodesProvider = decodeBarcodesProvider ?? (() => true);
        _detectorFactory = detectorFactory ?? VisionObbHost.DetectorFactory;
    }

    internal IReadOnlyList<VisionObbDetection> LastObbDetections { get; private set; } = [];
    internal VisionObbDetection? LastSelectedObbDetection { get; private set; }
    internal CameraBarcodeGeometry? LastGeometry => _standardDecoder.LastGeometry;
    internal bool IsDetectorAvailable => _detector != null;
    internal int PixelBufferAllocationCount => _standardDecoder.PixelBufferAllocationCount;

    internal string? DecodeGuideRegion(
        Mat frame,
        Func<string, bool>? isValid,
        CameraBarcodeGuideGeometry geometry)
    {
        LastObbDetections = [];
        LastSelectedObbDetection = null;
        if (_disposed || frame == null || frame.IsDisposed || frame.Empty())
            return null;

        (IReadOnlyList<VisionObbDetection> detections, bool detectorAvailable) =
            DetectDetections(frame);
        LastObbDetections = detections;
        if (!ReadDecodeBarcodes())
            return null;

        return DecodeDetectedRegions(
            frame,
            isValid,
            geometry,
            useGuideRegionFallback: !detectorAvailable);
    }

    internal void DetectOnly(
        Mat frame,
        double sourceOffsetX = 0,
        double sourceOffsetY = 0)
    {
        LastObbDetections = [];
        LastSelectedObbDetection = null;
        if (_disposed || frame == null || frame.IsDisposed || frame.Empty())
            return;
        LastObbDetections = DetectDetections(
            frame,
            sourceOffsetX,
            sourceOffsetY).Detections;
    }

    internal void ReleaseDetector()
    {
        if (_disposed)
            return;

        _detector?.Dispose();
        _detector = null;
        _loadedPath = null;
        _unavailableModelPath = null;
        _missingModelLogged = false;
        LastObbDetections = [];
        LastSelectedObbDetection = null;
    }

    internal string? DecodeStandardGuideRegion(
        Mat frame,
        Func<string, bool>? isValid,
        CameraBarcodeGuideGeometry geometry)
    {
        LastSelectedObbDetection = null;
        return _standardDecoder.DecodeGuideRegion(frame, isValid, geometry);
    }

    internal string? DecodeCachedDetections(
        Mat frame,
        Func<string, bool>? isValid,
        CameraBarcodeGuideGeometry geometry)
    {
        if (LastObbDetections.Count == 0)
        {
            LastSelectedObbDetection = null;
            return _standardDecoder.DecodeGuideRegion(frame, isValid, geometry);
        }

        string? decoded = DecodeDetectedRegions(
            frame,
            LastObbDetections,
            isValid,
            geometry,
            useGuideRegionFallback: false);
        if (decoded != null)
            return decoded;

        // 检测框只代表候选区域，不能作为“画面里一定有码”的证据。
        // 所有 OBB 裁图都失败后必须回到原始取景区域，避免假框阻断正常 ZXing。
        LastSelectedObbDetection = null;
        return _standardDecoder.DecodeGuideRegion(frame, isValid, geometry);
    }

    private (IReadOnlyList<VisionObbDetection> Detections, bool DetectorAvailable)
        DetectDetections(
            Mat frame,
            double sourceOffsetX = 0,
            double sourceOffsetY = 0)
    {
        (bool enabled, string path) = ReadSettings();
        IVisionObbDetector? detector = ResolveDetector(enabled, path);
        if (detector != null)
        {
            try
            {
                LastObbDetections = detector.Detect(frame)
                    .Take(VisionObbModelPolicy.MaxDetections)
                    .Select(detection => TranslateDetection(
                        detection,
                        sourceOffsetX,
                        sourceOffsetY))
                    .ToArray();
            }
            catch (Exception ex)
            {
                DisableDetectorAfterRuntimeFailure(detector, path, ex);
                detector = null;
            }
        }
        return (LastObbDetections, detector != null);
    }

    internal static VisionObbDetection TranslateDetection(
        in VisionObbDetection detection,
        double sourceOffsetX,
        double sourceOffsetY) =>
        new(
            detection.CenterX + sourceOffsetX,
            detection.CenterY + sourceOffsetY,
            detection.Width,
            detection.Height,
            detection.AngleDegrees,
            detection.Score,
            detection.ClassId);

    internal string? DecodeDetectedRegions(
        Mat frame,
        IReadOnlyList<VisionObbDetection> detections,
        Func<string, bool>? isValid,
        CameraBarcodeGuideGeometry geometry = default,
        bool useGuideRegionFallback = false)
    {
        LastObbDetections = detections
            .Take(VisionObbModelPolicy.MaxDetections)
            .ToArray();
        return DecodeDetectedRegions(
            frame,
            isValid,
            geometry,
            useGuideRegionFallback);
    }

    private string? DecodeDetectedRegions(
        Mat frame,
        Func<string, bool>? isValid,
        CameraBarcodeGuideGeometry geometry,
        bool useGuideRegionFallback)
    {
        var orientedCandidates = new List<DecodedObbBarcodeCandidate>();
        var diagnostics = new List<VisionObbCandidateDiagnostic>();
        var fullFrame = new Rect(0, 0, frame.Width, frame.Height);
        foreach (VisionObbDetection detection in LastObbDetections)
        {
            int width = Math.Max(1, (int)Math.Round(detection.Width));
            int height = Math.Max(1, (int)Math.Round(detection.Height));
            bool decodeAllowed = CameraBarcodeDecodeSizePolicy.ShouldDecode(
                width,
                height,
                frame.Width,
                frame.Height,
                _standardDecoder.QrCodeEnabledForDecoding);
            if (!decodeAllowed)
            {
                diagnostics.Add(DescribeCandidate(
                    frame,
                    detection,
                    decodeAllowed: false,
                    decoded: false));
                continue;
            }

            bool useDirectCrop = ShouldUseDirectCrop(detection);
            string? oriented = DecodeDetectionPatch(
                frame,
                detection,
                useDirectCrop,
                isValid);
            if (string.IsNullOrWhiteSpace(oriented))
            {
                oriented = DecodeDetectionPatch(
                    frame,
                    detection,
                    !useDirectCrop,
                    isValid);
            }
            if (string.IsNullOrWhiteSpace(oriented))
            {
                diagnostics.Add(DescribeCandidate(
                    frame,
                    detection,
                    decodeAllowed: true,
                    decoded: false));
                continue;
            }
            diagnostics.Add(DescribeCandidate(
                frame,
                detection,
                decodeAllowed: true,
                decoded: true));
            orientedCandidates.Add(new DecodedObbBarcodeCandidate(oriented, detection));
        }
        LogCandidateDiagnostics(frame, diagnostics);

        ObbBarcodeSelection selection = SelectCandidate(
            orientedCandidates,
            fullFrame);
        if (selection.Code != null)
        {
            LastSelectedObbDetection = selection.Detection;
            return selection.Code;
        }

        if (!useGuideRegionFallback)
            return null;

        string? decoded = _standardDecoder.DecodeGuideRegion(frame, isValid, geometry);
        LastSelectedObbDetection = FindDetectionForDecodedGeometry();
        return decoded;
    }

    private void LogCandidateDiagnostics(
        Mat frame,
        IReadOnlyList<VisionObbCandidateDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
            return;

        long nowMs = Environment.TickCount64;
        string signature = string.Join(
            "|",
            diagnostics.Select(candidate =>
                $"{candidate.CenterX:F0}:{candidate.CenterY:F0}:"
                + $"{candidate.Width:F0}:{candidate.Height:F0}:"
                + $"{candidate.AngleDegrees:F0}:{candidate.Score:F2}:"
                + $"{candidate.DecodeAllowed}:{candidate.Decoded}"));
        if (string.Equals(
                signature,
                _lastObbDiagnosticsSignature,
                StringComparison.Ordinal)
            && nowMs - _lastObbDiagnosticsAtMs < ObbDiagnosticsMinIntervalMs)
        {
            return;
        }

        _lastObbDiagnosticsSignature = signature;
        _lastObbDiagnosticsAtMs = nowMs;
        VisionObbDiagnosticLog.Append(diagnostics);
    }

    private VisionObbCandidateDiagnostic DescribeCandidate(
        Mat frame,
        in VisionObbDetection detection,
        bool decodeAllowed,
        bool decoded)
    {
        int width = Math.Max(1, (int)Math.Round(detection.Width));
        int height = Math.Max(1, (int)Math.Round(detection.Height));
        double diagonal = CameraBarcodeDecodeSizePolicy.DiagonalPixels(width, height);
        double ratio = CameraBarcodeDecodeSizePolicy.FrameDiagonalRatio(
            width,
            height,
            frame.Width,
            frame.Height);
        double thresholdPercent =
            CameraBarcodeDecodeSizePolicy.MinimumObbFrameDiagonalRatio * 100;
        return new VisionObbCandidateDiagnostic(
            frame.Width,
            frame.Height,
            CameraBarcodeDecodeSizePolicy.DiagonalPixels(frame.Width, frame.Height),
            detection.CenterX,
            detection.CenterY,
            detection.Width,
            detection.Height,
            detection.AngleDegrees,
            diagonal,
            ratio * 100,
            detection.Score,
            thresholdPercent,
            decodeAllowed,
            decoded);
    }

    private string? DecodeDetectionPatch(
        Mat frame,
        in VisionObbDetection detection,
        bool useDirectCrop,
        Func<string, bool>? isValid)
    {
        using Mat patch = useDirectCrop
            ? CreateAxisAlignedPatch(frame, detection)
            : CreateUprightPatch(frame, detection);
        if (patch.Empty())
            return null;
        if (!useDirectCrop)
            return _standardDecoder.DecodeDetached(patch, isValid);

        string? decoded = _standardDecoder.DecodeDetachedFast(patch);
        if (!string.IsNullOrWhiteSpace(decoded)
            && (isValid == null || TryValidate(isValid, decoded)))
        {
            return decoded;
        }
        return _standardDecoder.DecodeDetached(patch, isValid);
    }

    internal static ObbBarcodeSelection SelectCandidate(
        IReadOnlyList<DecodedObbBarcodeCandidate> candidates,
        Rect guide)
    {
        if (candidates.Count == 0)
            return new ObbBarcodeSelection(
                Code: null,
                Score: 0,
                CandidateCount: 0,
                Detection: null);

        double maxArea = candidates.Max(candidate =>
            Math.Max(1, candidate.Detection.Width * candidate.Detection.Height));
        var scored = candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                GroupKey = JdBarcodePolicy.Waybill(candidate.Code),
                Score = CalculateScore(candidate.Detection, guide, maxArea)
            })
            .ToArray();
        var bestByOrder = scored
            .GroupBy(item => item.GroupKey)
            .Select(group => group
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Candidate.Detection.Score)
                .First())
            .ToArray();
        var selected = bestByOrder
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Candidate.Detection.Score)
            .First();
        return new ObbBarcodeSelection(
            JdBarcodePolicy.Normalize(selected.Candidate.Code),
            selected.Score,
            scored.Length,
            selected.Candidate.Detection);
    }

    private VisionObbDetection? FindDetectionForDecodedGeometry()
    {
        if (_standardDecoder.LastGeometry is not { } geometry
            || LastObbDetections.Count == 0)
        {
            return null;
        }

        double centerX = geometry.CenterX;
        double centerY = geometry.CenterY;
        return LastObbDetections
            .OrderBy(detection =>
                Math.Pow(detection.CenterX - centerX, 2)
                + Math.Pow(detection.CenterY - centerY, 2))
            .Cast<VisionObbDetection?>()
            .FirstOrDefault();
    }

    internal static double CalculateScore(
        in VisionObbDetection detection,
        Rect guide,
        double maxArea)
    {
        double area = Math.Max(1, detection.Width * detection.Height);
        double areaScore = Math.Clamp(area / Math.Max(1, maxArea), 0, 1);
        double confidenceRange = Math.Max(
            0.0001,
            1.0 - VisionObbModelPolicy.ScoreThreshold);
        double confidenceScore = Math.Clamp(
            (detection.Score - VisionObbModelPolicy.ScoreThreshold) / confidenceRange,
            0,
            1);
        double centerX = guide.X + guide.Width / 2.0;
        double centerY = guide.Y + guide.Height / 2.0;
        double radius = Math.Max(
            1,
            0.5 * Math.Sqrt(guide.Width * guide.Width + guide.Height * guide.Height));
        double distance = Math.Sqrt(
            Math.Pow(detection.CenterX - centerX, 2)
            + Math.Pow(detection.CenterY - centerY, 2));
        double centerScore = Math.Clamp(1.0 - distance / radius, 0, 1);
        return AreaWeight * areaScore
               + ConfidenceWeight * confidenceScore
               + CenterWeight * centerScore;
    }

    private IVisionObbDetector? ResolveDetector(bool enabled, string path)
    {
        if (_disposed || !enabled || string.IsNullOrWhiteSpace(path))
        {
            _detector?.Dispose();
            _detector = null;
            _loadedPath = null;
            _unavailableModelPath = null;
            _missingModelLogged = false;
            return null;
        }

        if (string.Equals(
                _unavailableModelPath,
                path,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (_detector != null
            && string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return _detector;
        }

        _detector?.Dispose();
        _detector = _detectorFactory.TryLoad(path, VisionObbHost.PreferDirectMl);
        _loadedPath = _detector == null ? null : path;
        _unavailableModelPath = _detector == null ? path : null;
        if (_detector == null && !_missingModelLogged)
        {
            _missingModelLogged = true;
            RuntimeLog.Warn("CameraVision", $"增强识别模型不可用：{path}");
        }
        return _detector;
    }

    private void DisableDetectorAfterRuntimeFailure(
        IVisionObbDetector detector,
        string path,
        Exception exception)
    {
        if (ReferenceEquals(_detector, detector))
        {
            _detector = null;
            _loadedPath = null;
        }
        detector.Dispose();
        _unavailableModelPath = path;
        if (_missingModelLogged)
            return;
        _missingModelLogged = true;
        RuntimeLog.Warn(
            "CameraVision",
            $"视觉检测失败，已停用当前模型：{exception.GetType().Name}: {exception.Message}");
    }

    private (bool Enabled, string Path) ReadSettings()
    {
        try
        {
            return _settingsProvider();
        }
        catch
        {
            return (false, "");
        }
    }

    private bool ReadDecodeBarcodes()
    {
        try
        {
            return _decodeBarcodesProvider();
        }
        catch
        {
            return false;
        }
    }

    private static bool TryValidate(Func<string, bool> validator, string code)
    {
        try
        {
            return validator(code);
        }
        catch
        {
            return false;
        }
    }

    internal static bool ShouldUseDirectCrop(in VisionObbDetection detection) =>
        double.IsFinite(detection.AngleDegrees)
        && GetRightAngleResidualDegrees(detection.AngleDegrees)
           <= DirectCropMaxResidualAngleDegrees;

    internal static double GetRightAngleResidualDegrees(double angleDegrees)
    {
        if (!double.IsFinite(angleDegrees))
            return double.PositiveInfinity;
        double normalized = Math.Abs(angleDegrees % 90.0);
        return Math.Min(normalized, 90.0 - normalized);
    }

    internal static Mat CreateAxisAlignedPatch(
        Mat frame,
        in VisionObbDetection detection)
    {
        if (frame == null || frame.IsDisposed || frame.Empty())
            return new Mat();

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
            return new Mat();
        }

        int padding = Math.Max(
            8,
            (int)Math.Round(
                Math.Min(detection.Width, detection.Height) * CropPaddingRatio));
        int left = Math.Clamp(
            (int)Math.Floor(minX - padding),
            0,
            Math.Max(0, frame.Width - 1));
        int top = Math.Clamp(
            (int)Math.Floor(minY - padding),
            0,
            Math.Max(0, frame.Height - 1));
        int right = Math.Clamp(
            (int)Math.Ceiling(maxX + padding),
            left + 1,
            frame.Width);
        int bottom = Math.Clamp(
            (int)Math.Ceiling(maxY + padding),
            top + 1,
            frame.Height);
        return new Mat(frame, new Rect(left, top, right - left, bottom - top));
    }

    internal static Mat CreateUprightPatch(Mat frame, in VisionObbDetection detection)
    {
        int width = Math.Max(1, (int)Math.Round(detection.Width));
        int height = Math.Max(1, (int)Math.Round(detection.Height));
        int padding = Math.Max(
            8,
            (int)Math.Round(Math.Min(width, height) * CropPaddingRatio));
        int outputWidth = Math.Clamp(width + padding * 2, 8, 4096);
        int outputHeight = Math.Clamp(height + padding * 2, 8, 4096);

        Point2f[] source = VisionObbGeometry.ToCorners(detection);
        Point2f[] destination =
        [
            new(padding, padding),
            new(padding + width, padding),
            new(padding + width, padding + height),
            new(padding, padding + height)
        ];

        using Mat transform = Cv2.GetPerspectiveTransform(source, destination);
        var patch = new Mat();
        Cv2.WarpPerspective(
            frame,
            patch,
            transform,
            new Size(outputWidth, outputHeight),
            InterpolationFlags.Linear,
            BorderTypes.Constant,
            Scalar.White);
        return patch;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _detector?.Dispose();
        _detector = null;
        _standardDecoder.Dispose();
    }
}

internal readonly record struct ObbBarcodeSelection(
    string? Code,
    double Score,
    int CandidateCount,
    VisionObbDetection? Detection);
