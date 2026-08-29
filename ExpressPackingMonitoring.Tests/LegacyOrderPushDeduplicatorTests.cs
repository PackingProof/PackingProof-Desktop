using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class LegacyOrderPushDeduplicatorTests
{
    [Fact]
    public void IdenticalRealOrdersAreIgnoredInsideCompatibilityWindow()
    {
        var clock = new MutableTimeProvider();
        var deduplicator = new LegacyOrderPushDeduplicator(clock);
        var order = new OrderInfo { TrackingNumber = "yt-001", BuyerMessage = "轻放" };

        using (LegacyOrderPushDeduplicator.LegacyOrderPushDeduplication first = deduplicator.Begin([order]))
        {
            Assert.Single(first.AcceptedItems);
            first.Complete();
        }
        using (LegacyOrderPushDeduplicator.LegacyOrderPushDeduplication duplicate = deduplicator.Begin([
            new OrderInfo { TrackingNumber = "YT-001", BuyerMessage = "轻放" }
        ]))
        {
            Assert.Empty(duplicate.AcceptedItems);
            duplicate.Complete();
        }

        clock.Advance(LegacyOrderPushDeduplicator.DuplicateWindow + TimeSpan.FromMilliseconds(1));
        using LegacyOrderPushDeduplicator.LegacyOrderPushDeduplication afterWindow = deduplicator.Begin([order]);
        Assert.Single(afterWindow.AcceptedItems);
    }

    [Fact]
    public void ChangedOrdersAndTestOrdersRemainAvailable()
    {
        var deduplicator = new LegacyOrderPushDeduplicator();
        using (LegacyOrderPushDeduplicator.LegacyOrderPushDeduplication first = deduplicator.Begin([
            new OrderInfo { TrackingNumber = "YT-002", SellerMemo = "原备注" }
        ]))
        {
            first.Complete();
        }

        using LegacyOrderPushDeduplicator.LegacyOrderPushDeduplication next = deduplicator.Begin([
            new OrderInfo { TrackingNumber = "YT-002", SellerMemo = "新备注" },
            new OrderInfo { TrackingNumber = "YT-TEST", IsTest = true },
            new OrderInfo { TrackingNumber = "YT-TEST", IsTest = true }
        ]);

        Assert.Equal(3, next.AcceptedItems.Count);
    }

    [Fact]
    public void FailedProcessingRollsBackReservation()
    {
        var deduplicator = new LegacyOrderPushDeduplicator();
        var order = new OrderInfo { TrackingNumber = "YT-003" };

        using (LegacyOrderPushDeduplicator.LegacyOrderPushDeduplication failed = deduplicator.Begin([order]))
            Assert.Single(failed.AcceptedItems);

        using LegacyOrderPushDeduplicator.LegacyOrderPushDeduplication retry = deduplicator.Begin([order]);
        Assert.Single(retry.AcceptedItems);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
