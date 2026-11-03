using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Vision;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System.IO;

namespace ExpressPackingMonitoring.Vision.Obb;

/// <summary>
/// 通用 YOLO-OBB 检测器，可用于面单、商品或其他单类/多类目标。
/// 模型输入固定为 640×640，输出采用 Ultralytics OBB 原始头
/// [cx, cy, w, h, classScore, angle]。
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
        string executionProvider)
    {
        _session = session;
        _inputName = inputName;
        _outputName = outputName;
        _channelsFirst = channelsFirst;
        _executionProvider = executionProvider;
    }

    public string ExecutionProvider => _executionProvider;

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
            if (!TryResolveContract(
                    inputDimensions,
                    outputDimensions,
                    out bool channelsFirst,
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
        out string error)
    {
        channelsFirst = true;
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

        channelsFirst = outputDimensions[1] <= outputDimensions[2];
        int channels = channelsFirst ? outputDimensions[1] : outputDimensions[2];
        int anchors = channelsFirst ? outputDimensions[2] : outputDimensions[1];
        if (channels < 6 || anchors <= 0)
        {
            error =
                "模型输出必须包含至少 cx、cy、w、h、类别分和角度六个通道，"
                + $"实际为 {FormatDimensions(outputDimensions)}";
            return false;
        }
        return true;
    }

    private static string FormatDimensions(int[]? dimensions) =>
        dimensions == null ? "unknown" : $"[{string.Join(",", dimensions)}]";

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
            if (_disposed || frame == null || frame.IsDisposed || frame.Empty())
                return [];

            if (!PrepareLetterbox(
                    frame,
                    _canvas,
                    _resized,
                    _convertedBgr,
                    ref _canvasSourceWidth,
                    ref _canvasSourceHeight,
                    out double scale,
                    out int padLeft,
                    out int padTop)
                || !FillInput(_canvas, _input.Buffer.Span))
            {
                return [];
            }

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, _input)]);
            Tensor<float> output = results.First().AsTensor<float>();
            List<VisionObbDetection> candidates = ParseOutput(
                output.ToDenseTensor().Buffer.Span,
                output.Dimensions.ToArray(),
                _channelsFirst);
            List<VisionObbDetection> kept = SuppressOverlaps(candidates, NmsThreshold);

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
        bool channelsFirst)
    {
        var detections = new List<VisionObbDetection>();
        if (dimensions.Length < 3)
            return detections;

        int channels = channelsFirst ? dimensions[1] : dimensions[2];
        int anchors = channelsFirst ? dimensions[2] : dimensions[1];
        if (channels < 6 || anchors <= 0)
            return detections;

        int classCount = channels - 5;
        for (int anchor = 0; anchor < anchors; anchor++)
        {
            float score = 0;
            int bestClassId = 0;
            for (int classIndex = 0; classIndex < classCount; classIndex++)
            {
                float value = Read(
                    data,
                    channels,
                    anchors,
                    4 + classIndex,
                    anchor,
                    channelsFirst);
                if (value > score)
                {
                    score = value;
                    bestClassId = classIndex;
                }
            }
            if (score < ScoreThreshold)
                continue;

            detections.Add(new VisionObbDetection(
                Read(data, channels, anchors, 0, anchor, channelsFirst),
                Read(data, channels, anchors, 1, anchor, channelsFirst),
                Read(data, channels, anchors, 2, anchor, channelsFirst),
                Read(data, channels, anchors, 3, anchor, channelsFirst),
                Read(data, channels, anchors, channels - 1, anchor, channelsFirst)
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
                    AxisAlignedIoU(candidate, existing) > iouThreshold
                    || AxisAlignedContainment(candidate, existing) > 0.85))
                continue;
            kept.Add(candidate);
            if (kept.Count >= MaxDetections)
                break;
        }
        return kept;
    }

    private static float AxisAlignedIoU(
        in VisionObbDetection left,
        in VisionObbDetection right)
    {
        Bounds a = GetBounds(left);
        Bounds b = GetBounds(right);
        double intersectionWidth = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        double intersectionHeight = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        double intersection = intersectionWidth * intersectionHeight;
        if (intersection <= 0)
            return 0;
        double union = a.Area + b.Area - intersection;
        return union <= 0 ? 0 : (float)(intersection / union);
    }

    private static float AxisAlignedContainment(
        in VisionObbDetection left,
        in VisionObbDetection right)
    {
        Bounds a = GetBounds(left);
        Bounds b = GetBounds(right);
        double intersectionWidth = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        double intersectionHeight = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        double intersection = intersectionWidth * intersectionHeight;
        double smallerArea = Math.Max(0.001, Math.Min(a.Area, b.Area));
        return (float)Math.Clamp(intersection / smallerArea, 0, 1);
    }

    private static Bounds GetBounds(in VisionObbDetection detection)
    {
        Point2f[] corners = VisionObbGeometry.ToCorners(detection);
        double left = corners.Min(point => point.X);
        double top = corners.Min(point => point.Y);
        double right = corners.Max(point => point.X);
        double bottom = corners.Max(point => point.Y);
        return new Bounds(left, top, right, bottom);
    }

    private static bool PrepareLetterbox(
        Mat frame,
        Mat canvas,
        Mat resized,
        Mat convertedBgr,
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
        padLeft = (InputSize - width) / 2;
        padTop = (InputSize - height) / 2;
        Cv2.Resize(
            source,
            resized,
            new Size(width, height),
            interpolation: InterpolationFlags.Nearest);
        using Mat target = canvas.SubMat(new Rect(padLeft, padTop, width, height));
        resized.CopyTo(target);
        return true;
    }

    internal static unsafe bool FillInput(Mat canvas, Span<float> buffer)
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
        const float scale = 1f / 255f;
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

    private readonly record struct Bounds(double Left, double Top, double Right, double Bottom)
    {
        internal double Area => Math.Max(0, Right - Left) * Math.Max(0, Bottom - Top);
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

