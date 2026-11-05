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
    bool Recommended)
{
    internal double AddedP95Ms => Math.Max(0, EnhancedP95Ms - BaselineP95Ms);
    internal double AddedCpuMsPerFrame =>
        Math.Max(0, EnhancedCpuMsPerFrame - BaselineCpuMsPerFrame);
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
    internal const int WarmupIterations = 3;
    internal const int MeasuredIterations = 20;
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

    internal static VisionObbBenchmarkResult Run(
        string? modelPath,
        bool preferDirectMl = true,
        int frameWidth = ReferenceFrameWidth,
        int frameHeight = ReferenceFrameHeight)
    {
        frameWidth = Math.Clamp(frameWidth, 320, 7680);
        frameHeight = Math.Clamp(frameHeight, 240, 4320);
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
                Recommended: false);
        }

        (Mat blankFrame, VisionObbDetection[] blankDetections) =
            CreateBenchmarkFrame(frameWidth, frameHeight, barcodeCount: 0);
        (Mat singleFrame, VisionObbDetection[] singleDetections) =
            CreateBenchmarkFrame(frameWidth, frameHeight, barcodeCount: 1);
        (Mat doubleFrame, VisionObbDetection[] doubleDetections) =
            CreateBenchmarkFrame(frameWidth, frameHeight, barcodeCount: 2);
        (Mat sixFrame, VisionObbDetection[] sixDetections) =
            CreateBenchmarkFrame(frameWidth, frameHeight, barcodeCount: 6);
        (Mat qrFrame, VisionObbDetection[] qrDetections) =
            CreateBenchmarkFrame(frameWidth, frameHeight, barcodeCount: 1, qrCode: true);
        (Mat tiltedFrame, VisionObbDetection[] tiltedDetections) =
            CreateTiltedBenchmarkFrame(frameWidth, frameHeight, angleDegrees: 25);
        (Mat falsePositive1Frame, VisionObbDetection[] falsePositive1Detections) =
            CreateFalsePositiveBenchmarkFrame(frameWidth, frameHeight, candidateCount: 1);
        (Mat falsePositive3Frame, VisionObbDetection[] falsePositive3Detections) =
            CreateFalsePositiveBenchmarkFrame(frameWidth, frameHeight, candidateCount: 3);
        (Mat falsePositive6Frame, VisionObbDetection[] falsePositive6Detections) =
            CreateFalsePositiveBenchmarkFrame(frameWidth, frameHeight, candidateCount: 6);
        (Mat bareJdFrame, VisionObbDetection[] bareJdDetections) =
            CreateBenchmarkFrame(frameWidth, frameHeight, barcodeCount: 1, bareWaybill: true);
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
        using var obbDecoder = new CameraBarcodeObbDecoder(decoder);
        using var qrObbDecoder = new CameraBarcodeObbDecoder(qrDecoder);

        LatencyMeasurement baseline = Measure(
            () => decoder.DecodeGuideRegion(singleFrame, _ => true));
        LatencyMeasurement[] enhancedMeasurements =
        [
            MeasureProduction(
                detector,
                obbDecoder,
                blankFrame,
                blankDetections),
            MeasureProduction(
                detector,
                obbDecoder,
                singleFrame,
                singleDetections),
            MeasureProduction(
                detector,
                obbDecoder,
                doubleFrame,
                doubleDetections),
            MeasureProduction(
                detector,
                obbDecoder,
                sixFrame,
                sixDetections),
            MeasureProduction(
                detector,
                qrObbDecoder,
                qrFrame,
                qrDetections),
            MeasureProduction(
                detector,
                obbDecoder,
                tiltedFrame,
                tiltedDetections),
            MeasureProduction(
                detector,
                obbDecoder,
                falsePositive1Frame,
                falsePositive1Detections),
            MeasureProduction(
                detector,
                obbDecoder,
                falsePositive3Frame,
                falsePositive3Detections),
            MeasureProduction(
                detector,
                obbDecoder,
                falsePositive6Frame,
                falsePositive6Detections),
            MeasureProduction(
                detector,
                obbDecoder,
                bareJdFrame,
                bareJdDetections)
        ];
        LatencyMeasurement enhanced = enhancedMeasurements
            .OrderByDescending(measurement => measurement.P95Ms)
            .First();

        return new VisionObbBenchmarkResult(
            ModelAvailable: true,
            Provider: detector.ExecutionProvider,
            BaselineP95Ms: baseline.P95Ms,
            EnhancedP95Ms: enhanced.P95Ms,
            BaselineCpuMsPerFrame: baseline.CpuMsPerFrame,
            EnhancedCpuMsPerFrame: enhanced.CpuMsPerFrame,
            Recommended: ShouldRecommend(
                detector.ExecutionProvider,
                enhanced.P95Ms,
                enhanced.CpuMsPerFrame));
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

    private static LatencyMeasurement Measure(Action action)
    {
        for (int index = 0; index < WarmupIterations; index++)
            action();

        using Process process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        var samples = new double[MeasuredIterations];
        for (int index = 0; index < samples.Length; index++)
        {
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
        CameraBarcodeObbDecoder obbDecoder,
        Mat frame,
        IReadOnlyList<VisionObbDetection> detections) =>
        Measure(() =>
        {
            detector.Detect(frame);
            obbDecoder.DecodeDetectedRegions(
                frame,
                detections,
                _ => true,
                useGuideRegionFallback: false);
        });

    private static (Mat Frame, VisionObbDetection[] Detections) CreateTiltedBenchmarkFrame(
        int frameWidth,
        int frameHeight,
        double angleDegrees)
    {
        double scale = Math.Min(
            frameWidth / (double)ReferenceFrameWidth,
            frameHeight / (double)ReferenceFrameHeight);
        using Mat code = CreateCodeMat(
            "YT000000000025",
            BarcodeFormat.CODE_128,
            Math.Max(120, (int)Math.Round(480 * scale)),
            Math.Max(48, (int)Math.Round(160 * scale)));
        var baseFrame = new Mat(
            new Size(frameWidth, frameHeight),
            MatType.CV_8UC3,
            Scalar.White);
        try
        {
            int x = (baseFrame.Width - code.Width) / 2;
            int y = (baseFrame.Height - code.Height) / 2;
            using (Mat target = baseFrame.SubMat(
                       new Rect(x, y, code.Width, code.Height)))
            {
                code.CopyTo(target);
            }

            using Mat matrix = Cv2.GetRotationMatrix2D(
                new Point2f(baseFrame.Width / 2f, baseFrame.Height / 2f),
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
                    new VisionObbDetection(
                        baseFrame.Width / 2.0,
                        baseFrame.Height / 2.0,
                        code.Width,
                        code.Height,
                        -angleDegrees,
                        0.95f)
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
        bool bareWaybill = false)
    {
        var frame = new Mat(
            new Size(frameWidth, frameHeight),
            MatType.CV_8UC3,
            Scalar.White);
        if (barcodeCount <= 0)
            return (frame, []);

        (int X, int Y)[] positions =
        [
            (120, 180),
            (720, 180),
            (1320, 180),
            (120, 700),
            (720, 700),
            (1320, 700)
        ];
        double scaleX = frameWidth / (double)ReferenceFrameWidth;
        double scaleY = frameHeight / (double)ReferenceFrameHeight;
        double codeScale = Math.Min(scaleX, scaleY);
        var detections = new VisionObbDetection[barcodeCount];
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
                qrCode
                    ? Math.Max(96, (int)Math.Round(220 * codeScale))
                    : Math.Max(120, (int)Math.Round(480 * codeScale)),
                qrCode
                    ? Math.Max(96, (int)Math.Round(220 * codeScale))
                    : Math.Max(48, (int)Math.Round(160 * codeScale)));
            (int referenceX, int referenceY) = qrCode
                ? (820, 390)
                : positions[index];
            int x = (int)Math.Round(referenceX * scaleX);
            int y = (int)Math.Round(referenceY * scaleY);
            x = Math.Clamp(x, 0, Math.Max(0, frameWidth - code.Width));
            y = Math.Clamp(y, 0, Math.Max(0, frameHeight - code.Height));
            int padding = Math.Max(6, (int)Math.Round(28 * codeScale));
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
            detections[index] = new VisionObbDetection(
                x + code.Width / 2.0,
                y + code.Height / 2.0,
                code.Width,
                code.Height,
                AngleDegrees: index % 2 == 0 ? 0 : 8,
                Score: 0.95f);
        }
        return (frame, detections);
    }

    private static (Mat Frame, VisionObbDetection[] Detections)
        CreateFalsePositiveBenchmarkFrame(
            int frameWidth,
            int frameHeight,
            int candidateCount)
    {
        var frame = new Mat(
            new Size(frameWidth, frameHeight),
            MatType.CV_8UC3,
            Scalar.White);
        (int X, int Y)[] positions =
        [
            (120, 180),
            (720, 180),
            (1320, 180),
            (120, 700),
            (720, 700),
            (1320, 700)
        ];
        double scaleX = frameWidth / (double)ReferenceFrameWidth;
        double scaleY = frameHeight / (double)ReferenceFrameHeight;
        double candidateScale = Math.Min(scaleX, scaleY);
        int candidateWidth = Math.Max(120, (int)Math.Round(480 * candidateScale));
        int candidateHeight = Math.Max(40, (int)Math.Round(160 * candidateScale));
        var detections = new VisionObbDetection[candidateCount];
        for (int index = 0; index < candidateCount; index++)
        {
            (int referenceX, int referenceY) = positions[index];
            int x = Math.Clamp(
                (int)Math.Round(referenceX * scaleX),
                0,
                Math.Max(0, frameWidth - candidateWidth));
            int y = Math.Clamp(
                (int)Math.Round(referenceY * scaleY),
                0,
                Math.Max(0, frameHeight - candidateHeight));
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
                thickness: Math.Max(1, (int)Math.Round(5 * candidateScale)),
                lineType: LineTypes.Link8);
            detections[index] = new VisionObbDetection(
                bounds.X + bounds.Width / 2.0,
                bounds.Y + bounds.Height / 2.0,
                bounds.Width,
                bounds.Height,
                AngleDegrees: 0,
                Score: 0.95f);
        }
        return (frame, detections);
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
