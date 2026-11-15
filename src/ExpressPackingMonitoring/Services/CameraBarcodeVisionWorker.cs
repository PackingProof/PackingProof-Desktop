using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Vision;
using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// OBB 视觉刷新队列。OBB 推理可能持续数百毫秒，必须和 ZXing 解码线程分开，
/// 否则一次推理就会把条码识别拖到 3-4 FPS。这里只保留最新一帧，推理期间到达的
/// 新帧会替换旧帧，不排队。
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
    private bool _disposed;

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
        if (_disposed || _cancellationToken.IsCancellationRequested)
            return false;

        bool signal;
        lock (_gate)
        {
            // 实时档不再人为限频，但硬件跟不上时只允许一个在跑、一个待跑；
            // 更早的帧直接丢掉，避免 30 FPS 的 Mat.Clone 把内存和 CPU 拖满。
            if (_processing || _pendingFrame != null)
                return false;
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
        if (_disposed)
            return;
        _disposed = true;
        ClearPending();
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }
}
