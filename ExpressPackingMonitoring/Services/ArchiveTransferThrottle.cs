using System.Diagnostics;

namespace ExpressPackingMonitoring.Services;

/// <summary>恢复阶段按字节限制 NAS 读写，避免单个大文件独占磁盘/SMB。</summary>
internal sealed class ArchiveTransferThrottle
{
    private readonly Func<bool> _enabled;
    private readonly long _bytesPerSecond;
    private readonly object _sync = new();
    private long _nextTick;

    public ArchiveTransferThrottle(Func<bool> enabled, long bytesPerSecond)
    {
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _bytesPerSecond = Math.Max(0, bytesPerSecond);
    }

    internal bool IsActive => _bytesPerSecond > 0 && _enabled();

    public async ValueTask WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0 || _bytesPerSecond <= 0 || !_enabled())
            return;

        long delayTicks;
        long now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            if (!_enabled())
            {
                _nextTick = now;
                return;
            }
            long start = Math.Max(now, _nextTick);
            long duration = (long)Math.Ceiling(
                bytes * (double)Stopwatch.Frequency / _bytesPerSecond);
            _nextTick = checked(start + Math.Max(1, duration));
            delayTicks = start - now;
        }
        if (delayTicks <= 0)
            return;
        int delayMs = (int)Math.Min(
            int.MaxValue,
            Math.Ceiling(delayTicks * 1000d / Stopwatch.Frequency));
        await Task.Delay(Math.Max(1, delayMs), cancellationToken).ConfigureAwait(false);
    }
}
