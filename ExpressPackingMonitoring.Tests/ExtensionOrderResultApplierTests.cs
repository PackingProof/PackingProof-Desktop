using System.IO;
using System.Text.Json;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionOrderResultApplierTests
{
    [Fact]
    public void Apply_PersistsProviderResultAndReturnsMergedExistingOrderModel()
    {
        using var fixture = new ApplierFixture();
        ExtensionOrderMergeResult merged = fixture.Applier.Apply(fixture.Item(
            ExtensionScanResultStatus.Found,
            [fixture.Order("ORDER-001", 3, "蓝色水杯") ]));

        Assert.True(merged.SourceStateChanged);
        Assert.NotNull(merged.Order);
        Assert.Equal("ORDER-001", merged.Order!.OrderId);
        Assert.Equal("蓝色水杯 ×3", merged.Order.ProductInfo);
        Assert.Equal(3, merged.Order.TotalItemCount);
        Assert.Equal("example.erp", merged.Order.ProviderId);
    }

    [Fact]
    public void Apply_TransientResultKeepsPriorConfirmedOrder()
    {
        using var fixture = new ApplierFixture();
        fixture.Applier.Apply(fixture.Item(
            ExtensionScanResultStatus.Found,
            [fixture.Order("ORDER-001", 1, "商品 A")]));

        ExtensionOrderMergeResult result = fixture.Applier.Apply(fixture.Item(
            ExtensionScanResultStatus.Timeout,
            [],
            inboxId: 2,
            observedAt: Utc(8, 0, 2)));

        Assert.True(result.HasTransientFailure);
        Assert.NotNull(result.Order);
        Assert.Equal("商品 A ×1", result.Order!.ProductInfo);
    }

    [Fact]
    public void Apply_RejectsWrongOriginSessionTrackingAndTamperedPayload()
    {
        using var fixture = new ApplierFixture();
        ExtensionResultInboxItem item = fixture.Item(
            ExtensionScanResultStatus.Found,
            [fixture.Order("ORDER-001", 1, "商品 A")]);

        Assert.Throws<InvalidDataException>(() => fixture.Applier.Apply(item with
        {
            OriginNodeId = "other-node-001"
        }));
        Assert.Throws<InvalidDataException>(() => fixture.Applier.Apply(item with
        {
            RecordingSessionId = "missing-session-001"
        }));
        Assert.Throws<InvalidDataException>(() => fixture.Applier.Apply(item with
        {
            TrackingNumber = "OTHER123"
        }));

        ExtensionNormalizedResultPayload tampered = fixture.Payload(
            [new ExtensionNormalizedOrder
            {
                TrackingNumber = "OTHER123",
                OrderId = "ORDER-001",
                TotalItemCount = 1,
                Products = [new ExtensionNormalizedProduct { Name = "商品 A", Quantity = 1 }],
                RefundState = "none"
            }]);
        Assert.Throws<InvalidDataException>(() => fixture.Applier.Apply(item with
        {
            PayloadJson = JsonSerializer.Serialize(tampered, JsonOptions)
        }));
    }

    private static DateTimeOffset Utc(int hour, int minute, int second) =>
        new(2026, 8, 23, hour, minute, second, TimeSpan.Zero);

    private sealed class ApplierFixture : IDisposable
    {
        private readonly string _directory;

        internal ApplierFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), "packingproof-extension-order-applier-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            string databasePath = Path.Combine(_directory, "videos.db");
            Database = new VideoDatabase(databasePath);
            SourceStore = new ExtensionOrderSourceStore(databasePath);
            Applier = new ExtensionOrderResultApplier(Database, SourceStore, "recording-node-001");
            Database.InsertVideoRecord(
                "YT123456",
                "发货",
                "h264",
                "libx264",
                Path.Combine(_directory, "video.mp4"),
                Utc(8, 0, 0).LocalDateTime,
                recordingSessionId: "recording-session-001");
        }

        internal VideoDatabase Database { get; }
        internal ExtensionOrderSourceStore SourceStore { get; }
        internal ExtensionOrderResultApplier Applier { get; }

        internal ExtensionNormalizedOrder Order(string orderId, int quantity, string productName) => new()
        {
            TrackingNumber = "YT123456",
            OrderId = orderId,
            BuyerMessage = "请轻放",
            SellerMemo = "核对颜色",
            TotalItemCount = quantity,
            Products = [new ExtensionNormalizedProduct { Name = productName, Quantity = quantity }],
            RefundState = "none"
        };

        internal ExtensionNormalizedResultPayload Payload(IReadOnlyList<ExtensionNormalizedOrder> orders) => new()
        {
            SchemaVersion = 1,
            Orders = orders
        };

        internal ExtensionResultInboxItem Item(
            ExtensionScanResultStatus status,
            IReadOnlyList<ExtensionNormalizedOrder> orders,
            long inboxId = 1,
            DateTimeOffset? observedAt = null) => new()
        {
            Id = inboxId,
            ExtensionInstanceId = "erp-extension-001",
            ProviderId = "example.erp",
            ResultId = $"order-result-{inboxId:000}",
            DeliveryId = $"order-delivery-{inboxId:000}",
            TaskId = "order-task-001",
            OriginNodeId = "recording-node-001",
            RecordingSessionId = "recording-session-001",
            TrackingNumber = "YT123456",
            Capability = ExtensionScanCapabilities.OrderLookup,
            Revision = inboxId,
            Status = status,
            ObservedAtUtc = observedAt ?? Utc(8, 0, 1),
            PayloadJson = JsonSerializer.Serialize(Payload(orders), JsonOptions),
            State = ExtensionResultInboxStates.Applying,
            CreatedAtUtc = Utc(8, 0, 1),
            UpdatedAtUtc = Utc(8, 0, 1)
        };

        public void Dispose()
        {
            SourceStore.Dispose();
            Database.Dispose();
            SqliteTestPool.ClearPoolFor(_directory);
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
