using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Vision;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System.Globalization;
using System.IO;

namespace ExpressPackingMonitoring.Vision.Obb;

internal enum VisionObbOutputLayout
{
    Ultralytics,
    Yolox,
    Rtmdet
}

/// <summary>
/// 通用 YOLO-OBB 检测器，可用于面单、商品或其他单类/多类目标。
/// 模型输入固定为 640×640，通过 ONNX 元数据选择 Ultralytics 或 YOLOX 输出契约。
/// </summary>
internal sealed class VisionObbDetector : IVisionObbDetector
{
    internal const int InputSize = VisionObbModelPolicy.InputSize;
    internal const float ScoreThreshold = VisionObbModelPolicy.ScoreThreshold;
    internal const float NmsThreshold = VisionObbModelPolicy.NmsThreshold;
    internal const int MaxDetections = VisionObbModelPolicy.MaxDetections;
    private const byte PadValue = 114;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly bool _channelsFirst;
    private readonly VisionObbOutputLayout _outputLayout;
    private readonly float _scoreThreshold;
    private readonly float _nmsThreshold;
    private readonly string _executionProvider;
    private readonly Mat _canvas = new(
        InputSize,
        InputSize,
        MatType.CV_8UC3,
        new Scalar(PadValue, PadValue, PadValue));
    private readonly Mat _resized = new();
    private readonly Mat _convertedBgr = new();
    private readonly DenseTensor<float> _input =
        new(new[] { 1, 3, InputSize, InputSize });
    private readonly object _detectLock = new();
    private int _canvasSourceWidth;
    private int _canvasSourceHeight;
    private bool _disposed;

    private VisionObbDetector(
        InferenceSession session,
        string inputName,
        string outputName,
        bool channelsFirst,
        VisionObbOutputLayout outputLayout,
        float scoreThreshold,
        float nmsThreshold,
        string executionProvider)
    {
        _session = session;
        _inputName = inputName;
        _outputName = outputName;
        _channelsFirst = channelsFirst;
        _outputLayout = outputLayout;
        _scoreThreshold = scoreThreshold;
        _nmsThreshold = nmsThreshold;
        _executionProvider = executionProvider;
    }

    public string ExecutionProvider => _executionProvider;
    internal int LastRawCandidateCount { get; private set; }
    internal int LastFilteredDetectionCount { get; private set; }

    internal static VisionObbDetector? TryLoad(
        string? modelPath,
        bool preferDirectMl = true)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            return null;

