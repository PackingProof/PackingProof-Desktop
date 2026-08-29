using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class LegacyOrderPushDeduplicatorTests
{
    [Fact]
    public void IdenticalRequestsReuseCompletedResultInsideCompatibilityWindow()
    {
        var clock = new MutableTimeProvider();
        var deduplicator = new LegacyOrderPushDeduplicator(clock);
        var orders = new List<OrderInfo> { new() { TrackingNumber = "yt-001", BuyerMessage = "轻放" } };
        int operationCount = 0;

        LegacyOrderPushExecution<int> first = deduplicator.Execute(
            "192.168.1.20",
            "direct",
            orders,
            () => Interlocked.Increment(ref operationCount));
        LegacyOrderPushExecution<int> duplicate = deduplicator.Execute(
            "::ffff:192.168.1.20",
            "direct",
            [new OrderInfo { TrackingNumber = "YT-001", BuyerMessage = "轻放" }],
            () => Interlocked.Increment(ref operationCount));

        Assert.False(first.IsDuplicate);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(1, duplicate.Result);
        Assert.Equal(1, operationCount);

        clock.Advance(LegacyOrderPushDeduplicator.DuplicateWindow + TimeSpan.FromMilliseconds(1));
        LegacyOrderPushExecution<int> afterWindow = deduplicator.Execute(
            "192.168.1.20",
            "direct",
            orders,
            () => Interlocked.Increment(ref operationCount));
        Assert.False(afterWindow.IsDuplicate);
        Assert.Equal(2, operationCount);
    }

    [Fact]
    public async Task ConcurrentDuplicateWaitsForAndReusesFirstResult()
    {
        var deduplicator = new LegacyOrderPushDeduplicator();
        var orders = new List<OrderInfo> { new() { TrackingNumber = "YT-002" } };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int operationCount = 0;

        Task<LegacyOrderPushExecution<int>> first = Task.Run(() => deduplicator.Execute(
            "192.168.1.20",
            "direct",
            orders,
            () =>
            {
                Interlocked.Increment(ref operationCount);
                started.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return 42;
            }));
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task<LegacyOrderPushExecution<int>> duplicate = Task.Run(() => deduplicator.Execute(
            "192.168.1.20",
            "direct",
            orders,
            () => Interlocked.Increment(ref operationCount)));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(duplicate.IsCompleted);

        release.TrySetResult();
        LegacyOrderPushExecution<int>[] results = await Task.WhenAll(first, duplicate);

        Assert.Equal(1, operationCount);
        Assert.Equal(42, Assert.Single(results, result => !result.IsDuplicate).Result);
        Assert.Equal(42, Assert.Single(results, result => result.IsDuplicate).Result);
    }

    [Fact]
    public async Task ConcurrentFailureReachesBothCallersAndAllowsRetry()
    {
        var deduplicator = new LegacyOrderPushDeduplicator();
        var orders = new List<OrderInfo> { new() { TrackingNumber = "YT-003" } };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task first = Task.Run(() => Assert.Throws<InvalidOperationException>(() => deduplicator.Execute<int>(
            "192.168.1.20",
            "direct",
            orders,
            () =>
            {
                started.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                throw new InvalidOperationException("写入失败");
            })));
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task duplicate = Task.Run(() => Assert.Throws<InvalidOperationException>(() => deduplicator.Execute(
            "192.168.1.20",
            "direct",
            orders,
            () => 1)));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        release.TrySetResult();
        await Task.WhenAll(first, duplicate);
        LegacyOrderPushExecution<int> retry = deduplicator.Execute(
            "192.168.1.20",
            "direct",
            orders,
            () => 7);

        Assert.False(retry.IsDuplicate);
        Assert.Equal(7, retry.Result);
    }

    [Fact]
    public void DifferentSourcesScopesChangedOrdersAndTestsRemainIndependent()
    {
        var deduplicator = new LegacyOrderPushDeduplicator();
        var order = new List<OrderInfo> { new() { TrackingNumber = "YT-004", SellerMemo = "原备注" } };
        _ = deduplicator.Execute("192.168.1.20", "direct", order, () => 1);

        Assert.False(deduplicator.Execute("192.168.1.21", "direct", order, () => 2).IsDuplicate);
        Assert.False(deduplicator.Execute("192.168.1.20", "broadcast:node-a", order, () => 3).IsDuplicate);
        Assert.False(deduplicator.Execute(
            "192.168.1.20",
            "direct",
            [new OrderInfo { TrackingNumber = "YT-004", SellerMemo = "新备注" }],
            () => 4).IsDuplicate);
        Assert.False(deduplicator.Execute(
            "192.168.1.20",
            "direct",
            [new OrderInfo { TrackingNumber = "YT-TEST", IsTest = true }],
            () => 5).IsDuplicate);
        Assert.Equal(
            LegacyOrderPushDeduplicator.CreateBroadcastScope(["NODE-b", "node-A"]),
            LegacyOrderPushDeduplicator.CreateBroadcastScope(["node-a", "node-B"]));
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
