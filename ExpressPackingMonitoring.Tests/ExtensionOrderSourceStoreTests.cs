using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionOrderSourceStoreTests
{
    [Fact]
    public void Apply_MergesProvidersWithoutLettingNotFoundClearAnotherProvider()
    {
        using var fixture = new StoreFixture();
        fixture.Store.Apply(fixture.Update(
            "erp-extension-001",
            "alpha.erp",
            1,
            ExtensionScanResultStatus.Found,
            [Order("ORDER-A", "买家留言", "", 2, "水杯", "none")]));
        ExtensionOrderMergeResult merged = fixture.Store.Apply(fixture.Update(
            "erp-extension-002",
            "beta.erp",
            2,
            ExtensionScanResultStatus.NotFound));

        Assert.NotNull(merged.Order);
        Assert.Equal("ORDER-A", merged.Order!.OrderId);
        Assert.Equal(2, merged.Order.TotalItemCount);
        Assert.Equal("水杯 ×2", merged.Order.ProductInfo);
        Assert.Equal(2, merged.RespondedProviderCount);
        Assert.Equal(1, merged.NotFoundProviderCount);
    }

    [Fact]
    public void Apply_TransientFailurePreservesConfirmedDataAndExplicitNotFoundClearsOnlyItsSource()
    {
        using var fixture = new StoreFixture();
        fixture.Store.Apply(fixture.Update(
            "erp-extension-001",
            "alpha.erp",
            1,
            ExtensionScanResultStatus.Found,
            [Order("ORDER-A", "", "备注", 1, "商品 A", "none")]));
        ExtensionOrderMergeResult transient = fixture.Store.Apply(fixture.Update(
            "erp-extension-001",
            "alpha.erp",
            2,
            ExtensionScanResultStatus.Timeout,
            observedAt: Utc(8, 0, 2)));
        ExtensionOrderMergeResult notFound = fixture.Store.Apply(fixture.Update(
            "erp-extension-001",
            "alpha.erp",
            3,
            ExtensionScanResultStatus.NotFound,
            observedAt: Utc(8, 0, 3)));

        Assert.NotNull(transient.Order);
        Assert.True(transient.HasTransientFailure);
        Assert.Null(notFound.Order);
        Assert.Equal(1, notFound.NotFoundProviderCount);
    }

    [Fact]
    public void Apply_UsesNewestProviderCopyPerOrderAndStrongestRefundState()
    {
        using var fixture = new StoreFixture();
        fixture.Store.Apply(fixture.Update(
            "erp-extension-001",
            "alpha.erp",
            1,
            ExtensionScanResultStatus.Found,
            [Order("ORDER-A", "旧留言", "", 1, "旧商品", "requested")],
            observedAt: Utc(8, 0, 1)));
        fixture.Store.Apply(fixture.Update(
            "erp-extension-002",
            "beta.erp",
            2,
            ExtensionScanResultStatus.Found,
            [Order("ORDER-A", "新留言", "", 3, "新商品", "none")],
            observedAt: Utc(8, 0, 2)));
        ExtensionOrderMergeResult merged = fixture.Store.Apply(fixture.Update(
            "refund-extension-001",
            "refund.erp",
            3,
            ExtensionScanResultStatus.Found,
            [Order("ORDER-A", "", "", 0, "", "refunded", "退款完成")],
            capability: ExtensionScanCapabilities.RefundLookup,
            observedAt: Utc(8, 0, 3)));

        Assert.NotNull(merged.Order);
        Assert.Equal("新留言", merged.Order!.BuyerMessage);
        Assert.Equal("新商品 ×3", merged.Order.ProductInfo);
        Assert.Equal(3, merged.Order.TotalItemCount);
        Assert.True(merged.Order.IsPrintedRefund);
        Assert.Equal("refunded", merged.Order.RefundStatus);
        Assert.Equal("退款完成", merged.Order.RefundProductInfo);
    }

    [Fact]
    public void Apply_LateOlderConclusionCannotOverwriteNewerState()
    {
        using var fixture = new StoreFixture();
        fixture.Store.Apply(fixture.Update(
            "erp-extension-001",
            "alpha.erp",
            2,
            ExtensionScanResultStatus.NotFound,
            observedAt: Utc(8, 0, 2)));
        ExtensionOrderMergeResult result = fixture.Store.Apply(fixture.Update(
            "erp-extension-001",
            "alpha.erp",
            1,
            ExtensionScanResultStatus.Found,
            [Order("ORDER-A", "", "", 1, "旧商品", "none")],
            observedAt: Utc(8, 0, 1)));

        Assert.Null(result.Order);
        Assert.Equal(1, result.NotFoundProviderCount);
    }

    private static ExtensionNormalizedOrder Order(
        string orderId,
        string buyerMessage,
        string sellerMemo,
        int quantity,
        string productName,
        string refundState,
        string refundReason = "") => new()
    {
        TrackingNumber = "YT123456",
        OrderId = orderId,
        BuyerMessage = buyerMessage,
        SellerMemo = sellerMemo,
        TotalItemCount = quantity,
        Products = quantity > 0
            ? [new ExtensionNormalizedProduct { Name = productName, Quantity = quantity }]
            : [],
        RefundState = refundState,
        RefundReason = refundReason
    };

    private static DateTimeOffset Utc(int hour, int minute, int second) =>
        new(2026, 8, 23, hour, minute, second, TimeSpan.Zero);

    private sealed class StoreFixture : IDisposable
    {
        private readonly string _directory;

        internal StoreFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), "packingproof-extension-order-source-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Store = new ExtensionOrderSourceStore(Path.Combine(_directory, "videos.db"));
        }

        internal ExtensionOrderSourceStore Store { get; }

        internal ExtensionOrderSourceUpdate Update(
            string extensionId,
            string providerId,
            long inboxId,
            ExtensionScanResultStatus status,
            IReadOnlyList<ExtensionNormalizedOrder>? orders = null,
            string capability = ExtensionScanCapabilities.OrderLookup,
            DateTimeOffset? observedAt = null) => new()
        {
            InboxId = inboxId,
            ExtensionInstanceId = extensionId,
            ProviderId = providerId,
            ResultId = $"result-order-{inboxId:000}",
            DeliveryId = $"delivery-order-{inboxId:000}",
            OriginNodeId = "recording-node-001",
            TrackingNumber = "YT123456",
            Capability = capability,
            Status = status,
            ObservedAtUtc = observedAt ?? Utc(8, 0, (int)inboxId),
            Orders = orders ?? []
        };

        public void Dispose()
        {
            Store.Dispose();
            SqliteTestPool.ClearPoolFor(_directory);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
