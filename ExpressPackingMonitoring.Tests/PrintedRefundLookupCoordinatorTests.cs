using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class PrintedRefundLookupCoordinatorTests
{
    [Fact]
    public async Task Queue_NormalizesTrackingNumberAndUsesFreshSnapshot()
    {
        IReadOnlyList<string>? requestedTrackingNumbers = null;
        var resolved = new TaskCompletionSource<(PrintedRefundScanCheck Check, OrderInfo? Order, string Source)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var freshOrder = new OrderInfo
        {
            TrackingNumber = "TRACK-1",
            IsPrintedRefund = true,
            RefundStatus = "SUCCESS"
        };
        var source = new StubOrderSource(
            new OrderInfo { TrackingNumber = "TRACK-1", IsPrintedRefund = false },
            trackingNumbers =>
            {
                requestedTrackingNumbers = trackingNumbers;
                return new OrderLookupResult { Responded = true, Orders = [freshOrder] };
            });
        var coordinator = new PrintedRefundLookupCoordinator(
            () => source,
            (check, order, sourceLabel) => resolved.TrySetResult((check, order, sourceLabel)),
            () => false);

        coordinator.Queue(" track-1 ", "发货");

        var result = await resolved.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(["TRACK-1"], requestedTrackingNumbers);
        Assert.Equal("TRACK-1", result.Check.TrackingNumber);
        Assert.Equal("发货", result.Check.Mode);
        Assert.Same(freshOrder, result.Order);
        Assert.Equal("最新订单查询", result.Source);
    }

    [Fact]
    public async Task Queue_FallsBackToCachedOrderWhenSnapshotDoesNotRespond()
    {
        var cachedOrder = new OrderInfo
        {
            TrackingNumber = "TRACK-2",
            IsPrintedRefund = true
        };
        var resolved = new TaskCompletionSource<(OrderInfo? Order, string Source)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new PrintedRefundLookupCoordinator(
            () => new StubOrderSource(
                cachedOrder,
                _ => new OrderLookupResult { Responded = false }),
            (_, order, source) => resolved.TrySetResult((order, source)),
            () => false);

        coordinator.Queue("TRACK-2", "退货");

        var result = await resolved.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.Same(cachedOrder, result.Order);
        Assert.Equal("请求失败后的最近缓存", result.Source);
    }

    private sealed class StubOrderSource(
        OrderInfo? cachedOrder,
        Func<IReadOnlyList<string>, OrderLookupResult> lookup) : IPrintedRefundOrderSource
    {
        public OrderInfo? GetCachedOrder(string trackingNumber) => cachedOrder;

        public Task<OrderLookupResult> RequestFreshSnapshotAsync(
            TimeSpan timeout,
            IReadOnlyList<string> trackingNumbers) =>
            Task.FromResult(lookup(trackingNumbers));
    }
}
