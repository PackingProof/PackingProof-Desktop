using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 按“下次到期”调度手机版本检查：成功后 6 小时、失败后 5 分钟再查。
/// 检查本身由 <see cref="MobileAppUpdatePolicyProvider"/> 限流，这里只负责安排单次计时，
/// 不用固定 15 秒轮询空转。
/// </summary>
internal sealed class MobileAppUpdateRefreshScheduler : IDisposable
{
    private readonly MobileAppUpdatePolicyProvider _policy;
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _disposed;

    internal MobileAppUpdateRefreshScheduler(MobileAppUpdatePolicyProvider policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <summary>启动时先查一次，之后按到期时间安排。</summary>
    internal void Start()
    {
        if (_disposed)
            return;

        _ = _policy.RefreshInBackground();
        Schedule();
    }

    private void Schedule()
    {
        TimeSpan delay = _policy.GetTimeUntilNextRefresh();
        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                _timer ??= new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
                _timer.Change(delay, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void OnTimer(object? state) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            Task? refresh = _policy.RefreshInBackground();
            if (refresh != null)
                await refresh.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("MobileUpdate", $"手机版本检查调度失败：{ex.Message}");
        }
        finally
        {
            Schedule();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            try { _timer?.Dispose(); } catch { }
            _timer = null;
        }
    }
}
