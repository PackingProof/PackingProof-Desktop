using ExpressPackingMonitoring.Logging;
using System.Diagnostics;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 串行执行后台刷新，重复请求只保留最新一次。
/// 高频状态事件不能每次都直接查数据库；同时刷新必须串行，避免并发查询互相锁住。
/// </summary>
internal sealed class CoalescingBackgroundRefresh : IDisposable
{
    private readonly object _gate = new();
    private PendingRefresh? _pending;
    private TaskCompletionSource<bool>? _idle;
    private bool _running;
    private bool _disposed;

    internal void Request(string label, Func<Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            if (_disposed)
                return;

            _pending = new PendingRefresh(label, operation);
            if (_running)
                return;

            _running = true;
            _idle = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = Task.Run(RunLoopAsync);
    }

    internal Task WaitForIdleAsync()
    {
        lock (_gate)
        {
            return _running && _idle != null
                ? _idle.Task
                : Task.CompletedTask;
        }
    }

    private async Task RunLoopAsync()
    {
        while (true)
        {
            PendingRefresh? pending;
            TaskCompletionSource<bool>? idle;
            lock (_gate)
            {
                pending = _pending;
                _pending = null;
                if (pending == null || _disposed)
                {
                    _running = false;
                    idle = _idle;
                    _idle = null;
                    idle?.TrySetResult(true);
                    return;
                }
            }

            try
            {
                long started = Stopwatch.GetTimestamp();
                await pending.Operation().ConfigureAwait(false);
                TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed >= TimeSpan.FromSeconds(2))
                {
                    RuntimeLog.Warn(
                        "UiRefresh",
                        $"{pending.Label} 后台刷新耗时 {elapsed.TotalSeconds:F1}s");
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn(
                    "UiRefresh",
                    $"后台界面刷新失败：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending = null;
            if (_running)
                return;
            _idle?.TrySetResult(true);
            _idle = null;
        }
    }

    private sealed record PendingRefresh(string Label, Func<Task> Operation);
}
