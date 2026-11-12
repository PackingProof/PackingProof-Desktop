using System.IO;
using ExpressPackingMonitoring.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

/// <summary>面单检测框：原图像素坐标，角度为「长轴与水平线的夹角」。</summary>
internal readonly record struct LabelObbDetection(
    double CenterX,
    double CenterY,
    double Width,
    double Height,
    double AngleDegrees,
    float Score);

/// <summary>
/// YOLO-OBB 面单检测（可选能力）。
///
/// 作用：在杂乱画面里定位面单，并给出面单的旋转角。有了它就不必靠
/// 全画面梯度方向去猜倾角 —— 画面里同时有两个方向不同的面单、
/// 或者背景有规则斜纹时，梯度法会挑错，模型不会。
///
/// 🔴 模型文件和 ONNX Runtime 原生库都**不随包分发**（保持本项目对
///    Kokoro TTS 的既有约定：可选运行时由使用方自备）。任一缺失时
///    <see cref="TryLoad"/> 返回 null，识别链路自动退回纯图像处理的
///    摆正方案，功能降级但不会报错。
/// </summary>
internal sealed class YoloObbDetector : IDisposable
{
    private const string LogCategory = "CameraBarcode";

    /// <summary>模型输入边长。当前训练与导出都按 640。</summary>
    internal const int InputSize = 640;

    /// <summary>超过这个分数才当作面单，和 ultralytics 默认阈值一致。</summary>
    internal const float ScoreThreshold = 0.25f;

    /// <summary>旋转框 NMS 的 IoU 阈值。</summary>
    internal const float NmsThreshold = 0.45f;

    /// <summary>
    /// letterbox 的填充灰。必须和训练时一致（ultralytics 默认 114），
    /// 用黑色会让上下灰边变成强边缘，模型容易把画面上下两条当成别的东西。
    /// </summary>
    private const byte PadValue = 114;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly bool _channelsFirst;
    private bool _disposed;

    private YoloObbDetector(
        InferenceSession session,
        string inputName,
        string outputName,
        bool channelsFirst)
    {
        _session = session;
        _inputName = inputName;
        _outputName = outputName;
        _channelsFirst = channelsFirst;
    }

    /// <summary>
    /// 尝试加载模型。文件不存在、ONNX Runtime 缺失、模型格式不对
    /// 一律返回 null —— 这是**可选能力**，不能因为加载失败影响主链路。
    /// </summary>
    public static YoloObbDetector? TryLoad(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            return null;
        if (!File.Exists(modelPath))
        {
            RuntimeLog.Warn(LogCategory, $"面单检测模型不存在：{modelPath}，退回图像摆正");
            return null;
        }

        InferenceSession? session = null;
        try
        {
            session = new InferenceSession(modelPath);
            string inputName = session.InputMetadata.Keys.First();
            string outputName = session.OutputMetadata.Keys.First();

            // 输出形状 [1, 通道, 锚点] 或 [1, 锚点, 通道] 都可能（取决于导出方式），
            // 用「哪个维度更像通道数」来判断，别写死。
            int[]? dims = session.OutputMetadata[outputName].Dimensions;
            bool channelsFirst = dims == null || dims.Length < 3 || dims[1] <= dims[2];

            RuntimeLog.Info(LogCategory, $"面单检测模型已加载：{modelPath}");
            return new YoloObbDetector(session, inputName, outputName, channelsFirst);
        }
        catch (Exception ex)
        {
            // 最常见的是 ONNX Runtime 原生库没装（DllNotFoundException）：
            // 这是预期内的降级，不是错误。
            session?.Dispose();
            RuntimeLog.Warn(
                LogCategory,
                $"面单检测模型加载失败（{ex.GetType().Name}），退回图像摆正：{ex.Message}");
            return null;
        }
    }

    /// <summary>在整帧里找面单，返回按分数降序的候选。</summary>
    public IReadOnlyList<LabelObbDetection> Detect(Mat frame)
    {
        if (_disposed || frame == null || frame.IsDisposed || frame.Empty())
            return [];

        using Mat canvas = new(InputSize, InputSize, MatType.CV_8UC3, new Scalar(PadValue, PadValue, PadValue));
        if (!PrepareLetterbox(frame, canvas, out double scale, out int padLeft, out int padTop))
            return [];

        var input = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        FillChannels(canvas, input.Buffer.Span);

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
        Tensor<float> output = results.First().AsTensor<float>();
        ReadOnlySpan<float> data = output.ToDenseTensor().Buffer.Span;

        List<LabelObbDetection> candidates = ParseOutput(data, output.Dimensions.ToArray());
        List<LabelObbDetection> kept = CameraBarcodeObbPolicy.SuppressOverlaps(candidates, NmsThreshold);

        // 网络坐标 → 原图坐标：先减掉 letterbox 补边，再除掉缩放比。
        var detections = new List<LabelObbDetection>(kept.Count);
        foreach (LabelObbDetection candidate in kept)
        {
            detections.Add(candidate with
            {
                CenterX = (candidate.CenterX - padLeft) / scale,
                CenterY = (candidate.CenterY - padTop) / scale,
                Width = candidate.Width / scale,
                Height = candidate.Height / scale
            });
        }
        return detections;
    }

