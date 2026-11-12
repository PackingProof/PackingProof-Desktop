using ExpressPackingMonitoring.Logging;
using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 面单定向模型的持有者：按配置懒加载 YOLO-OBB，路径变了就重新加载。
///
/// 单独一个类是因为两件事：
///   ① <see cref="CameraBarcodeRecognitionService"/> 已经贴着源码规模预算，
///      二十多行加载逻辑塞进去会把守卫测试顶穿；
///   ② "模型不可用"的降级判断只集中在这一处，识别链路只管来拿角度。
/// </summary>
internal sealed class CameraBarcodeYoloAngleSource : IDisposable
{
    private readonly Func<(bool Enabled, string Path)> _settingsProvider;
    private readonly object _lock = new();
    private YoloObbDetector? _detector;
    private string? _loadedPath;
    private bool _disposed;

    public CameraBarcodeYoloAngleSource(Func<(bool Enabled, string Path)> settingsProvider)
    {
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
    }

    /// <summary>
    /// 返回面单长轴与水平线的夹角（度）。null 表示这一帧拿不到
    /// （模型没启用 / 没加载成功 / 没检出面单），调用方应退回梯度方向估角。
    /// </summary>
    public double? Detect(Mat roi)
    {
        if (_disposed)
            return null;

        YoloObbDetector? detector = Resolve();
        if (detector == null)
            return null;

        try
        {
            IReadOnlyList<LabelObbDetection> detections = detector.Detect(roi);
            return detections.Count == 0 ? null : detections[0].AngleDegrees;
        }
        catch (Exception ex)
        {
            // 推理失败（运行时被替换、显存不足、模型被覆盖）不该让整帧识别失败。
            RuntimeLog.Warn("CameraBarcode", $"面单定向推理失败：{ex.Message}");
            return null;
        }
    }

    private YoloObbDetector? Resolve()
    {
        (bool enabled, string path) = _settingsProvider();
        if (!enabled || string.IsNullOrWhiteSpace(path))
        {
            lock (_lock)
            {
                _detector?.Dispose();
                _detector = null;
                _loadedPath = null;
            }
            return null;
        }

        lock (_lock)
        {
            if (_detector != null
                && string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return _detector;
            }

            _detector?.Dispose();
            _detector = YoloObbDetector.TryLoad(path);
            // 加载失败留 null 路径：下次调用再试一次（用户可能刚把模型放好）。
            _loadedPath = _detector == null ? null : path;
            return _detector;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _detector?.Dispose();
            _detector = null;
            _loadedPath = null;
        }
    }
}
