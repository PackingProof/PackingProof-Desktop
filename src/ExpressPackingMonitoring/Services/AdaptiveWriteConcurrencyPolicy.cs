using System.Diagnostics;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// Uses real completed upload writes as the benchmark signal. The media type only
/// chooses a cautious starting point; concurrency then follows observed throughput.
/// </summary>
internal sealed class AdaptiveWriteConcurrencyPolicy
{
    internal const int MaximumConcurrency = 16;

    private readonly Func<long> _timestampProvider;
    private readonly long _windowTicks;
    private long _windowStarted;
    private long _windowBytes;
    private long _windowWriteDurationTicks;
    private long _lastWriteCompleted;
    private int _windowWrites;
    private int _stableWindows;
    private double _bestThroughput;
    private double _bestAverageLatencyMs;
    private int _bestConcurrencyLimit;
    private bool _probingHigherConcurrency;
    private int _probeCooldownWindows;

    internal AdaptiveWriteConcurrencyPolicy(
        int initialConcurrency,
        Func<long>? timestampProvider = null,
        TimeSpan? adaptationWindow = null)
    {
        CurrentLimit = Math.Clamp(initialConcurrency, 1, MaximumConcurrency);
        _bestConcurrencyLimit = CurrentLimit;
        _timestampProvider = timestampProvider ?? Stopwatch.GetTimestamp;
        TimeSpan window = adaptationWindow ?? TimeSpan.FromSeconds(1);
        _windowTicks = Math.Max(
            1,
            (long)Math.Round(window.TotalSeconds * Stopwatch.Frequency));
    }

    internal int CurrentLimit { get; private set; }

    internal void RecordWriteFailure()
    {
        CurrentLimit = Math.Max(1, CurrentLimit / 2);
        _bestThroughput = 0;
        _bestAverageLatencyMs = 0;
        _bestConcurrencyLimit = CurrentLimit;
        _stableWindows = 0;
        _probingHigherConcurrency = false;
        _probeCooldownWindows = 3;
        _windowStarted = 0;
        _windowBytes = 0;
        _windowWriteDurationTicks = 0;
        _windowWrites = 0;
        _lastWriteCompleted = 0;
    }

    internal void RecordWrite(
        int bytesWritten,
        TimeSpan duration,
        bool hasQueuedWriters)
    {
        if (bytesWritten <= 0 || duration <= TimeSpan.Zero)
            return;

        long now = _timestampProvider();
        bool longIdleGap =
            _lastWriteCompleted > 0
            && now - _lastWriteCompleted > _windowTicks * 2;
        if (_windowStarted == 0 || longIdleGap)
        {
            _windowStarted = now;
            _windowBytes = 0;
            _windowWriteDurationTicks = 0;
            _windowWrites = 0;
        }
        _windowBytes += bytesWritten;
        _windowWriteDurationTicks += duration.Ticks;
        _windowWrites++;
        _lastWriteCompleted = now;

        long elapsedTicks = now - _windowStarted;
        if (elapsedTicks < _windowTicks)
            return;
        if (_windowWrites < 2
            && _windowWriteDurationTicks < _windowTicks / 2)
        {
            return;
        }

        double elapsedSeconds = elapsedTicks / (double)Stopwatch.Frequency;
        double throughput = elapsedSeconds <= 0
            ? 0
            : _windowBytes / elapsedSeconds;
        double averageLatencyMs = _windowWrites == 0
            ? 0
            : TimeSpan.FromTicks(_windowWriteDurationTicks / _windowWrites).TotalMilliseconds;
        Adapt(throughput, averageLatencyMs, hasQueuedWriters);

        _windowStarted = now;
        _windowBytes = 0;
        _windowWriteDurationTicks = 0;
        _windowWrites = 0;
    }

    private void Adapt(
        double throughput,
        double averageLatencyMs,
        bool hasQueuedWriters)
    {
        if (throughput <= 0)
            return;

        if (_bestThroughput <= 0)
        {
            _bestThroughput = throughput;
            _bestAverageLatencyMs = averageLatencyMs;
            _bestConcurrencyLimit = CurrentLimit;
            return;
        }
        if (!hasQueuedWriters)
            return;

        double throughputRatio = throughput / _bestThroughput;
        double latencyRatio = _bestAverageLatencyMs <= 0
            ? 1
            : averageLatencyMs / _bestAverageLatencyMs;
        bool degraded =
            throughputRatio < 0.70
            || (throughputRatio <= 1.05 && latencyRatio > 2.0);
        if (degraded)
        {
            int decrease = Math.Max(1, CurrentLimit / 4);
            CurrentLimit = Math.Max(1, CurrentLimit - decrease);
            _stableWindows = 0;
            _probingHigherConcurrency = false;
            return;
        }

        if (throughputRatio > 1.05)
        {
            _bestThroughput = throughput;
            _bestAverageLatencyMs = averageLatencyMs;
            _bestConcurrencyLimit = CurrentLimit;
            _stableWindows = 0;
            _probingHigherConcurrency = false;
            return;
        }

        if (CurrentLimit >= MaximumConcurrency)
            return;

        if (_probingHigherConcurrency)
        {
            CurrentLimit = Math.Max(1, _bestConcurrencyLimit);
            _probingHigherConcurrency = false;
            _probeCooldownWindows = 3;
            _stableWindows = 0;
            return;
        }

        if (throughputRatio < 0.95)
        {
            _stableWindows = 0;
            return;
        }
        if (_probeCooldownWindows > 0)
        {
            _probeCooldownWindows--;
            return;
        }

        _stableWindows++;
        if (_stableWindows < 2)
            return;

        CurrentLimit++;
        _probingHigherConcurrency = true;
        _stableWindows = 0;
    }
}