    /// <summary>缩放并居中补边，口径与训练时一致（保持长宽比、不裁切）。</summary>
    private static bool PrepareLetterbox(
        Mat frame,
        Mat canvas,
        out double scale,
        out int padLeft,
        out int padTop)
    {
        scale = 1;
        padLeft = 0;
        padTop = 0;

        // 统一成三通道：调用方给的可能是已经灰度化的 ROI。
        using Mat bgr = new();
        switch (frame.Channels())
        {
            case 1:
                Cv2.CvtColor(frame, bgr, ColorConversionCodes.GRAY2BGR);
                break;
            case 3:
                frame.CopyTo(bgr);
                break;
            case 4:
                Cv2.CvtColor(frame, bgr, ColorConversionCodes.BGRA2BGR);
                break;
            default:
                return false;
        }

        scale = Math.Min((double)InputSize / bgr.Width, (double)InputSize / bgr.Height);
        int width = Math.Max(1, (int)Math.Round(bgr.Width * scale));
        int height = Math.Max(1, (int)Math.Round(bgr.Height * scale));
        padLeft = (InputSize - width) / 2;
        padTop = (InputSize - height) / 2;

        using Mat resized = new();
        // 与既有实现（OBS 端 fill_input）保持一致用最近邻：
        // 双线性会把一维码的细条纹抹平，反而降低远处的检出率。
        Cv2.Resize(bgr, resized, new Size(width, height), 0, 0, InterpolationFlags.Nearest);
        using Mat target = canvas.SubMat(new Rect(padLeft, padTop, width, height));
        resized.CopyTo(target);
        return true;
    }

    /// <summary>
    /// 填 NCHW 张量。🔴 必须是「三个平面」：先全部 R，再全部 G，再全部 B。
    /// 写成像素交错（HWC）的话模型看到的是错乱的图，分类分会直接掉到 0.00x，
    /// 表现成「模型明明能识别却什么都检不出来」。
    /// </summary>
    private static void FillChannels(Mat canvas, Span<float> buffer)
    {
        int plane = InputSize * InputSize;
        for (int y = 0; y < InputSize; y++)
        {
            for (int x = 0; x < InputSize; x++)
            {
                Vec3b pixel = canvas.At<Vec3b>(y, x);
                int offset = y * InputSize + x;
                buffer[offset] = pixel.Item2 / 255f;              // R
                buffer[plane + offset] = pixel.Item1 / 255f;      // G
                buffer[2 * plane + offset] = pixel.Item0 / 255f;  // B
            }
        }
    }

    /// <summary>
    /// 解析 ultralytics OBB 头：每个锚点是
    /// <c>[cx, cy, w, h, 类别分..., 角度]</c>，角度是弧度，在最后一通道。
    /// </summary>
    private List<LabelObbDetection> ParseOutput(ReadOnlySpan<float> data, int[] dimensions)
    {
        var detections = new List<LabelObbDetection>();
        if (dimensions.Length < 3)
            return detections;

        int channels = _channelsFirst ? dimensions[1] : dimensions[2];
        int anchors = _channelsFirst ? dimensions[2] : dimensions[1];
        if (channels < 6 || anchors <= 0)
            return detections;

        int classCount = channels - 5;   // 4 个框参数 + 角度 + 至少 1 个类别
        for (int anchor = 0; anchor < anchors; anchor++)
        {
            float score = 0;
            for (int cls = 0; cls < classCount; cls++)
            {
                float value = Read(data, channels, anchors, 4 + cls, anchor);
                if (value > score)
                    score = value;
            }
            if (score < ScoreThreshold)
                continue;

            detections.Add(new LabelObbDetection(
                Read(data, channels, anchors, 0, anchor),
                Read(data, channels, anchors, 1, anchor),
                Read(data, channels, anchors, 2, anchor),
                Read(data, channels, anchors, 3, anchor),
                Read(data, channels, anchors, channels - 1, anchor) * 180.0 / Math.PI,
                score));
        }

        detections.Sort((a, b) => b.Score.CompareTo(a.Score));
        return detections;
    }

    private float Read(
        ReadOnlySpan<float> data,
        int channels,
        int anchors,
        int channel,
        int anchor) =>
        _channelsFirst
            ? data[channel * anchors + anchor]
            : data[anchor * channels + channel];

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session.Dispose();
    }
}
