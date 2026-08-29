using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

internal sealed class PrintedRefundLookupCoordinator
{
    private static readonly TimeSpan LookupInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(15);

    private readonly object _sync = new();
    private readonly List<PrintedRefundScanCheck> _pendingChecks = [];
    private readonly Func<IPrintedRefundOrderSource?> _getOrderSource;
    private readonly Action<PrintedRefundScanCheck, OrderInfo?, string> _onResolved;
    private readonly Func<bool> _shouldStop;
    private Task? _lookupTask;
    private DateTime _lastLookupUtc = DateTime.MinValue;

    public PrintedRefundLookupCoordinator(
        Func<IPrintedRefundOrderSource?> getOrderSource,
        Action<PrintedRefundScanCheck, OrderInfo?, string> onResolved,
        Func<bool> shouldStop)
    {
        _getOrderSource = getOrderSource;
        _onResolved = onResolved;
        _shouldStop = shouldStop;
    }

    public void Queue(string trackingNumber, string mode)
    {
        if (string.IsNullOrWhiteSpace(trackingNumber))
            return;

        lock (_sync)
        {
            _pendingChecks.Add(new PrintedRefundScanCheck(
                trackingNumber.Trim().ToUpperInvariant(),
                mode));
            if (_lookupTask == null || _lookupTask.IsCompleted)
                _lookupTask = Task.Run(RunLoopAsync);
        }
    }

    internal static TimeSpan GetLookupDelay(DateTime lastRequestUtc, DateTime nowUtc)
    {
        if (lastRequestUtc == DateTime.MinValue)
            return TimeSpan.Zero;

        TimeSpan remaining = LookupInterval - (nowUtc - lastRequestUtc);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    internal static OrderInfo? ResolveOrder(
        OrderLookupResult lookupResult,
        string trackingNumber,
        OrderInfo? cachedOrder)
    {
        if (lookupResult?.Responded != true)
            return cachedOrder;

        return lookupResult.Orders?.FirstOrDefault(order =>
            string.Equals(
                order?.TrackingNumber?.Trim(),
                trackingNumber?.Trim(),
                StringComparison.OrdinalIgnoreCase));
    }

    private async Task RunLoopAsync()
    {
        while (true)
        {
            TimeSpan delay;
            lock (_sync)
            {
                if (_shouldStop() || _pendingChecks.Count == 0)
                {
                    _lookupTask = null;
                    return;
                }
                delay = GetLookupDelay(_lastLookupUtc, DateTime.UtcNow);
            }

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);

            OrderLookupResult result = new() { Responded = false };
            Dictionary<string, OrderInfo?> cachedOrders = new(StringComparer.OrdinalIgnoreCase);
            string[] trackingNumbers;
            lock (_sync)
            {
                _lastLookupUtc = DateTime.UtcNow;
                trackingNumbers = _pendingChecks
                    .Select(check => check.TrackingNumber)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            try
            {
                IPrintedRefundOrderSource? source = _getOrderSource();
                if (source != null)
                {
                    foreach (string trackingNumber in trackingNumbers)
                        cachedOrders[trackingNumber] = source.GetCachedOrder(trackingNumber);
                    result = await source.RequestFreshSnapshotAsync(LookupTimeout, trackingNumbers);
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error("Scan", "Printed-refund snapshot request failed", ex);
            }

            List<PrintedRefundScanCheck> checks;
            lock (_sync)
            {
                checks = _pendingChecks.ToList();
                _pendingChecks.Clear();
            }

            foreach (PrintedRefundScanCheck check in checks)
            {
                cachedOrders.TryGetValue(check.TrackingNumber, out OrderInfo? cachedOrder);
                OrderInfo? orderInfo = ResolveOrder(result, check.TrackingNumber, cachedOrder);
                _onResolved(
                    check,
                    orderInfo,
                    result.Responded ? "最新订单查询" : "请求失败后的最近缓存");
            }

            RuntimeLog.Info(
                "Scan",
                $"Printed-refund snapshot checked: responded={result.Responded}, returned={result.Orders.Count}, scans={checks.Count}");
        }
    }
}

internal interface IPrintedRefundOrderSource
{
    OrderInfo? GetCachedOrder(string trackingNumber);
    Task<OrderLookupResult> RequestFreshSnapshotAsync(
        TimeSpan timeout,
        IReadOnlyList<string> trackingNumbers);
}

internal sealed class WebServerPrintedRefundOrderSource(WebServer server) : IPrintedRefundOrderSource
{
    public OrderInfo? GetCachedOrder(string trackingNumber) => server.GetOrderInfo(trackingNumber);

    public Task<OrderLookupResult> RequestFreshSnapshotAsync(
        TimeSpan timeout,
        IReadOnlyList<string> trackingNumbers) =>
        server.RequestFreshOrderSnapshotAsync(timeout, trackingNumbers);
}

internal sealed class PrintedRefundScanCheck(string trackingNumber, string mode)
{
    private int _alerted;

    public Guid AlertId { get; } = Guid.NewGuid();
    public string TrackingNumber { get; } = trackingNumber;
    public string Mode { get; } = mode;

    public bool TryMarkAlerted() => Interlocked.Exchange(ref _alerted, 1) == 0;
}
