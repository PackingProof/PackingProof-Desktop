using System.IO;
using System.Text.Json;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionScanResultValidatorTests
{
    [Fact]
    public void Validate_NormalizesStructuredOrderAndComputesProductCount()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 2));
        var validator = new ExtensionScanResultValidator(time);
        ExtensionValidatedScanResult result = validator.Validate(
            Authorization(ExtensionScanCapabilities.OrderLookup),
            Delivery(time, ExtensionScanCapabilities.OrderLookup),
            OrderRequest(time));

        Assert.Equal(ExtensionScanResultStatus.Found, result.InboxSubmission.Status);
        Assert.Equal("YT123456", result.InboxSubmission.TrackingNumber);
        using JsonDocument payload = JsonDocument.Parse(result.InboxSubmission.NormalizedPayloadJson);
        JsonElement order = payload.RootElement.GetProperty("orders")[0];
        Assert.Equal(3, order.GetProperty("totalItemCount").GetInt32());
        Assert.Equal("blue-500", order.GetProperty("products")[0].GetProperty("sku").GetString());
        Assert.Equal("requested", order.GetProperty("refundState").GetString());
    }

    [Fact]
    public void Validate_RejectsOrderFromAnotherTrackingNumberAndMismatchedTotal()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 2));
        var validator = new ExtensionScanResultValidator(time);
        ExtensionAuthorizationContext authorization = Authorization(ExtensionScanCapabilities.OrderLookup);
        ExtensionScanDelivery delivery = Delivery(time, ExtensionScanCapabilities.OrderLookup);
        ExtensionScanResultRequest request = OrderRequest(time);

        request.Orders[0].TrackingNumber = "OTHER123";
        Assert.Throws<InvalidDataException>(() => validator.Validate(authorization, delivery, request));

        request = OrderRequest(time);
        request.Orders[0].TotalItemCount = 4;
        Assert.Throws<InvalidDataException>(() => validator.Validate(authorization, delivery, request));
    }

    [Fact]
    public void Validate_AcceptsStableMeasurementAsSemanticTextOnly()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 2));
        var validator = new ExtensionScanResultValidator(time);
        ExtensionValidatedScanResult result = validator.Validate(
            Authorization(ExtensionScanCapabilities.MeasurementCapture),
            Delivery(time, ExtensionScanCapabilities.MeasurementCapture),
            MeasurementRequest(time));

        using JsonDocument payload = JsonDocument.Parse(result.InboxSubmission.NormalizedPayloadJson);
        JsonElement measurement = payload.RootElement.GetProperty("measurements")[0];
        Assert.Equal("weight", measurement.GetProperty("measurementType").GetString());
        Assert.Equal("1.25", measurement.GetProperty("value").GetString());
        Assert.Equal("kg", measurement.GetProperty("unit").GetString());
        Assert.True(measurement.GetProperty("stable").GetBoolean());
        Assert.DoesNotContain("command", result.InboxSubmission.NormalizedPayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_MeasurementRequiresFieldPermissionAndCorrectNodeBinding()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 2));
        var validator = new ExtensionScanResultValidator(time);
        ExtensionScanDelivery delivery = Delivery(time, ExtensionScanCapabilities.MeasurementCapture);
        ExtensionScanResultRequest request = MeasurementRequest(time);
        ExtensionAuthorizationContext authorization = Authorization(
            ExtensionScanCapabilities.MeasurementCapture);

        Assert.Throws<UnauthorizedAccessException>(() => validator.Validate(
            authorization with
            {
                Permissions =
                [
                    ExtensionPermissions.ScanTasksRead,
                    ExtensionPermissions.ScanResultsWrite
                ]
            },
            delivery,
            request));
        Assert.Throws<UnauthorizedAccessException>(() => validator.Validate(
            authorization with { BoundOriginNodeIds = ["other-recording-node"] },
            delivery,
            request));
    }

    [Fact]
    public void Validate_RejectsUnsupportedMeasurementUnitDuplicateTypeAndZeroStableValue()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 2));
        var validator = new ExtensionScanResultValidator(time);
        ExtensionAuthorizationContext authorization = Authorization(
            ExtensionScanCapabilities.MeasurementCapture);
        ExtensionScanDelivery delivery = Delivery(time, ExtensionScanCapabilities.MeasurementCapture);
        ExtensionScanResultRequest request = MeasurementRequest(time);

        request.Measurements[0].Unit = "lb";
        Assert.Throws<InvalidDataException>(() => validator.Validate(authorization, delivery, request));

        request = MeasurementRequest(time);
        request.Measurements.Add(new ExtensionMeasurementResult
        {
            MeasurementType = "weight",
            Value = "2",
            Unit = "kg",
            Stable = true,
            CapturedAtUtc = time.GetUtcNow()
        });
        Assert.Throws<InvalidDataException>(() => validator.Validate(authorization, delivery, request));

        request = MeasurementRequest(time);
        request.Measurements[0].Value = "0";
        Assert.Throws<InvalidDataException>(() => validator.Validate(authorization, delivery, request));
    }

    [Fact]
    public void Validate_EnforcesCapabilityStatusAndPayloadShape()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 2));
        var validator = new ExtensionScanResultValidator(time);
        ExtensionAuthorizationContext orderAuthorization = Authorization(
            ExtensionScanCapabilities.OrderLookup);
        ExtensionScanDelivery orderDelivery = Delivery(time, ExtensionScanCapabilities.OrderLookup);

        ExtensionScanResultRequest completedOrder = OrderRequest(time);
        completedOrder.Status = "completed";
        Assert.Throws<InvalidDataException>(() => validator.Validate(
            orderAuthorization,
            orderDelivery,
            completedOrder));

        ExtensionScanResultRequest notFound = OrderRequest(time);
        notFound.Status = "not_found";
        Assert.Throws<InvalidDataException>(() => validator.Validate(
            orderAuthorization,
            orderDelivery,
            notFound));
    }

    [Fact]
    public void Validate_AllowsRetryOnlyForTransientStatusWithinTaskLifetime()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 2));
        var validator = new ExtensionScanResultValidator(time);
        ExtensionAuthorizationContext authorization = Authorization(
            ExtensionScanCapabilities.OrderLookup);
        ExtensionScanDelivery delivery = Delivery(time, ExtensionScanCapabilities.OrderLookup);
        ExtensionScanResultRequest request = EmptyRequest(time, "rate_limited");
        request.RetryAfterUtc = time.GetUtcNow().AddSeconds(5);

        ExtensionValidatedScanResult result = validator.Validate(authorization, delivery, request);
        Assert.Equal(request.RetryAfterUtc, result.RetryAfterUtc);

        request = OrderRequest(time);
        request.RetryAfterUtc = time.GetUtcNow().AddSeconds(5);
        Assert.Throws<InvalidDataException>(() => validator.Validate(authorization, delivery, request));
    }

    [Fact]
    public void Validate_DerivesExtensionIdentityAndRejectsProviderSpoofing()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 2));
        var validator = new ExtensionScanResultValidator(time);
        ExtensionAuthorizationContext authorization = Authorization(
            ExtensionScanCapabilities.OrderLookup);
        ExtensionScanResultRequest request = OrderRequest(time);
        request.ProviderId = "another.provider";

        Assert.Throws<UnauthorizedAccessException>(() => validator.Validate(
            authorization,
            Delivery(time, ExtensionScanCapabilities.OrderLookup),
            request));
    }

    private static ExtensionScanResultRequest OrderRequest(MutableTimeProvider time) => new()
    {
        DeliveryId = "delivery-result-001",
        TaskId = "scan-task-result-001",
        ProviderId = "example.erp",
        ResultId = "stable-result-001",
        Revision = 1,
        Status = "found",
        ObservedAtUtc = time.GetUtcNow(),
        Orders =
        [
            new ExtensionOrderResult
            {
                TrackingNumber = "yt123456",
                OrderId = "ORDER-001",
                BuyerMessage = "请轻放",
                SellerMemo = "核对颜色",
                TotalItemCount = 0,
                RefundState = "requested",
                RefundReason = "申请退款",
                Products =
                [
                    new ExtensionProductResult
                    {
                        Name = "蓝色水杯",
                        Sku = "blue-500",
                        MerchantSku = "PDD-001",
                        Quantity = 3
                    }
                ]
            }
        ]
    };

    private static ExtensionScanResultRequest MeasurementRequest(MutableTimeProvider time) => new()
    {
        DeliveryId = "delivery-result-001",
        TaskId = "scan-task-result-001",
        ProviderId = "example.erp",
        ResultId = "stable-result-001",
        Revision = 1,
        Status = "completed",
        ObservedAtUtc = time.GetUtcNow(),
        Measurements =
        [
            new ExtensionMeasurementResult
            {
                MeasurementType = "weight",
                Value = "1.2500",
                Unit = "kg",
                Stable = true,
                CapturedAtUtc = time.GetUtcNow()
            }
        ]
    };

    private static ExtensionScanResultRequest EmptyRequest(
        MutableTimeProvider time,
        string status) => new()
    {
        DeliveryId = "delivery-result-001",
        TaskId = "scan-task-result-001",
        ProviderId = "example.erp",
        ResultId = "stable-result-001",
        Revision = 1,
        Status = status,
        ObservedAtUtc = time.GetUtcNow()
    };

    private static ExtensionAuthorizationContext Authorization(string capability) => new()
    {
        ExtensionInstanceId = "erp-extension-001",
        ProviderId = "example.erp",
        DisplayName = "示例扩展",
        Version = "1.0",
        Source = "test",
        Permissions = capability == ExtensionScanCapabilities.MeasurementCapture
            ?
            [
                ExtensionPermissions.ScanTasksRead,
                ExtensionPermissions.ScanResultsWrite,
                ExtensionPermissions.RecordingFieldsWrite
            ]
            :
            [
                ExtensionPermissions.ScanTasksRead,
                ExtensionPermissions.ScanResultsWrite
            ],
        Capabilities = [capability],
        RoutingScope = capability == ExtensionScanCapabilities.MeasurementCapture
            ? ExtensionRoutingScope.SelectedRecordingNodes
            : ExtensionRoutingScope.AllLocalRecordingNodes,
        BoundOriginNodeIds = capability == ExtensionScanCapabilities.MeasurementCapture
            ? ["recording-node-001"]
            : [],
        CredentialGeneration = 1,
        ApprovedAtUtc = Utc(7, 0, 0),
        UpdatedAtUtc = Utc(7, 0, 0)
    };

    private static ExtensionScanDelivery Delivery(MutableTimeProvider time, string capability) => new()
    {
        DeliveryId = "delivery-result-001",
        ExtensionInstanceId = "erp-extension-001",
        Capability = capability,
        ScanEvent = new ExtensionScanEvent
        {
            TaskId = "scan-task-result-001",
            OriginNodeId = "recording-node-001",
            RecordingSessionId = "recording-session-001",
            TrackingNumber = "YT123456",
            RecordingMode = "shipping",
            OccurredAtUtc = time.GetUtcNow().AddSeconds(-2),
            SoftDeadlineUtc = time.GetUtcNow().AddSeconds(3),
            ExpiresAtUtc = time.GetUtcNow().AddSeconds(28),
            RequestedCapabilities = [capability]
        }
    };

    private static DateTimeOffset Utc(int hour, int minute, int second) =>
        new(2026, 8, 23, hour, minute, second, TimeSpan.Zero);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