        InferenceSession? session = null;
        try
        {
            (session, string executionProvider) = CreateSession(modelPath, preferDirectMl);
            string inputName = session.InputMetadata.Keys.First();
            string outputName = session.OutputMetadata.Keys.First();
            int[]? inputDimensions = session.InputMetadata[inputName].Dimensions;
            int[]? outputDimensions = session.OutputMetadata[outputName].Dimensions;
            session.ModelMetadata.CustomMetadataMap.TryGetValue(
                "packingproof.output_layout",
                out string? configuredOutputLayout);
            float scoreThreshold = ReadThreshold(
                session.ModelMetadata.CustomMetadataMap,
                "packingproof.score_threshold",
                ScoreThreshold);
            float nmsThreshold = ReadThreshold(
                session.ModelMetadata.CustomMetadataMap,
                "packingproof.nms_threshold",
                NmsThreshold);
            if (!TryResolveContract(
                    inputDimensions,
                    outputDimensions,
                    configuredOutputLayout,
                    out bool channelsFirst,
                    out VisionObbOutputLayout outputLayout,
                    out string contractError))
            {
                throw new InvalidDataException(contractError);
            }
            RuntimeLog.Info(
                "CameraVision",
                $"增强识别模型已加载：{modelPath}, provider={executionProvider}");
            return new VisionObbDetector(
                session,
                inputName,
                outputName,
                channelsFirst,
                outputLayout,
                scoreThreshold,
                nmsThreshold,
                executionProvider);
        }
        catch (Exception ex)
        {
            session?.Dispose();
            RuntimeLog.Warn(
                "CameraVision",
                $"增强识别模型加载失败（{ex.GetType().Name}）：{ex.Message}");
            return null;
        }
    }

    internal static bool TryResolveContract(
        int[]? inputDimensions,
        int[]? outputDimensions,
        out bool channelsFirst,
        out string error) =>
        TryResolveContract(
            inputDimensions,
            outputDimensions,
            configuredOutputLayout: null,
            out channelsFirst,
            out _,
            out error);

    internal static bool TryResolveContract(
        int[]? inputDimensions,
        int[]? outputDimensions,
        string? configuredOutputLayout,
        out bool channelsFirst,
        out VisionObbOutputLayout outputLayout,
        out string error)
    {
        channelsFirst = true;
        outputLayout = VisionObbOutputLayout.Ultralytics;
        error = "";
        if (inputDimensions is not { Length: 4 }
            || inputDimensions[0] is not (1 or -1)
            || inputDimensions[1] != 3
            || inputDimensions[2] != InputSize
            || inputDimensions[3] != InputSize)
        {
            error =
                $"模型输入必须是 1×3×{InputSize}×{InputSize}，实际为 "
                + FormatDimensions(inputDimensions);
            return false;
        }

        if (outputDimensions is not { Length: 3 }
            || outputDimensions[0] is not (1 or -1))
        {
            error = $"模型输出维度无效：{FormatDimensions(outputDimensions)}";
            return false;
        }

        channelsFirst = outputDimensions[1] == -1
            ? false
            : outputDimensions[2] == -1
                ? true
                : outputDimensions[1] <= outputDimensions[2];
        int channels = channelsFirst ? outputDimensions[1] : outputDimensions[2];
        int anchors = channelsFirst ? outputDimensions[2] : outputDimensions[1];
        if (channels == -1)
        {
            error =
                "模型输出必须包含固定的通道维，不能同时把通道数和候选数设为动态"
                + $"实际为 {FormatDimensions(outputDimensions)}";
            return false;
        }
        if (string.Equals(
                configuredOutputLayout,
                "yolox_obb",
                StringComparison.OrdinalIgnoreCase))
        {
            outputLayout = VisionObbOutputLayout.Yolox;
        }
        else if (string.Equals(
                     configuredOutputLayout,
                     "rtmdet_obb",
                     StringComparison.OrdinalIgnoreCase))
        {
            outputLayout = VisionObbOutputLayout.Rtmdet;
        }
        else if (!string.IsNullOrWhiteSpace(configuredOutputLayout)
                 && !string.Equals(
                     configuredOutputLayout,
                     "ultralytics_obb",
                     StringComparison.OrdinalIgnoreCase))
        {
            error =
                $"不支持的模型输出布局：{configuredOutputLayout}";
            return false;
        }

        int minimumChannels = outputLayout switch
        {
            VisionObbOutputLayout.Yolox => 7,
            VisionObbOutputLayout.Rtmdet => 6,
            _ => 6
        };
        if (channels < minimumChannels || anchors <= 0)
        {
            error =
                $"模型输出布局 {outputLayout} 至少需要 {minimumChannels} 个通道，"
                + $"实际为 {FormatDimensions(outputDimensions)}";
            return false;
        }
        return true;
    }

    private static string FormatDimensions(int[]? dimensions) =>
        dimensions == null ? "unknown" : $"[{string.Join(",", dimensions)}]";

    private static float ReadThreshold(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        float fallback)
    {
        if (!metadata.TryGetValue(key, out string? value)
            || !float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float parsed)
            || !float.IsFinite(parsed))
        {
            return fallback;
        }
        return Math.Clamp(parsed, 0.01f, 0.99f);
    }

    private static (InferenceSession Session, string Provider) CreateSession(
        string modelPath,
        bool preferDirectMl)
    {
        if (preferDirectMl)
        {
            try
            {
                using var directMlOptions = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 4),
                    InterOpNumThreads = 1
                };
                directMlOptions.AppendExecutionProvider_DML(0);
                return (new InferenceSession(modelPath, directMlOptions), "DirectML");
            }
            catch
            {
                // DirectML 初始化失败时继续尝试 CPU。
            }
        }

        using var cpuOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 4),
            InterOpNumThreads = 1
        };
        return (new InferenceSession(modelPath, cpuOptions), "CPU");
    }

    public IReadOnlyList<VisionObbDetection> Detect(Mat frame)
    {
        lock (_detectLock)
        {
            LastRawCandidateCount = 0;
            LastFilteredDetectionCount = 0;
            if (_disposed || frame == null || frame.IsDisposed || frame.Empty())
                return [];

            if (!PrepareLetterbox(
                    frame,
                    _canvas,
                    _resized,
                    _convertedBgr,
                    topLeftPadding: _outputLayout is VisionObbOutputLayout.Yolox
                        or VisionObbOutputLayout.Rtmdet,
                    interpolation: _outputLayout is VisionObbOutputLayout.Yolox
                        or VisionObbOutputLayout.Rtmdet
                        ? InterpolationFlags.Linear
                        : InterpolationFlags.Nearest,
                    ref _canvasSourceWidth,
                    ref _canvasSourceHeight,
                    out double scale,
                    out int padLeft,
                    out int padTop)
                || (_outputLayout == VisionObbOutputLayout.Rtmdet
                    ? !FillRtmdetInput(_canvas, _input.Buffer.Span)
                    : !FillInput(
                        _canvas,
                        _input.Buffer.Span,
                        normalize: _outputLayout != VisionObbOutputLayout.Yolox)))
            {
                return [];
            }

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, _input)]);
            Tensor<float> output = results.First().AsTensor<float>();
            List<VisionObbDetection> candidates = ParseOutput(
                output.ToDenseTensor().Buffer.Span,
                output.Dimensions.ToArray(),
                _channelsFirst,
                _outputLayout,
                _scoreThreshold);
            LastRawCandidateCount = candidates.Count;
            List<VisionObbDetection> kept = SuppressOverlaps(candidates, _nmsThreshold);
            LastFilteredDetectionCount = kept.Count;

            var restored = new List<VisionObbDetection>(kept.Count);
            foreach (VisionObbDetection detection in kept)
            {
                restored.Add(detection with
                {
                    CenterX = (detection.CenterX - padLeft) / scale,
                    CenterY = (detection.CenterY - padTop) / scale,
                    Width = detection.Width / scale,
                    Height = detection.Height / scale
                });
            }
            return restored;
        }
    }

    internal static List<VisionObbDetection> ParseOutput(
        ReadOnlySpan<float> data,
        int[] dimensions,
        bool channelsFirst) =>
        ParseOutput(
            data,
            dimensions,
            channelsFirst,
            VisionObbOutputLayout.Ultralytics,
            ScoreThreshold);

    internal static List<VisionObbDetection> ParseOutput(
        ReadOnlySpan<float> data,
        int[] dimensions,
        bool channelsFirst,
        VisionObbOutputLayout outputLayout,
        float scoreThreshold = ScoreThreshold)
    {
        var detections = new List<VisionObbDetection>();
        if (dimensions.Length < 3)
            return detections;

        int channels = channelsFirst ? dimensions[1] : dimensions[2];
        int anchors = channelsFirst ? dimensions[2] : dimensions[1];
        int minimumChannels = outputLayout == VisionObbOutputLayout.Yolox ? 7 : 6;
        if (channels < minimumChannels || anchors <= 0)
            return detections;

        int classCount = outputLayout switch
        {
            VisionObbOutputLayout.Yolox => channels - 6,
            VisionObbOutputLayout.Rtmdet => 0,
            _ => channels - 5
        };
        for (int anchor = 0; anchor < anchors; anchor++)
        {
            if (outputLayout == VisionObbOutputLayout.Rtmdet)
            {
                float rtmdetScore = Read(
                    data,
                    channels,
                    anchors,
                    5,
                    anchor,
                    channelsFirst);
                if (rtmdetScore < scoreThreshold)
                    continue;
                detections.Add(new VisionObbDetection(
                    Read(data, channels, anchors, 0, anchor, channelsFirst),
                    Read(data, channels, anchors, 1, anchor, channelsFirst),
                    Read(data, channels, anchors, 2, anchor, channelsFirst),
                    Read(data, channels, anchors, 3, anchor, channelsFirst),
                    Read(data, channels, anchors, 4, anchor, channelsFirst)
                        * 180.0 / Math.PI,
                    rtmdetScore,
                    0));
                continue;
            }

            float objectness = outputLayout == VisionObbOutputLayout.Yolox
                ? Read(data, channels, anchors, 5, anchor, channelsFirst)
                : 1;
            float score = 0;
            int bestClassId = 0;
            for (int classIndex = 0; classIndex < classCount; classIndex++)
            {
                float value = Read(
                    data,
                    channels,
                    anchors,
                    (outputLayout == VisionObbOutputLayout.Yolox ? 6 : 4) + classIndex,
                    anchor,
                    channelsFirst);
                float candidateScore = value * objectness;
                if (candidateScore > score)
                {
                    score = candidateScore;
                    bestClassId = classIndex;
                }
            }
            if (score < scoreThreshold)
                continue;

            detections.Add(new VisionObbDetection(
                Read(data, channels, anchors, 0, anchor, channelsFirst),
                Read(data, channels, anchors, 1, anchor, channelsFirst),
                Read(data, channels, anchors, 2, anchor, channelsFirst),
                Read(data, channels, anchors, 3, anchor, channelsFirst),
                (outputLayout == VisionObbOutputLayout.Yolox ? -1 : 1)
                    * Read(
                        data,
                        channels,
                        anchors,
                        outputLayout == VisionObbOutputLayout.Yolox ? 4 : channels - 1,
                        anchor,
                        channelsFirst)
                    * 180.0 / Math.PI,
                score,
                bestClassId));
        }

        detections.Sort((left, right) => right.Score.CompareTo(left.Score));
        return detections;
    }

    internal static List<VisionObbDetection> SuppressOverlaps(
        IReadOnlyList<VisionObbDetection> detections,
        float iouThreshold)
    {
        var kept = new List<VisionObbDetection>(Math.Min(detections.Count, MaxDetections));
        foreach (VisionObbDetection candidate in detections
                     .OrderByDescending(item => item.Width * item.Height)
                     .ThenByDescending(item => item.Score)
                     .Take(MaxDetections * 4))
        {
            if (kept.Any(existing =>
                {
                    (float iou, float containment) = RotatedOverlap(
                        candidate,
                        existing);
                    return iou > iouThreshold || containment > 0.85f;
                }))
                continue;
            kept.Add(candidate);
            if (kept.Count >= MaxDetections)
                break;
        }
        return kept;
    }

    private static (float IoU, float Containment) RotatedOverlap(
        in VisionObbDetection left,
        in VisionObbDetection right)
    {
        double leftArea = Math.Max(0, left.Width * left.Height);
        double rightArea = Math.Max(0, right.Width * right.Height);
        if (leftArea <= 0 || rightArea <= 0)
            return (0, 0);

        var leftRect = new RotatedRect(
            new Point2f((float)left.CenterX, (float)left.CenterY),
            new Size2f((float)left.Width, (float)left.Height),
            (float)left.AngleDegrees);
        var rightRect = new RotatedRect(
            new Point2f((float)right.CenterX, (float)right.CenterY),
            new Size2f((float)right.Width, (float)right.Height),
            (float)right.AngleDegrees);
        RectanglesIntersectTypes intersectionType =
            Cv2.RotatedRectangleIntersection(leftRect, rightRect, out Point2f[] region);
        if (intersectionType == RectanglesIntersectTypes.None
            || region.Length < 3)
        {
            return (0, 0);
        }

        double intersectionArea = Math.Abs(PolygonArea(region));
        double unionArea = leftArea + rightArea - intersectionArea;
        float iou = unionArea <= 0
            ? 0
            : (float)Math.Clamp(intersectionArea / unionArea, 0, 1);
        float containment = (float)Math.Clamp(
            intersectionArea / Math.Max(0.001, Math.Min(leftArea, rightArea)),
            0,
            1);
        return (iou, containment);
    }

    private static double PolygonArea(IReadOnlyList<Point2f> points)
    {
        double twiceArea = 0;
        for (int index = 0; index < points.Count; index++)
        {
            Point2f current = points[index];
            Point2f next = points[(index + 1) % points.Count];
            twiceArea += current.X * next.Y - next.X * current.Y;
        }
        return twiceArea / 2.0;
    }

    private static bool PrepareLetterbox(
        Mat frame,
        Mat canvas,
        Mat resized,
        Mat convertedBgr,
        bool topLeftPadding,
        InterpolationFlags interpolation,
        ref int canvasSourceWidth,
        ref int canvasSourceHeight,
        out double scale,
        out int padLeft,
        out int padTop)
    {
        scale = 1;
        padLeft = 0;
        padTop = 0;
        Mat source;
        switch (frame.Channels())
        {
            case 1:
                Cv2.CvtColor(frame, convertedBgr, ColorConversionCodes.GRAY2BGR);
                source = convertedBgr;
                break;
            case 3:
                source = frame;
                break;
            case 4:
                Cv2.CvtColor(frame, convertedBgr, ColorConversionCodes.BGRA2BGR);
                source = convertedBgr;
                break;
            default:
                return false;
        }

        if (canvasSourceWidth != frame.Width || canvasSourceHeight != frame.Height)
        {
            canvas.SetTo(new Scalar(PadValue, PadValue, PadValue));
            canvasSourceWidth = frame.Width;
            canvasSourceHeight = frame.Height;
        }

        scale = Math.Min((double)InputSize / source.Width, (double)InputSize / source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        padLeft = topLeftPadding ? 0 : (InputSize - width) / 2;
        padTop = topLeftPadding ? 0 : (InputSize - height) / 2;
        Cv2.Resize(
            source,
            resized,
            new Size(width, height),
            interpolation: interpolation);
        using Mat target = canvas.SubMat(new Rect(padLeft, padTop, width, height));
        resized.CopyTo(target);
        return true;
    }

    internal static unsafe bool FillInput(Mat canvas, Span<float> buffer)
        => FillInput(canvas, buffer, normalize: true);

    internal static unsafe bool FillInput(
        Mat canvas,
        Span<float> buffer,
        bool normalize)
    {
        int expectedValues = 3 * InputSize * InputSize;
        if (canvas == null
            || canvas.IsDisposed
            || canvas.Empty()
            || canvas.Type() != MatType.CV_8UC3
            || canvas.Width != InputSize
            || canvas.Height != InputSize
            || !canvas.IsContinuous()
            || buffer.Length < expectedValues)
        {
            return false;
        }

        int plane = InputSize * InputSize;
        float scale = normalize ? 1f / 255f : 1f;
        byte* source = (byte*)canvas.DataPointer;
        fixed (float* destination = buffer)
        {
            for (int offset = 0; offset < plane; offset++)
            {
                int sourceOffset = offset * 3;
                destination[offset] = source[sourceOffset + 2] * scale;
                destination[plane + offset] = source[sourceOffset + 1] * scale;
                destination[2 * plane + offset] = source[sourceOffset] * scale;
            }
        }
        return true;
    }

    internal static unsafe bool FillRtmdetInput(Mat canvas, Span<float> buffer)
    {
        int expectedValues = 3 * InputSize * InputSize;
        if (canvas == null
            || canvas.IsDisposed
            || canvas.Empty()
            || canvas.Type() != MatType.CV_8UC3
            || canvas.Width != InputSize
            || canvas.Height != InputSize
            || !canvas.IsContinuous()
            || buffer.Length < expectedValues)
        {
            return false;
        }

        ReadOnlySpan<float> means = [103.53f, 116.28f, 123.675f];
        ReadOnlySpan<float> standardDeviations = [57.375f, 57.12f, 58.395f];
        int plane = InputSize * InputSize;
        byte* source = (byte*)canvas.DataPointer;
        fixed (float* destination = buffer)
        {
            for (int offset = 0; offset < plane; offset++)
            {
                int sourceOffset = offset * 3;
                destination[offset] =
                    (source[sourceOffset] - means[0]) / standardDeviations[0];
                destination[plane + offset] =
                    (source[sourceOffset + 1] - means[1]) / standardDeviations[1];
                destination[2 * plane + offset] =
                    (source[sourceOffset + 2] - means[2]) / standardDeviations[2];
            }
        }
        return true;
    }

    private static float Read(
        ReadOnlySpan<float> data,
        int channels,
        int anchors,
        int channel,
        int anchor,
        bool channelsFirst) =>
        channelsFirst
            ? data[channel * anchors + anchor]
            : data[anchor * channels + channel];

    public void Dispose()
    {
        lock (_detectLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _session.Dispose();
            _canvas.Dispose();
            _resized.Dispose();
            _convertedBgr.Dispose();
        }
    }

}

/// <summary>可选的 OBB 视觉实现入口；主程序只编译依赖其公开契约。</summary>
public sealed class VisionObbModule : IVisionObbDetectorFactory
{
    public static VisionObbModule Instance { get; } = new();

    public bool IsAvailable => true;

    public IVisionObbDetector? TryLoad(string? modelPath, bool preferDirectMl = true) =>
        VisionObbDetector.TryLoad(modelPath, preferDirectMl);

    public VisionModelMetadata? ReadModelMetadata(string modelPath) =>
        VisionModelDiagnostics.ReadMetadata(modelPath);
}

