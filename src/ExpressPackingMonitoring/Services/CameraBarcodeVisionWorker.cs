using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Vision;
using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// OBB 视觉刷新队列。OBB 推理可能持续数百毫秒，必须和 ZXing 解码线程分开，
/// 否则一次推理就会把条码识别拖到 3-4 FPS。推理期间不积压，新帧直接丢弃。
/// </summary>
internal sealed class CameraBarcodeVisionWorker : IDisposable
{
    private readonly CameraBarcodeVisionProcessor _processor;
    private readonly Action<CameraBarcodeRecognitionStatus> _statusChanged;
    private readonly Func<int> _generationProvider;
    private readonly Func<bool> _isDisposed;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationToken _cancellationToken;
    private readonly Task _task;
    private readonly object _gate = new();
    private Mat? _pendingFrame;
    private CameraBarcodeGuideGeometry _pendingGeometry;
    private int _pendingGeneration;
    private bool _processing;
    private int _disposed;

    internal Task Task => _task;

    internal CameraBarcodeVisionWorker(
        CameraBarcodeVisionProcessor processor,
        Action<CameraBarcodeRecognitionStatus> statusChanged,
        Func<int> generationProvider,
        Func<bool> isDisposed,
        CancellationToken cancellationToken)
    {
        _processor = processor;
        _statusChanged = statusChanged;
        _generationProvider = generationProvider;
        _isDisposed = isDisposed;
        _cancellationToken = cancellationToken;
        _task = System.Threading.Tasks.Task.Run(ProcessLoopAsync);
    }

    internal bool TryEnqueue(
        Mat frame,
        CameraBarcodeGuideGeometry geometry,
        int generation)
    {
        if (Volatile.Read(ref _disposed) != 0 || _cancellationToken.IsCancellationRequested)
            return false;

        bool signal;
        lock (_gate)
        {
            // 推理期间不再积压：没有待跑槽，上一帧没处理完时直接丢掉新帧。
            // 必须与 Dispose 的最终检查共用锁，避免关闭瞬间克隆出无人消费的 Mat。
            if (Volatile.Read(ref _disposed) != 0 || _cancellationToken.IsCancellationRequested)
                return false;
            if (_processing || _pendingFrame != null)
                return false;
            // 调用方会继续把原帧交给预览和录像并释放，这里必须持有独立副本。
            _pendingFrame = frame.Clone();
            _pendingGeometry = geometry;
            _pendingGeneration = generation;
            signal = _signal.CurrentCount == 0;
        }

        if (signal)
        {
            try { _signal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }
        return true;
    }

    internal void ClearPending()
    {
        Mat? dropped;
        lock (_gate)
        {
            dropped = _pendingFrame;
            _pendingFrame = null;
        }
        dropped?.Dispose();
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            while (!_cancellationToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(_cancellationToken).ConfigureAwait(false);

                Mat? frame;
                CameraBarcodeGuideGeometry geometry;
                int generation;
                lock (_gate)
                {
                    frame = _pendingFrame;
                    geometry = _pendingGeometry;
                    generation = _pendingGeneration;
                    _pendingFrame = null;
                    _processing = frame != null;
                }
                if (frame == null)
                    continue;

                try
                {
                    _processor.ProcessVisionFrame(frame, geometry);
                    if (generation == _generationProvider() && !_isDisposed())
                    {
                        IReadOnlyList<VisionObbDetection> detections =
                            _processor.ObbDetections;
                        _statusChanged(new CameraBarcodeRecognitionStatus(
                            CameraBarcodeRecognitionState.Idle,
                            ObbDetections: detections.Count == 0 ? null : detections,
                            SelectedObbDetection: _processor.SelectedObbDetection,
                            BarcodeDecodingEvaluated: false,
                            VisionObbAvailable: _processor.VisionObbAvailable,
                            SupportsBarcodeDetection:
                                _processor.SupportsBarcodeDetection));
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error(
                        "CameraBarcode",
                        "Vision OBB worker stopped unexpectedly",
                        ex);
                }
                finally
                {
                    frame.Dispose();
                    lock (_gate)
                        _processing = false;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeLog.Error("CameraBarcode", "Vision OBB worker stopped unexpectedly", ex);
        }
    }

    public void Dispose()
    {
        Mat? dropped;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            Interlocked.Exchange(ref _disposed, 1);
            dropped = _pendingFrame;
            _pendingFrame = null;
        }
        dropped?.Dispose();
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }
}
