using ExpressPackingMonitoring.Vision;
using ExpressPackingMonitoring.Logging;
using OpenCvSharp;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using ZXing;
using ZXing.Common;

namespace ExpressPackingMonitoring.Services;

internal sealed record VisionObbBenchmarkResult(
    bool ModelAvailable,
    string Provider,
    double BaselineP95Ms,
    double EnhancedP95Ms,
    double BaselineCpuMsPerFrame,
    double EnhancedCpuMsPerFrame,
    bool Recommended,
    bool AllScenariosCompleted = true)
{
    internal double AddedP95Ms =>
        BaselineP95Ms > 0
            ? Math.Max(0, EnhancedP95Ms - BaselineP95Ms)
            : 0;
    internal double AddedCpuMsPerFrame =>
        BaselineCpuMsPerFrame > 0
            ? Math.Max(0, EnhancedCpuMsPerFrame - BaselineCpuMsPerFrame)
            : 0;
}

internal static class VisionObbPerformanceBenchmark
{
    private static int _modelReadWarningLogged;

    internal const double TargetDecodesPerSecond = 4;
    internal const double HeadroomRatio = 0.30;
    internal const double TargetIntervalMs = 1000 / TargetDecodesPerSecond;
    internal const double RecommendedP95Ms = TargetIntervalMs * (1 - HeadroomRatio);
    internal const double RecommendedCpuMsPerFrame = RecommendedP95Ms;
    internal const double CpuProviderSelectionMargin = 0.10;
    internal const double DirectMlCpuComparisonP95Ratio = 0.75;
    internal const double ClearPerformanceRatio = 0.75;
    internal const int ScreeningWarmupIterations = 1;
    internal const int ScreeningMeasuredIterations = 7;
    internal const int ConfirmationWarmupIterations = 2;
    internal const int ConfirmationMeasuredIterations = 21;
    internal const int ReferenceFrameWidth = 1920;
    internal const int ReferenceFrameHeight = 1080;

    internal static string GetModelIdentity(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            return "";
        try
        {
            using FileStream stream = File.OpenRead(modelPath);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            if (Interlocked.Exchange(ref _modelReadWarningLogged, 1) == 0)
            {
                RuntimeLog.Warn(
                    "CameraVision",
                    $"无法读取视觉模型指纹，本次跳过自动性能检测：{ex.GetType().Name}: {ex.Message}");
            }
            return "";
        }
    }

    internal static bool ShouldRunForModel(
        string? currentModelKey,
        string? completedModelKey) =>
        !string.IsNullOrWhiteSpace(currentModelKey)
        && !string.Equals(
            currentModelKey,
            completedModelKey,
            StringComparison.Ordinal);

    internal static VisionObbBenchmarkResult SelectFasterProvider(
        VisionObbBenchmarkResult directMl,
        VisionObbBenchmarkResult cpu)
    {
        if (!directMl.ModelAvailable)
            return cpu.ModelAvailable ? cpu : directMl;
        if (!cpu.ModelAvailable
            || !string.Equals(
                directMl.Provider,
                "DirectML",
                StringComparison.OrdinalIgnoreCase))
        {
            return directMl;
        }
        if (cpu.Recommended != directMl.Recommended)
            return cpu.Recommended ? cpu : directMl;
        return cpu.EnhancedP95Ms > 0
            && cpu.EnhancedP95Ms
                < directMl.EnhancedP95Ms * (1 - CpuProviderSelectionMargin)
            ? cpu
            : directMl;
    }

