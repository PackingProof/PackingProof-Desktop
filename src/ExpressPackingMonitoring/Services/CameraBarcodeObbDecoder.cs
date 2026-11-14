using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Vision;
using OpenCvSharp;
using System.Diagnostics;

namespace ExpressPackingMonitoring.Services;

internal sealed record DecodedObbBarcodeCandidate(
    string Code,
    VisionObbDetection Detection);

internal readonly record struct ObbPatchDecodeAttempt(
    string? Code,
    string PatchMode,
    double PatchMs,
    double DecodeMs);

/// <summary>
/// 共享 OBB 检测器：倾斜识别或智能跟随任一开启都会提供检测结果；
/// 只有倾斜识别消费者需要时才继续执行 ZXing 解码。
/// </summary>
internal sealed class CameraBarcodeObbDecoder : IDisposable
{
    internal const double AreaWeight = 0.45;
    internal const double ConfidenceWeight = 0.35;
    internal const double CenterWeight = 0.20;
    internal const double DirectCropMaxResidualAngleDegrees = 1;
    internal const double CropPaddingRatio = 0.04;
    internal const double QuietZoneRatio = 0.08;
    private const long ObbDiagnosticsMinIntervalMs = 1500;
    private const double SlowGuideLogThresholdMs = 50;

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
    private double _lastDetectionMs;
    private int _lastDetectionCount;
    private int _lastDetectorClassCount = 1;
    private VisionObbDetectorTiming _lastDetectorTiming;

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
    internal bool SupportsBarcodeDetection => _lastDetectorClassCount > 1;
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
        _lastDetectorClassCount = 1;
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
            long started = Stopwatch.GetTimestamp();
            string? guideDecoded = _standardDecoder.DecodeGuideRegion(frame, isValid, geometry);
            double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            LogStandaloneGuideDiagnostic(
                frame,
                "guide_no_detection",
                elapsedMs,
                guideDecoded != null);
            return guideDecoded;
        }

        string? decoded = DecodeDetectedRegions(
            frame,
            LastObbDetections,
            isValid,
            geometry,
            useGuideRegionFallback: false);
        if (decoded != null)
            return decoded;

        if (HasBarcodeClassSupport())
        {
            LastSelectedObbDetection = null;
            LogStandaloneGuideDiagnostic(
                frame,
                "barcode_candidates_failed",
                0,
                decoded: false);
            return null;
        }

        // 检测框只代表候选区域，不能作为“画面里一定有码”的证据。
        // 所有 OBB 裁图都失败后必须回到原始取景区域，避免假框阻断正常 ZXing。
        LastSelectedObbDetection = null;
        long fallbackStarted = Stopwatch.GetTimestamp();
        string? fallbackDecoded = _standardDecoder.DecodeGuideRegion(frame, isValid, geometry);
        double fallbackMs =
            Stopwatch.GetElapsedTime(fallbackStarted).TotalMilliseconds;
        LogStandaloneGuideDiagnostic(
            frame,
            "guide_after_obb",
            fallbackMs,
            fallbackDecoded != null);
        return fallbackDecoded;
    }

    private void LogStandaloneGuideDiagnostic(
        Mat frame,
        string stage,
        double elapsedMs,
        bool decoded)
    {
        if (!decoded && elapsedMs < SlowGuideLogThresholdMs)
            return;

        VisionObbTimingDiagnosticLog.Append(
        [
            CreateGuideTimingDiagnostic(
                frame,
                stage,
                elapsedMs,
                decoded,
                elapsedMs)
        ]);
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
                long started = Stopwatch.GetTimestamp();
                IReadOnlyList<VisionObbDetection> detected = detector.Detect(frame);
                _lastDetectionMs =
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                _lastDetectorTiming = detector.LastTiming;
                _lastDetectionCount = detected.Count;
                _lastDetectorClassCount = detector.ClassCount;
                LastObbDetections = detected
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
        else
        {
            _lastDetectionMs = 0;
            _lastDetectionCount = 0;
            _lastDetectorClassCount = 1;
            _lastDetectorTiming = default;
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
        long decodeStarted = Stopwatch.GetTimestamp();
        var orientedCandidates = new List<DecodedObbBarcodeCandidate>();
        var diagnostics = new List<VisionObbCandidateDiagnostic>();
        var timingDiagnostics = new List<VisionObbTimingDiagnostic>();
        var fullFrame = new Rect(0, 0, frame.Width, frame.Height);
        int candidateIndex = 0;
        foreach (VisionObbDetection detection in SelectDecodeCandidates())
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
                timingDiagnostics.Add(CreateTimingDiagnostic(
                    frame,
                    detection,
                    candidateIndex,
                    patchMode: "threshold_blocked",
                    patchMs: 0,
                    decodeMs: 0,
                    decoded: false,
                    totalDecodeMs: Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds,
                    stage: "candidate"));
                candidateIndex++;
                continue;
            }

            bool useDirectCrop = ShouldUseDirectCrop(detection);
            ObbPatchDecodeAttempt attempt = DecodeDetectionPatch(
                frame,
                detection,
                useDirectCrop,
                isValid);
            timingDiagnostics.Add(CreateTimingDiagnostic(
                frame,
                detection,
                candidateIndex,
                attempt.PatchMode,
                attempt.PatchMs,
                attempt.DecodeMs,
                attempt.Code != null,
                Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds,
                stage: "candidate"));
            string? oriented = attempt.Code;
            if (string.IsNullOrWhiteSpace(oriented))
            {
                attempt = DecodeDetectionPatch(
                    frame,
                    detection,
                    !useDirectCrop,
                    isValid);
                timingDiagnostics.Add(CreateTimingDiagnostic(
                    frame,
                    detection,
                    candidateIndex,
                    attempt.PatchMode,
                    attempt.PatchMs,
                    attempt.DecodeMs,
                    attempt.Code != null,
                    Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds,
                    stage: "candidate"));
                oriented = attempt.Code;
            }
            if (string.IsNullOrWhiteSpace(oriented))
            {
                diagnostics.Add(DescribeCandidate(
                    frame,
                    detection,
                    decodeAllowed: true,
                    decoded: false));
                candidateIndex++;
                continue;
            }
            diagnostics.Add(DescribeCandidate(
                frame,
                detection,
                decodeAllowed: true,
                decoded: true));
            orientedCandidates.Add(new DecodedObbBarcodeCandidate(oriented, detection));
            candidateIndex++;
        }

        ObbBarcodeSelection selection = SelectCandidate(
            orientedCandidates,
            fullFrame);
        if (selection.Code != null)
        {
            LastSelectedObbDetection = selection.Detection;
            LogCandidateDiagnostics(
                diagnostics,
                timingDiagnostics);
            return selection.Code;
        }

        if (!useGuideRegionFallback)
        {
            LogCandidateDiagnostics(
                diagnostics,
                timingDiagnostics);
            return null;
        }

        long fallbackStarted = Stopwatch.GetTimestamp();
        string? decoded = _standardDecoder.DecodeGuideRegion(frame, isValid, geometry);
        timingDiagnostics.Add(CreateGuideTimingDiagnostic(
            frame,
            stage: "guide_fallback",
            Stopwatch.GetElapsedTime(fallbackStarted).TotalMilliseconds,
            decoded != null,
            Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds));
        LastSelectedObbDetection = FindDetectionForDecodedGeometry();
        LogCandidateDiagnostics(
            diagnostics,
            timingDiagnostics);
        return decoded;
    }

    private IReadOnlyList<VisionObbDetection> SelectDecodeCandidates()
    {
        if (!HasBarcodeClassSupport())
            return LastObbDetections;

        VisionObbDetection[] waybills = LastObbDetections
            .Where(detection => VisionObbClassPolicy.IsWaybill(detection))
            .ToArray();
        return LastObbDetections
            .Where(detection => VisionObbClassPolicy.IsBarcode(detection))
            .Where(barcode => waybills.Any(waybill =>
                VisionObbGeometry.Contains(waybill, barcode)))
            .ToArray();
    }

    private bool HasBarcodeClassSupport() =>
        _lastDetectorClassCount > 1
        || LastObbDetections.Any(detection =>
            VisionObbClassPolicy.IsBarcode(detection));

    private void LogCandidateDiagnostics(
        IReadOnlyList<VisionObbCandidateDiagnostic> diagnostics,
        IReadOnlyList<VisionObbTimingDiagnostic> timingDiagnostics)
    {
        if (diagnostics.Count == 0 && timingDiagnostics.Count == 0)
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
        VisionObbTimingDiagnosticLog.Append(timingDiagnostics);
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
            (_standardDecoder.QrCodeEnabledForDecoding
                ? CameraBarcodeDecodeSizePolicy.QrCodeMinFrameDiagonalRatio
                : CameraBarcodeDecodeSizePolicy.OneDimensionalMinFrameDiagonalRatio)
            * 100;
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

    private VisionObbTimingDiagnostic CreateTimingDiagnostic(
        Mat frame,
        in VisionObbDetection detection,
        int candidateIndex,
        string patchMode,
        double patchMs,
        double decodeMs,
        bool decoded,
        double totalDecodeMs,
        string stage) =>
        new(
            Stage: stage,
            FrameWidth: frame.Width,
            FrameHeight: frame.Height,
            DetectionMs: _lastDetectionMs,
            DetectionCount: _lastDetectionCount,
            DetectorTiming: _lastDetectorTiming,
            CandidateIndex: candidateIndex,
            CandidateWidth: detection.Width,
            CandidateHeight: detection.Height,
            CandidateAngleDegrees: detection.AngleDegrees,
            CandidateDiagonalPercent:
                CameraBarcodeDecodeSizePolicy.FrameDiagonalRatio(
                    Math.Max(1, (int)Math.Round(detection.Width)),
                    Math.Max(1, (int)Math.Round(detection.Height)),
                    frame.Width,
                    frame.Height) * 100,
            PatchMode: patchMode,
            PatchMs: patchMs,
            DecodeMs: decodeMs,
            ZxingOrientation: -1,
            ZxingMode: "",
            ZxingAttempts: 0,
            Decoded: decoded,
            TotalDecodeMs: totalDecodeMs);

    private VisionObbTimingDiagnostic CreateGuideTimingDiagnostic(
        Mat frame,
        string stage,
        double decodeMs,
        bool decoded,
        double totalDecodeMs) =>
        new(
            Stage: stage,
            FrameWidth: frame.Width,
            FrameHeight: frame.Height,
            DetectionMs: _lastDetectionMs,
            DetectionCount: _lastDetectionCount,
            DetectorTiming: _lastDetectorTiming,
            CandidateIndex: -1,
            CandidateWidth: frame.Width,
            CandidateHeight: frame.Height,
            CandidateAngleDegrees: 0,
            CandidateDiagonalPercent: 100,
            PatchMode: "guide_region",
            PatchMs: 0,
            DecodeMs: decodeMs,
            ZxingOrientation: -1,
            ZxingMode: "",
            ZxingAttempts: 0,
            Decoded: decoded,
            TotalDecodeMs: totalDecodeMs);

    private ObbPatchDecodeAttempt DecodeDetectionPatch(
        Mat frame,
        in VisionObbDetection detection,
        bool useDirectCrop,
        Func<string, bool>? isValid)
    {
        long patchStarted = Stopwatch.GetTimestamp();
        using Mat sourcePatch = useDirectCrop
            ? CreateAxisAlignedPatch(frame, detection)
            : CreateRotatedPatch(frame, detection);
        using Mat patch = AddQuietZone(sourcePatch, QuietZoneRatio);
        double patchMs = Stopwatch.GetElapsedTime(patchStarted).TotalMilliseconds;
        string patchMode = useDirectCrop ? "direct_crop" : "affine_rotate";
        if (patch.Empty())
            return new ObbPatchDecodeAttempt(null, patchMode, patchMs, 0);

        long decodeStarted = Stopwatch.GetTimestamp();
        if (!useDirectCrop)
        {
            return new ObbPatchDecodeAttempt(
                _standardDecoder.DecodeDetached(patch, isValid),
                patchMode,
                patchMs,
                Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds);
        }

        string? decoded = _standardDecoder.DecodeDetachedFast(patch);
        if (!string.IsNullOrWhiteSpace(decoded)
            && (isValid == null || TryValidate(isValid, decoded)))
        {
            return new ObbPatchDecodeAttempt(
                decoded,
                patchMode,
                patchMs,
                Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds);
        }
        return new ObbPatchDecodeAttempt(
            _standardDecoder.DecodeDetached(patch, isValid),
            patchMode,
            patchMs,
            Stopwatch.GetElapsedTime(decodeStarted).TotalMilliseconds);
    }

    internal static Mat AddQuietZone(Mat image, double ratio)
    {
        if (image == null || image.IsDisposed || image.Empty())
            return new Mat();
        if (ratio <= 0 || !double.IsFinite(ratio))
            return image.Clone();

        int padding = Math.Max(
            1,
            (int)Math.Ceiling(Math.Max(image.Width, image.Height) * ratio));
        var padded = new Mat();
        if (image.Width >= image.Height)
        {
            Cv2.CopyMakeBorder(
                image,
                padded,
                0,
                0,
                padding,
                padding,
                BorderTypes.Constant,
                Scalar.White);
        }
        else
        {
            Cv2.CopyMakeBorder(
                image,
                padded,
                padding,
                padding,
                0,
                0,
                BorderTypes.Constant,
                Scalar.White);
        }
        return padded;
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

    internal static Mat CreateRotatedPatch(Mat frame, in VisionObbDetection detection)
    {
        int width = Math.Max(1, (int)Math.Round(detection.Width));
        int height = Math.Max(1, (int)Math.Round(detection.Height));
        int padding = Math.Max(
            8,
            (int)Math.Round(Math.Min(width, height) * CropPaddingRatio));
        int outputWidth = Math.Clamp(width + padding * 2, 8, 4096);
        int outputHeight = Math.Clamp(height + padding * 2, 8, 4096);

        Point2f[] source = VisionObbGeometry.ToCorners(detection)
            .Take(3)
            .ToArray();
        Point2f[] destination =
        [
            new(padding, padding),
            new(padding + width, padding),
            new(padding + width, padding + height)
        ];

        using Mat transform = Cv2.GetAffineTransform(source, destination);
        var patch = new Mat();
        Cv2.WarpAffine(
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
