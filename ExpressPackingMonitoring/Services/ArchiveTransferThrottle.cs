using ExpressPackingMonitoring.Logging;
using System.Diagnostics;

namespace ExpressPackingMonitoring.Services;

internal enum ArchiveLoadState
{
    Healthy,
    Degraded,
    Paused
}

/// <summary>
/// 大量积压恢复期间的自适应 I/O 控制器。系统健康时逐级放量至不限速；
/// UI、预览或摄像头出现压力时快速降档；录像期间在文件分块边界暂停重 I/O。
/// </summary>
internal sealed class ArchiveTransferThrottle
{
    private const long MiB = 1024L * 1024;
    private static readonly long[] DefaultRates =
    [
        24 * MiB,
        48 * MiB,
        96 * MiB,
        192 * MiB,
        384 * MiB,
        768 * MiB,
        0
    ];

    private readonly Func<bool> _enabled;
    private readonly Func<ArchiveLoadState> _loadStateProvider;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _healthyPromotionInterval;
    private readonly TimeSpan _degradedDemotionInterval;
    private readonly IReadOnlyList<long> _rates;
    private readonly object _sync = new();
    private int _rateIndex;
    private DateTimeOffset _healthySince;
    private DateTimeOffset _lastDemotionAt = DateTimeOffset.MinValue;
    private ArchiveLoadState _lastState = ArchiveLoadState.Healthy;
    private long _nextTimestamp;

    public ArchiveTransferThrottle(
        Func<bool> enabled,
        Func<ArchiveLoadState>? loadStateProvider = null,
        TimeProvider? timeProvider = null,
        TimeSpan? healthyPromotionInterval = null,
        TimeSpan? degradedDemotionInterval = null,
        IReadOnlyList<long>? rates = null)
    {
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _loadStateProvider = loadStateProvider ?? (() => ArchiveLoadState.Healthy);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _healthyPromotionInterval = healthyPromotionInterval ?? TimeSpan.FromSeconds(10);
        _degradedDemotionInterval = degradedDemotionInterval ?? TimeSpan.FromSeconds(2);
        _rates = rates is { Count: > 0 } ? rates : DefaultRates;
        _rateIndex = Math.Min(2, _rates.Count - 1);
        _healthySince = _timeProvider.GetUtcNow();
    }

    internal bool IsActive => _enabled();

    /// <summary>0 表示当前已放量到不限速。</summary>
    internal long CurrentBytesPerSecond
    {
        get
        {
            lock (_sync)
                return RefreshStateCore(_timeProvider.GetUtcNow(), _loadStateProvider());
        }
    }

    public async ValueTask WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0 || !_enabled())
        {
            ResetWhenDisabled();
            return;
        }

        while (_enabled())
        {
            ArchiveLoadState state = _loadStateProvider();
            long rate;
            lock (_sync)
                rate = RefreshStateCore(_timeProvider.GetUtcNow(), state);

            if (state != ArchiveLoadState.Paused)
            {
                if (rate > 0)
                    await DelayForBudgetAsync(bytes, rate, cancellationToken).ConfigureAwait(false);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }

        ResetWhenDisabled();
    }

    private long RefreshStateCore(DateTimeOffset now, ArchiveLoadState state)
    {
        if (!_enabled())
            return _rates[Math.Min(2, _rates.Count - 1)];

        if (state == ArchiveLoadState.Paused)
        {
            if (_lastState != ArchiveLoadState.Paused)
                RuntimeLog.Info("Archive", "实时录像已开始，积压归档 I/O 已暂停");
            _lastState = state;
            _healthySince = now;
            _nextTimestamp = 0;
            return _rates[_rateIndex];
        }

        if (_lastState == ArchiveLoadState.Paused)
        {
            _rateIndex = 0;
            _healthySince = now;
            _lastDemotionAt = DateTimeOffset.MinValue;
            RuntimeLog.Info("Archive", $"实时录像已结束，积压归档从 {FormatRate(_rates[_rateIndex])} 恢复");
        }

        if (state == ArchiveLoadState.Degraded)
        {
            _healthySince = now;
            if (_lastDemotionAt == DateTimeOffset.MinValue
                || now - _lastDemotionAt >= _degradedDemotionInterval)
            {
                int oldIndex = _rateIndex;
                _rateIndex = _rates[_rateIndex] == 0
                    ? Math.Min(4, _rates.Count - 1)
                    : Math.Max(0, _rateIndex - 1);
                _lastDemotionAt = now;
                if (_rateIndex != oldIndex)
                {
                    _nextTimestamp = 0;
                    RuntimeLog.Warn("Archive", $"检测到实时路径压力，积压归档降至 {FormatRate(_rates[_rateIndex])}");
                }
            }
        }
        else
        {
            _lastDemotionAt = DateTimeOffset.MinValue;
            if (_lastState != ArchiveLoadState.Healthy)
                _healthySince = now;
            if (_rateIndex < _rates.Count - 1
                && now - _healthySince >= _healthyPromotionInterval)
            {
                _rateIndex++;
                _healthySince = now;
                _nextTimestamp = 0;
                RuntimeLog.Info("Archive", $"实时路径保持正常，积压归档提升至 {FormatRate(_rates[_rateIndex])}");
            }
        }

        _lastState = state;
        return _rates[_rateIndex];
    }

    private async ValueTask DelayForBudgetAsync(
        int bytes,
        long bytesPerSecond,
        CancellationToken cancellationToken)
    {
        long delayTicks;
        long now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            long start = Math.Max(now, _nextTimestamp);
            long duration = (long)Math.Ceiling(
                bytes * (double)Stopwatch.Frequency / bytesPerSecond);
            _nextTimestamp = checked(start + Math.Max(1, duration));
            delayTicks = start - now;
        }
        if (delayTicks <= 0)
            return;
        TimeSpan delay = TimeSpan.FromSeconds(delayTicks / (double)Stopwatch.Frequency);
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private void ResetWhenDisabled()
    {
        lock (_sync)
        {
            _rateIndex = Math.Min(2, _rates.Count - 1);
            _healthySince = _timeProvider.GetUtcNow();
            _lastDemotionAt = DateTimeOffset.MinValue;
            _lastState = ArchiveLoadState.Healthy;
            _nextTimestamp = 0;
        }
    }

    private static string FormatRate(long rate) =>
        rate <= 0 ? "不限速" : $"{rate / MiB} MiB/s";
}