    /// <summary>
    /// DirectML 不是天然一定快于 CPU，尤其在旧核显和小模型上。只要 DirectML
    /// 已明显低于门槛，就没有必要再跑一轮完整的 CPU 基准；失败或贴近门槛时仍需对照。
    /// </summary>
    internal static bool ShouldCompareCpuProvider(
        VisionObbBenchmarkResult directMl)
    {
        if (!directMl.ModelAvailable
            || !string.Equals(
                directMl.Provider,
                "DirectML",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!directMl.Recommended || directMl.EnhancedP95Ms <= 0)
            return true;

        return directMl.EnhancedP95Ms
            >= RecommendedP95Ms * DirectMlCpuComparisonP95Ratio;
    }

    internal static VisionObbBenchmarkResult Run(
        string? modelPath,
        bool preferDirectMl = true,
        int frameWidth = ReferenceFrameWidth,
        int frameHeight = ReferenceFrameHeight,
        CancellationToken cancellationToken = default,
        CameraBarcodeGuideGeometry? guideGeometry = null)
    {
        frameWidth = Math.Clamp(frameWidth, 320, 7680);
        frameHeight = Math.Clamp(frameHeight, 240, 4320);
        CameraBarcodeGuideGeometry resolvedGuide =
            guideGeometry ?? CameraBarcodeGuideGeometry.Default;
        (Mat blankFrame, VisionObbDetection[] blankDetections) =
            CreateBenchmarkFrame(
                frameWidth,
                frameHeight,
                barcodeCount: 0,
                guideGeometry: resolvedGuide);
        (Mat singleFrame, VisionObbDetection[] singleDetections) =
            CreateBenchmarkFrame(
                frameWidth,
                frameHeight,
                barcodeCount: 1,
                guideGeometry: resolvedGuide);
        (Mat doubleFrame, VisionObbDetection[] doubleDetections) =
            CreateBenchmarkFrame(
                frameWidth,
                frameHeight,
                barcodeCount: 2,
                guideGeometry: resolvedGuide);
        (Mat sixFrame, VisionObbDetection[] sixDetections) =
            CreateBenchmarkFrame(
                frameWidth,
                frameHeight,
                barcodeCount: 6,
                guideGeometry: resolvedGuide);
        (Mat qrFrame, VisionObbDetection[] qrDetections) =
            CreateBenchmarkFrame(
                frameWidth,
                frameHeight,
                barcodeCount: 1,
                qrCode: true,
                guideGeometry: resolvedGuide);
        (Mat tiltedFrame, VisionObbDetection[] tiltedDetections) =
            CreateTiltedBenchmarkFrame(
                frameWidth,
                frameHeight,
                angleDegrees: 25,
                guideGeometry: resolvedGuide);
        (Mat falsePositive1Frame, VisionObbDetection[] falsePositive1Detections) =
            CreateFalsePositiveBenchmarkFrame(
                frameWidth,
                frameHeight,
                candidateCount: 1,
                guideGeometry: resolvedGuide);
        (Mat falsePositive3Frame, VisionObbDetection[] falsePositive3Detections) =
            CreateFalsePositiveBenchmarkFrame(
                frameWidth,
                frameHeight,
                candidateCount: 3,
                guideGeometry: resolvedGuide);
        (Mat falsePositive6Frame, VisionObbDetection[] falsePositive6Detections) =
            CreateFalsePositiveBenchmarkFrame(
                frameWidth,
                frameHeight,
                candidateCount: 6,
                guideGeometry: resolvedGuide);
        (Mat bareJdFrame, VisionObbDetection[] bareJdDetections) =
            CreateBenchmarkFrame(
                frameWidth,
                frameHeight,
                barcodeCount: 1,
                bareWaybill: true,
                guideGeometry: resolvedGuide);
        using var blankFrameScope = blankFrame;
        using var singleFrameScope = singleFrame;
        using var doubleFrameScope = doubleFrame;
        using var sixFrameScope = sixFrame;
        using var qrFrameScope = qrFrame;
        using var tiltedFrameScope = tiltedFrame;
        using var falsePositive1FrameScope = falsePositive1Frame;
        using var falsePositive3FrameScope = falsePositive3Frame;
        using var falsePositive6FrameScope = falsePositive6Frame;
        using var bareJdFrameScope = bareJdFrame;
        using var decoder = new CameraBarcodeFrameDecoder();
        using var qrDecoder = new CameraBarcodeFrameDecoder(() => true);
        using var noObbDecoder = new CameraBarcodeFrameDecoder();
        using var noObbQrDecoder = new CameraBarcodeFrameDecoder(() => true);

        using IVisionObbDetector? detector = VisionObbHost.DetectorFactory.TryLoad(
            modelPath,
            preferDirectMl);
        if (detector == null)
        {
            return new VisionObbBenchmarkResult(
                ModelAvailable: false,
                Provider: "",
                BaselineP95Ms: 0,
                EnhancedP95Ms: 0,
                BaselineCpuMsPerFrame: 0,
                EnhancedCpuMsPerFrame: 0,
                Recommended: false,
                AllScenariosCompleted: false);
        }

        using var obbDecoder = new CameraBarcodeObbDecoder(decoder);
        using var qrObbDecoder = new CameraBarcodeObbDecoder(qrDecoder);

        (Mat Frame, VisionObbDetection[] Detections, CameraBarcodeObbDecoder Decoder)[]
            enhancedScenarios =
        [
            (blankFrame, blankDetections, obbDecoder),
            (singleFrame, singleDetections, obbDecoder),
            (doubleFrame, doubleDetections, obbDecoder),
            (sixFrame, sixDetections, obbDecoder),
            (qrFrame, qrDetections, qrObbDecoder),
            (tiltedFrame, tiltedDetections, obbDecoder),
            (falsePositive1Frame, falsePositive1Detections, obbDecoder),
            (falsePositive3Frame, falsePositive3Detections, obbDecoder),
            (falsePositive6Frame, falsePositive6Detections, obbDecoder),
            (bareJdFrame, bareJdDetections, obbDecoder)
        ];

        var enhancedMeasurements = new List<LatencyMeasurement>(
            enhancedScenarios.Length);
        bool allScenariosCompleted = true;
        foreach ((
                     Mat frame,
                     VisionObbDetection[] detections,
                     CameraBarcodeObbDecoder scenarioDecoder)
                 in enhancedScenarios)
        {
            LatencyMeasurement measurement = MeasureProduction(
                detector,
                frame,
                detections,
                scenarioDecoder,
                resolvedGuide,
                cancellationToken,
                confirm: false);
            if (!IsClearlyBelowRecommendation(
                    detector.ExecutionProvider,
                    measurement))
            {
                measurement = MeasureProduction(
                    detector,
                    frame,
                    detections,
                    scenarioDecoder,
                    resolvedGuide,
                    cancellationToken,
                    confirm: true);
            }
            enhancedMeasurements.Add(measurement);
            if (ShouldAbortProviderBenchmark(
                    detector.ExecutionProvider,
                    measurement))
            {
                allScenariosCompleted = false;
                break;
            }
        }

        LatencyMeasurement enhanced = enhancedMeasurements
            .OrderByDescending(measurement => measurement.P95Ms)
            .First();
        bool recommended = allScenariosCompleted
            && ShouldRecommend(
                detector.ExecutionProvider,
                enhanced.P95Ms,
                enhanced.CpuMsPerFrame);
        if (!recommended)
        {
            return new VisionObbBenchmarkResult(
                ModelAvailable: true,
                Provider: detector.ExecutionProvider,
                BaselineP95Ms: 0,
                EnhancedP95Ms: enhanced.P95Ms,
                BaselineCpuMsPerFrame: 0,
                EnhancedCpuMsPerFrame: enhanced.CpuMsPerFrame,
                Recommended: false,
                AllScenariosCompleted: allScenariosCompleted);
        }

        (Mat Frame, CameraBarcodeFrameDecoder Decoder)[] noObbScenarios =
        [
            (blankFrame, noObbDecoder),
            (singleFrame, noObbDecoder),
            (doubleFrame, noObbDecoder),
            (sixFrame, noObbDecoder),
            (qrFrame, noObbQrDecoder),
            (tiltedFrame, noObbDecoder),
            (falsePositive1Frame, noObbDecoder),
            (falsePositive3Frame, noObbDecoder),
            (falsePositive6Frame, noObbDecoder),
            (bareJdFrame, noObbDecoder)
        ];
        LatencyMeasurement? noObb = null;
        foreach ((Mat frame, CameraBarcodeFrameDecoder scenarioDecoder)
                 in noObbScenarios)
        {
            LatencyMeasurement measurement = MeasureNoObbProduction(
                scenarioDecoder,
                frame,
                resolvedGuide,
                cancellationToken);
            if (noObb == null || measurement.P95Ms > noObb.Value.P95Ms)
                noObb = measurement;
        }

        return new VisionObbBenchmarkResult(
            ModelAvailable: true,
            Provider: detector.ExecutionProvider,
            BaselineP95Ms: noObb?.P95Ms ?? 0,
            EnhancedP95Ms: enhanced.P95Ms,
            BaselineCpuMsPerFrame: noObb?.CpuMsPerFrame ?? 0,
            EnhancedCpuMsPerFrame: enhanced.CpuMsPerFrame,
            Recommended: true,
            AllScenariosCompleted: true);
    }

    private static bool ShouldAbortProviderBenchmark(
        string? provider,
        LatencyMeasurement measurement) =>
        measurement.P95Ms > RecommendedP95Ms
        || (string.Equals(
                provider,
                "CPU",
                StringComparison.OrdinalIgnoreCase)
            && measurement.CpuMsPerFrame > RecommendedCpuMsPerFrame);

    private static LatencyMeasurement MeasureNoObbProduction(
        CameraBarcodeFrameDecoder decoder,
        Mat frame,
        CameraBarcodeGuideGeometry guideGeometry,
        CancellationToken cancellationToken) =>
        Measure(() =>
        {
            decoder.DecodeGuideRegion(
                frame,
                _ => true,
                guideGeometry);
        },
        cancellationToken);

    private static bool IsClearlyBelowRecommendation(
        string? provider,
        LatencyMeasurement measurement)
    {
        if (measurement.P95Ms > RecommendedP95Ms * ClearPerformanceRatio)
            return false;

        return !string.Equals(
                   provider,
                   "CPU",
                   StringComparison.OrdinalIgnoreCase)
               || measurement.CpuMsPerFrame
                   <= RecommendedCpuMsPerFrame * ClearPerformanceRatio;
    }

    internal static bool ShouldRecommend(
        string? provider,
        double enhancedP95Ms,
        double enhancedCpuMsPerFrame)
    {
        if (enhancedP95Ms <= 0 || enhancedP95Ms > RecommendedP95Ms)
            return false;

        if (string.Equals(
                provider,
                "DirectML",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
                   provider,
                   "CPU",
                   StringComparison.OrdinalIgnoreCase)
               && enhancedCpuMsPerFrame > 0
               && enhancedCpuMsPerFrame <= RecommendedCpuMsPerFrame;
    }

    private static LatencyMeasurement Measure(
        Action action,
        CancellationToken cancellationToken,
        int warmupIterations = ScreeningWarmupIterations,
        int measuredIterations = ScreeningMeasuredIterations)
    {
        for (int index = 0; index < warmupIterations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
        }

        using Process process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        var samples = new double[measuredIterations];
        for (int index = 0; index < samples.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long started = Stopwatch.GetTimestamp();
            action();
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        TimeSpan cpuAfter = process.TotalProcessorTime;

        Array.Sort(samples);
        int p95Index = Math.Clamp(
            (int)Math.Ceiling(samples.Length * 0.95) - 1,
            0,
            samples.Length - 1);
        return new LatencyMeasurement(
            samples[p95Index],
            Math.Max(0, (cpuAfter - cpuBefore).TotalMilliseconds) / samples.Length);
    }

    private static LatencyMeasurement MeasureProduction(
        IVisionObbDetector detector,
        Mat frame,
        IReadOnlyList<VisionObbDetection> detections,
        CameraBarcodeObbDecoder obbDecoder,
        CameraBarcodeGuideGeometry guideGeometry,
        CancellationToken cancellationToken,
        bool confirm) =>
        Measure(() =>
        {
            Rect guide = CameraBarcodeGuideLayout.ToFrameRect(
                frame.Width,
                frame.Height,
                guideGeometry);
            using var roi = new Mat(frame, guide);
            detector.Detect(roi);
            obbDecoder.DecodeDetectedRegions(
                frame,
                detections,
                _ => true,
                useGuideRegionFallback: false);
        },
        cancellationToken,
        confirm ? ConfirmationWarmupIterations : ScreeningWarmupIterations,
        confirm
            ? ConfirmationMeasuredIterations
            : ScreeningMeasuredIterations);

    private static (Mat Frame, VisionObbDetection[] Detections) CreateTiltedBenchmarkFrame(
        int frameWidth,
        int frameHeight,
        double angleDegrees,
        CameraBarcodeGuideGeometry? guideGeometry = null)
    {
        Rect guide = CameraBarcodeGuideLayout.ToFrameRect(
            frameWidth,
            frameHeight,
            guideGeometry ?? CameraBarcodeGuideGeometry.Default);
        using Mat code = CreateCodeMat(
            "YT000000000025",
            BarcodeFormat.CODE_128,
            Math.Max(100, (int)Math.Round(guide.Width * 0.38)),
            Math.Max(48, (int)Math.Round(guide.Height * 0.14)));
        var baseFrame = new Mat(
            new Size(frameWidth, frameHeight),
            MatType.CV_8UC3,
            Scalar.White);
        try
        {
            int x = guide.X + (guide.Width - code.Width) / 2;
            int y = guide.Y + (guide.Height - code.Height) / 2;
            using (Mat target = baseFrame.SubMat(
                       new Rect(x, y, code.Width, code.Height)))
            {
                code.CopyTo(target);
            }

            using Mat matrix = Cv2.GetRotationMatrix2D(
                new Point2f(
                    guide.X + guide.Width / 2f,
                    guide.Y + guide.Height / 2f),
                angleDegrees,
                1);
            var frame = new Mat();
            Cv2.WarpAffine(
                baseFrame,
                frame,
                matrix,
                baseFrame.Size(),
                InterpolationFlags.Linear,
                BorderTypes.Constant,
                Scalar.White);
            return (
                frame,
                [
                    CreateWaybillDetection(new VisionObbDetection(
                        guide.X + guide.Width / 2.0,
                        guide.Y + guide.Height / 2.0,
                        code.Width,
                        code.Height,
                        -angleDegrees,
                        0.95f,
                        VisionObbClassIds.Barcode)),
                    new VisionObbDetection(
                        guide.X + guide.Width / 2.0,
                        guide.Y + guide.Height / 2.0,
                        code.Width,
                        code.Height,
                        -angleDegrees,
                        0.95f,
                        VisionObbClassIds.Barcode)
                ]);
        }
        finally
        {
            baseFrame.Dispose();
        }
    }

    internal static (Mat Frame, VisionObbDetection[] Detections) CreateBenchmarkFrame(
        int frameWidth,
        int frameHeight,
        int barcodeCount,
        bool qrCode = false,
        bool bareWaybill = false,
        CameraBarcodeGuideGeometry? guideGeometry = null)
    {
        var frame = new Mat(
            new Size(frameWidth, frameHeight),
            MatType.CV_8UC3,
            Scalar.White);
        if (barcodeCount <= 0)
            return (frame, []);

        Rect guide = CameraBarcodeGuideLayout.ToFrameRect(
            frameWidth,
            frameHeight,
            guideGeometry ?? CameraBarcodeGuideGeometry.Default);
        int columns = Math.Min(3, barcodeCount);
        int rows = (barcodeCount + columns - 1) / columns;
        int cellWidth = Math.Max(1, guide.Width / columns);
        int cellHeight = Math.Max(1, guide.Height / rows);
        int codeWidth = qrCode
            ? Math.Max(72, (int)Math.Round(
                Math.Min(cellWidth, cellHeight) * (barcodeCount == 1 ? 0.52 : 0.72)))
            : Math.Max(
                96,
                (int)Math.Round(
                    Math.Min(cellWidth * 0.82, guide.Width * 0.36)));
        int codeHeight = qrCode
            ? codeWidth
            : Math.Max(40, (int)Math.Round(cellHeight * 0.26));
        var detections = new VisionObbDetection[barcodeCount * 2];
        for (int index = 0; index < barcodeCount; index++)
        {
            string value = qrCode
                ? "YT000000000001"
                : bareWaybill
                    ? "JD0123456789"
                    : $"YT{index + 1:D12}";
            using Mat code = CreateCodeMat(
                value,
                qrCode ? BarcodeFormat.QR_CODE : BarcodeFormat.CODE_128,
                codeWidth,
                codeHeight);
            (int x, int y) = ResolveGuideCellPosition(
                guide,
                index,
                columns,
                rows,
                code.Width,
                code.Height);
            x = Math.Clamp(x, 0, Math.Max(0, frameWidth - code.Width));
            y = Math.Clamp(y, 0, Math.Max(0, frameHeight - code.Height));
            int padding = Math.Max(
                6,
                (int)Math.Round(Math.Min(cellWidth, cellHeight) * 0.05));
            Cv2.Rectangle(
                frame,
                new Rect(
                    Math.Max(0, x - padding),
                    Math.Max(0, y - padding),
                    Math.Min(frameWidth - Math.Max(0, x - padding), code.Width + padding * 2),
                    Math.Min(frameHeight - Math.Max(0, y - padding), code.Height + padding * 2)),
                new Scalar(238, 238, 238),
                thickness: -1);
            using Mat target = frame.SubMat(
                new Rect(x, y, code.Width, code.Height));
            code.CopyTo(target);
            var barcode = new VisionObbDetection(
                x + code.Width / 2.0,
                y + code.Height / 2.0,
                code.Width,
                code.Height,
                AngleDegrees: index % 2 == 0 ? 0 : 8,
                Score: 0.95f,
                ClassId: VisionObbClassIds.Barcode);
            detections[index * 2] = CreateWaybillDetection(barcode);
            detections[index * 2 + 1] = barcode;
        }
        return (frame, detections);
    }

    private static (Mat Frame, VisionObbDetection[] Detections)
        CreateFalsePositiveBenchmarkFrame(
            int frameWidth,
            int frameHeight,
            int candidateCount,
            CameraBarcodeGuideGeometry? guideGeometry = null)
    {
        var frame = new Mat(
            new Size(frameWidth, frameHeight),
            MatType.CV_8UC3,
            Scalar.White);
        Rect guide = CameraBarcodeGuideLayout.ToFrameRect(
            frameWidth,
            frameHeight,
            guideGeometry ?? CameraBarcodeGuideGeometry.Default);
        int columns = Math.Min(3, candidateCount);
        int rows = (candidateCount + columns - 1) / columns;
        int cellWidth = Math.Max(1, guide.Width / columns);
        int cellHeight = Math.Max(1, guide.Height / rows);
        int candidateWidth = Math.Max(
            96,
            (int)Math.Round(Math.Min(cellWidth * 0.82, guide.Width * 0.36)));
        int candidateHeight = Math.Max(
            40,
            (int)Math.Round(cellHeight * 0.26));
        var detections = new VisionObbDetection[candidateCount];
        for (int index = 0; index < candidateCount; index++)
        {
            (int x, int y) = ResolveGuideCellPosition(
                guide,
                index,
                columns,
                rows,
                candidateWidth,
                candidateHeight);
            x = Math.Clamp(x, 0, Math.Max(0, frameWidth - candidateWidth));
            y = Math.Clamp(y, 0, Math.Max(0, frameHeight - candidateHeight));
            var bounds = new Rect(x, y, candidateWidth, candidateHeight);
            Cv2.Rectangle(
                frame,
                bounds,
                new Scalar(230, 230, 230),
                thickness: -1);
            Cv2.Line(
                frame,
                new Point(bounds.Left + Math.Max(4, bounds.Width / 20), bounds.Top + bounds.Height * 42 / 160),
                new Point(bounds.Right - Math.Max(4, bounds.Width / 20), bounds.Bottom - bounds.Height * 36 / 160),
                new Scalar(190, 190, 190),
                thickness: Math.Max(
                    1,
                    (int)Math.Round(candidateHeight / 32.0)),
                lineType: LineTypes.Link8);
            detections[index] = new VisionObbDetection(
                bounds.X + bounds.Width / 2.0,
                bounds.Y + bounds.Height / 2.0,
                bounds.Width,
                bounds.Height,
                AngleDegrees: 0,
                Score: 0.95f,
                ClassId: VisionObbClassIds.Waybill);
        }
        return (frame, detections);
    }

    private static VisionObbDetection CreateWaybillDetection(
        in VisionObbDetection barcode) =>
        new(
            barcode.CenterX,
            barcode.CenterY,
            barcode.Width * 1.65,
            barcode.Height * 2.10,
            barcode.AngleDegrees,
            Score: 0.92f,
            ClassId: VisionObbClassIds.Waybill);

    private static (int X, int Y) ResolveGuideCellPosition(
        Rect guide,
        int index,
        int columns,
        int rows,
        int width,
        int height)
    {
        int column = index % columns;
        int row = index / columns;
        int cellWidth = Math.Max(1, guide.Width / columns);
        int cellHeight = Math.Max(1, guide.Height / rows);
        int x = guide.X
            + column * cellWidth
            + Math.Max(0, (cellWidth - width) / 2);
        int y = guide.Y
            + row * cellHeight
            + Math.Max(0, (cellHeight - height) / 2);
        return (x, y);
    }

    private static Mat CreateCodeMat(
        string value,
        BarcodeFormat format,
        int width = 0,
        int height = 0)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = format,
            Options = new EncodingOptions
            {
                Width = width > 0 ? width : format == BarcodeFormat.QR_CODE ? 220 : 480,
                Height = height > 0 ? height : format == BarcodeFormat.QR_CODE ? 220 : 160,
                Margin = format == BarcodeFormat.QR_CODE ? 3 : 12,
                PureBarcode = format != BarcodeFormat.QR_CODE
            }
        };
        var pixels = writer.Write(value);
        using Mat bgra = Mat.FromPixelData(
            pixels.Height,
            pixels.Width,
            MatType.CV_8UC4,
            pixels.Pixels).Clone();
        var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr;
    }

    private readonly record struct LatencyMeasurement(
        double P95Ms,
        double CpuMsPerFrame);
}
