using System.Text.Json;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionResultInboxProcessorTests
{
    [Fact]
    public void ProcessNext_AppliesMeasurementAndMarksInboxApplied()
    {
        using var fixture = new ProcessorFixture();
        int notifications = 0;
        fixture.RebuildProcessor((_, _) => { }, (_, _) => notifications++);
        long inboxId = fixture.AcceptMeasurement();

        ExtensionResultProcessingOutcome outcome = fixture.Processor.ProcessNext();

        Assert.Equal(ExtensionResultProcessingDisposition.Applied, outcome.Disposition);
        Assert.Equal(ExtensionResultInboxStates.Applied, fixture.Inbox.Get(inboxId)!.State);
        Assert.Equal("1.25 kg", Assert.Single(fixture.Database.GetRecordingExtensionFields("recording-session-001")).Value);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void ProcessNext_AppliesOrderAndInvokesExistingOrderSink()
    {
        using var fixture = new ProcessorFixture();
        OrderInfo? received = null;
        fixture.RebuildProcessor((_, result) => received = result.Order);
        long inboxId = fixture.AcceptOrder();

        ExtensionResultProcessingOutcome outcome = fixture.Processor.ProcessNext();

        Assert.Equal(ExtensionResultProcessingDisposition.Applied, outcome.Disposition);
        Assert.Equal(ExtensionResultInboxStates.Applied, fixture.Inbox.Get(inboxId)!.State);
        Assert.NotNull(received);
        Assert.Equal("蓝色水杯 ×3", received!.ProductInfo);
    }

    [Fact]
    public void ProcessNext_ReplaysOrderSinkAfterPartialFailure()
    {
        using var fixture = new ProcessorFixture(maxAttempts: 3);
        int calls = 0;
        fixture.RebuildProcessor((_, _) =>
        {
            calls++;
            if (calls == 1) throw new IOException("temporary sink failure");
        });
        long inboxId = fixture.AcceptOrder();

        ExtensionResultProcessingOutcome failed = fixture.Processor.ProcessNext();
        Assert.Equal(ExtensionResultProcessingDisposition.RetryScheduled, failed.Disposition);
        Assert.Equal(ExtensionResultInboxStates.Failed, fixture.Inbox.Get(inboxId)!.State);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        ExtensionResultProcessingOutcome retried = fixture.Processor.ProcessNext();

        Assert.Equal(ExtensionResultProcessingDisposition.Applied, retried.Disposition);
        Assert.Equal(2, calls);
        Assert.Equal(ExtensionResultInboxStates.Applied, fixture.Inbox.Get(inboxId)!.State);
    }

    [Fact]
    public void ProcessNext_ReplaysMeasurementNotificationAfterDatabaseWriteFailureBoundary()
    {
        using var fixture = new ProcessorFixture(maxAttempts: 3);
        int notifications = 0;
        fixture.RebuildProcessor((_, _) => { }, (_, _) =>
        {
            notifications++;
            if (notifications == 1) throw new IOException("temporary notification failure");
        });
        long inboxId = fixture.AcceptMeasurement();

        Assert.Equal(
            ExtensionResultProcessingDisposition.RetryScheduled,
            fixture.Processor.ProcessNext().Disposition);
        Assert.Equal("1.25 kg", Assert.Single(
            fixture.Database.GetRecordingExtensionFields("recording-session-001")).Value);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(
            ExtensionResultProcessingDisposition.Applied,
            fixture.Processor.ProcessNext().Disposition);

        Assert.Equal(2, notifications);
        Assert.Equal(ExtensionResultInboxStates.Applied, fixture.Inbox.Get(inboxId)!.State);
    }

    [Fact]
    public void ProcessNext_MovesRepeatedFailureToDeadLetterAndDoesNotReclaimIt()
    {
        using var fixture = new ProcessorFixture(maxAttempts: 2);
        fixture.RebuildProcessor((_, _) => throw new IOException("persistent failure"));
        long inboxId = fixture.AcceptOrder();

        Assert.Equal(
            ExtensionResultProcessingDisposition.RetryScheduled,
            fixture.Processor.ProcessNext().Disposition);
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(
            ExtensionResultProcessingDisposition.DeadLettered,
            fixture.Processor.ProcessNext().Disposition);

        Assert.Equal(ExtensionResultInboxStates.DeadLetter, fixture.Inbox.Get(inboxId)!.State);
        Assert.Equal(ExtensionResultProcessingDisposition.Empty, fixture.Processor.ProcessNext().Disposition);
    }

    private sealed class ProcessorFixture : IDisposable
    {
        private readonly string _directory;
        private readonly int _maxAttempts;

        internal ProcessorFixture(int maxAttempts = 5)
        {
            _maxAttempts = maxAttempts;
            _directory = Path.Combine(Path.GetTempPath(), "packingproof-extension-processor-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            string databasePath = Path.Combine(_directory, "videos.db");
            Time = new MutableTimeProvider(Utc(8, 0, 0));
            Database = new VideoDatabase(databasePath);
            Inbox = new ExtensionResultInboxStore(databasePath, Time);
            OrderSources = new ExtensionOrderSourceStore(databasePath);
            Database.InsertVideoRecord(
                "YT123456",
                "发货",
                "h264",
                "libx264",
                Path.Combine(_directory, "video.mp4"),
                Utc(8, 0, 0).LocalDateTime,
                recordingSessionId: "recording-session-001");
            RebuildProcessor((_, _) => { });
        }

        internal MutableTimeProvider Time { get; }
        internal VideoDatabase Database { get; }
        internal ExtensionResultInboxStore Inbox { get; }
        internal ExtensionOrderSourceStore OrderSources { get; }
        internal ExtensionResultInboxProcessor Processor { get; private set; } = null!;

        internal void RebuildProcessor(
            Action<ExtensionResultInboxItem, ExtensionOrderMergeResult> orderSink,
            Action<string, IReadOnlyList<RecordingExtensionField>>? fieldsChanged = null)
        {
            Processor = new ExtensionResultInboxProcessor(
                Inbox,
                new ExtensionMeasurementResultApplier(Database, "recording-node-001", fieldsChanged),
                new ExtensionOrderResultApplier(Database, OrderSources, "recording-node-001"),
                orderSink,
                Time,
                _maxAttempts);
        }

        internal long AcceptMeasurement() => Inbox.Accept(Submission(
            ExtensionScanCapabilities.MeasurementCapture,
            ExtensionScanResultStatus.Completed,
            new ExtensionNormalizedResultPayload
            {
                SchemaVersion = 1,
                Measurements =
                [
                    new ExtensionNormalizedMeasurement
                    {
                        MeasurementType = "weight",
                        Value = "1.25",
                        Unit = "kg",
                        Stable = true,
                        CapturedAtUtc = Utc(8, 0, 0)
                    }
                ]
            })).InboxId!.Value;

        internal long AcceptOrder() => Inbox.Accept(Submission(
            ExtensionScanCapabilities.OrderLookup,
            ExtensionScanResultStatus.Found,
            new ExtensionNormalizedResultPayload
            {
                SchemaVersion = 1,
                Orders =
                [
                    new ExtensionNormalizedOrder
                    {
                        TrackingNumber = "YT123456",
                        OrderId = "ORDER-001",
                        TotalItemCount = 3,
                        Products =
                        [
                            new ExtensionNormalizedProduct
                            {
                                Name = "蓝色水杯",
                                Quantity = 3
                            }
                        ],
                        RefundState = "none"
                    }
                ]
            })).InboxId!.Value;

        private ExtensionResultSubmission Submission(
            string capability,
            ExtensionScanResultStatus status,
            ExtensionNormalizedResultPayload payload) => new()
        {
            ExtensionInstanceId = capability == ExtensionScanCapabilities.MeasurementCapture
                ? "scale-extension-001"
                : "order-extension-001",
            ProviderId = capability == ExtensionScanCapabilities.MeasurementCapture
                ? "example.scale"
                : "example.erp",
            ResultId = capability == ExtensionScanCapabilities.MeasurementCapture
                ? "measurement-result-001"
                : "order-result-001",
            DeliveryId = capability == ExtensionScanCapabilities.MeasurementCapture
                ? "measurement-delivery-001"
                : "order-delivery-001",
            TaskId = "scan-task-processor-001",
            OriginNodeId = "recording-node-001",
            RecordingSessionId = "recording-session-001",
            TrackingNumber = "YT123456",
            ExpiresAtUtc = Time.GetUtcNow().AddSeconds(30),
            Capability = capability,
            Revision = 1,
            Status = status,
            ObservedAtUtc = Time.GetUtcNow(),
            NormalizedPayloadJson = JsonSerializer.Serialize(payload, JsonOptions)
        };

        public void Dispose()
        {
            OrderSources.Dispose();
            Inbox.Dispose();
            Database.Dispose();
            SqliteTestPool.ClearPoolFor(_directory);
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static DateTimeOffset Utc(int hour, int minute, int second) =>
        new(2026, 8, 23, hour, minute, second, TimeSpan.Zero);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        internal void Advance(TimeSpan duration) => utcNow += duration;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
