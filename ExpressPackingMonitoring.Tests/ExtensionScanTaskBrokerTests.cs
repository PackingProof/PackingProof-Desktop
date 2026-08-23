using System.IO;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionScanTaskBrokerTests
{
    [Fact]
    public void Publish_CreatesIndependentDeliveriesOnlyForMatchingTargets()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 0));
        var broker = new ExtensionScanTaskBroker(time);

        ExtensionScanPublishResult published = broker.Publish(
            Event(time, "task-0001"),
            [
                Target("erp-extension-001", ExtensionScanCapabilities.OrderLookup),
                Target("scale-extension-001", ExtensionScanCapabilities.MeasurementCapture),
                Target("unrelated-extension-001", ExtensionScanCapabilities.RefundLookup)
            ]);

        Assert.Equal(2, published.Deliveries.Count);
        Assert.Empty(published.SkippedTargets);
        ExtensionScanDelivery erp = RequireNotNull(broker.Poll("erp-extension-001"));
        ExtensionScanDelivery scale = RequireNotNull(broker.Poll("scale-extension-001"));
        Assert.NotEqual(erp.DeliveryId, scale.DeliveryId);
        Assert.Equal(erp.ScanEvent.TaskId, scale.ScanEvent.TaskId);
        Assert.Null(broker.Poll("unrelated-extension-001"));
    }

    [Fact]
    public void Poll_IsAtLeastOnceWithoutConcurrentDuplicateDelivery()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 0));
        var broker = new ExtensionScanTaskBroker(time, redeliveryDelay: TimeSpan.FromSeconds(5));
        broker.Publish(Event(time, "task-0002"), [Target("erp-extension-001", ExtensionScanCapabilities.OrderLookup)]);

        ExtensionScanDelivery first = RequireNotNull(broker.Poll("erp-extension-001"));
        Assert.Null(broker.Poll("erp-extension-001"));
        time.Advance(TimeSpan.FromSeconds(5));
        ExtensionScanDelivery repeated = RequireNotNull(broker.Poll("erp-extension-001"));

        Assert.Equal(first.DeliveryId, repeated.DeliveryId);
        Assert.Equal(2, repeated.DeliveryAttempts);
    }

    [Fact]
    public void Acknowledge_ExtendsDeliveryLeaseAndIsIdempotent()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 0));
        var broker = new ExtensionScanTaskBroker(time, redeliveryDelay: TimeSpan.FromSeconds(5));
        broker.Publish(Event(time, "task-ack-0001"), [Target("erp-extension-001", ExtensionScanCapabilities.OrderLookup)]);
        ExtensionScanDelivery delivery = RequireNotNull(broker.Poll("erp-extension-001"));

        Assert.Equal(
            ExtensionScanAcknowledgementDisposition.Accepted,
            broker.Acknowledge("erp-extension-001", delivery.DeliveryId, delivery.ScanEvent.TaskId));
        Assert.Equal(
            ExtensionScanAcknowledgementDisposition.Duplicate,
            broker.Acknowledge("erp-extension-001", delivery.DeliveryId, delivery.ScanEvent.TaskId));
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Null(broker.Poll("erp-extension-001"));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.NotNull(broker.Poll("erp-extension-001"));
    }

    [Fact]
    public void Submit_HandlesDuplicateConflictStaleAndIncreasingRevisions()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 0));
        var broker = new ExtensionScanTaskBroker(time);
        broker.Publish(Event(time, "task-0003"), [Target("scale-extension-001", ExtensionScanCapabilities.MeasurementCapture)]);
        ExtensionScanDelivery delivery = RequireNotNull(broker.Poll("scale-extension-001"));

        ExtensionScanSubmission revision1 = Submission(delivery, 1, ExtensionScanResultStatus.InProgress, 'a');
        Assert.Equal(ExtensionScanSubmissionDisposition.Accepted, broker.Submit(revision1).Disposition);
        Assert.Equal(ExtensionScanSubmissionDisposition.Duplicate, broker.Submit(revision1).Disposition);
        Assert.Equal(
            ExtensionScanSubmissionDisposition.RevisionConflict,
            broker.Submit(revision1 with { PayloadFingerprint = Fingerprint('b') }).Disposition);

        ExtensionScanSubmission revision2 = Submission(delivery, 2, ExtensionScanResultStatus.Completed, 'c');
        ExtensionScanSubmissionResult completed = broker.Submit(revision2);
        Assert.Equal(ExtensionScanSubmissionDisposition.Accepted, completed.Disposition);
        Assert.Equal(ExtensionScanDeliveryState.Completed, completed.Delivery!.State);
        Assert.Equal(
            ExtensionScanSubmissionDisposition.StaleRevision,
            broker.Submit(revision1).Disposition);
        Assert.Null(broker.Poll("scale-extension-001"));
    }

    [Fact]
    public void Submit_RejectsAnotherExtensionAndTaskWithoutMutatingDelivery()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 0));
        var broker = new ExtensionScanTaskBroker(time);
        broker.Publish(Event(time, "task-0004"), [Target("erp-extension-001", ExtensionScanCapabilities.OrderLookup)]);
        ExtensionScanDelivery delivery = RequireNotNull(broker.Poll("erp-extension-001"));
        ExtensionScanSubmission valid = Submission(delivery, 1, ExtensionScanResultStatus.Found, 'd');

        Assert.Equal(
            ExtensionScanSubmissionDisposition.ExtensionMismatch,
            broker.Submit(valid with { ExtensionInstanceId = "other-extension-001" }).Disposition);
        Assert.Equal(
            ExtensionScanSubmissionDisposition.TaskMismatch,
            broker.Submit(valid with { TaskId = "another-task-001" }).Disposition);
        Assert.Equal(0, Assert.Single(broker.GetSnapshot()).LatestRevision);
    }

    [Fact]
    public void ExpiredDeliveryRejectsNewResultButAcknowledgesPriorDuplicate()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 0));
        var broker = new ExtensionScanTaskBroker(time);
        broker.Publish(Event(time, "task-0005"), [Target("scale-extension-001", ExtensionScanCapabilities.MeasurementCapture)]);
        ExtensionScanDelivery delivery = RequireNotNull(broker.Poll("scale-extension-001"));
        ExtensionScanSubmission progress = Submission(delivery, 1, ExtensionScanResultStatus.InProgress, 'e');
        Assert.Equal(ExtensionScanSubmissionDisposition.Accepted, broker.Submit(progress).Disposition);

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(ExtensionScanSubmissionDisposition.Duplicate, broker.Submit(progress).Disposition);
        Assert.Equal(
            ExtensionScanSubmissionDisposition.Expired,
            broker.Submit(Submission(delivery, 2, ExtensionScanResultStatus.Completed, 'f')).Disposition);
        Assert.Equal(ExtensionScanDeliveryState.Expired, Assert.Single(broker.GetSnapshot()).State);
    }

    [Fact]
    public void ApplyDurablyAccepted_CompletesResultPersistedBeforeExpiryBoundary()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 0));
        var broker = new ExtensionScanTaskBroker(time);
        ExtensionScanDelivery delivery = Assert.Single(broker.Publish(
            Event(time, "task-0017"),
            [Target("scale-extension-001", ExtensionScanCapabilities.MeasurementCapture)]).Deliveries);
        ExtensionScanSubmission completed = Submission(
            delivery,
            1,
            ExtensionScanResultStatus.Completed,
            '7');

        time.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal(
            ExtensionScanSubmissionDisposition.Expired,
            broker.Submit(completed).Disposition);
        ExtensionScanSubmissionResult applied = broker.ApplyDurablyAccepted(completed);
        Assert.Equal(ExtensionScanSubmissionDisposition.Accepted, applied.Disposition);
        Assert.Equal(ExtensionScanDeliveryState.Completed, applied.Delivery!.State);
    }

    [Fact]
    public void Publish_SkipsOnlyExtensionAtPendingCapacity()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 0));
        var broker = new ExtensionScanTaskBroker(time, maxPendingDeliveriesPerExtension: 1);
        broker.Publish(Event(time, "task-0006"), [Target("scale-extension-001", ExtensionScanCapabilities.MeasurementCapture)]);

        ExtensionScanPublishResult second = broker.Publish(
            Event(time, "task-0007"),
            [
                Target("scale-extension-001", ExtensionScanCapabilities.MeasurementCapture),
                Target("erp-extension-001", ExtensionScanCapabilities.OrderLookup)
            ]);

        Assert.Contains(second.SkippedTargets, skipped =>
            skipped.ExtensionInstanceId == "scale-extension-001"
            && skipped.Capability == ExtensionScanCapabilities.MeasurementCapture);
        Assert.Single(second.Deliveries);
        Assert.Equal("erp-extension-001", second.Deliveries[0].ExtensionInstanceId);
    }

    [Fact]
    public void RateLimitedResult_DefersRedeliveryUntilRequestedTime()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 0));
        var broker = new ExtensionScanTaskBroker(time, redeliveryDelay: TimeSpan.FromSeconds(5));
        broker.Publish(Event(time, "task-0011"), [Target("erp-extension-001", ExtensionScanCapabilities.OrderLookup)]);
        ExtensionScanDelivery delivery = RequireNotNull(broker.Poll("erp-extension-001"));
        DateTimeOffset retryAt = time.GetUtcNow().AddSeconds(20);

        ExtensionScanSubmissionResult limited = broker.Submit(
            Submission(delivery, 1, ExtensionScanResultStatus.RateLimited, '1') with
            {
                RetryAfterUtc = retryAt
            });

        Assert.Equal(ExtensionScanSubmissionDisposition.Accepted, limited.Disposition);
        Assert.Equal(ExtensionScanDeliveryState.Delivered, limited.Delivery!.State);
        time.Advance(TimeSpan.FromSeconds(19));
        Assert.Null(broker.Poll("erp-extension-001"));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(broker.Poll("erp-extension-001"));
    }

    [Fact]
    public void Publish_RejectsGlobalCapacityWithoutRemovingExistingTask()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 0));
        var broker = new ExtensionScanTaskBroker(time, maxActiveTasks: 1);
        broker.Publish(Event(time, "task-0008"), [Target("erp-extension-001", ExtensionScanCapabilities.OrderLookup)]);

        Assert.Throws<ExtensionScanTaskCapacityException>(() => broker.Publish(
            Event(time, "task-0009"),
            [Target("other-extension-001", ExtensionScanCapabilities.OrderLookup)]));
        Assert.Single(broker.GetSnapshot());
    }

    [Fact]
    public void Publish_WithNoMatchingTargetDoesNotConsumeOrCheckGlobalCapacity()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 0));
        var broker = new ExtensionScanTaskBroker(time, maxActiveTasks: 1);
        broker.Publish(Event(time, "task-0015"), [Target(
            "erp-extension-001",
            ExtensionScanCapabilities.OrderLookup)]);

        ExtensionScanPublishResult unmatched = broker.Publish(
            Event(time, "task-0016"),
            [Target("refund-extension-001", ExtensionScanCapabilities.RefundLookup)]);

        Assert.Empty(unmatched.Deliveries);
        Assert.Empty(unmatched.SkippedTargets);
        Assert.Single(broker.GetSnapshot());
    }

    [Fact]
    public async Task ConcurrentPoll_DeliversOnlyOnceInsideRedeliveryWindow()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 0));
        var broker = new ExtensionScanTaskBroker(time, redeliveryDelay: TimeSpan.FromSeconds(5));
        broker.Publish(Event(time, "task-0010"), [Target("erp-extension-001", ExtensionScanCapabilities.OrderLookup)]);

        ExtensionScanDelivery?[] deliveries = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => Task.Run(() => broker.Poll("erp-extension-001"))));

        Assert.Single(deliveries, delivery => delivery != null);
    }

    [Fact]
    public void Publish_CreatesIndependentDeliveryForEachExtensionCapability()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 0));
        var broker = new ExtensionScanTaskBroker(time);

        ExtensionScanPublishResult published = broker.Publish(
            Event(time, "task-0012"),
            [Target(
                "combined-extension-001",
                ExtensionScanCapabilities.OrderLookup,
                ExtensionScanCapabilities.MeasurementCapture)]);

        Assert.Equal(2, published.Deliveries.Count);
        ExtensionScanDelivery order = Assert.Single(
            published.Deliveries,
            delivery => delivery.Capability == ExtensionScanCapabilities.OrderLookup);
        ExtensionScanDelivery measurement = Assert.Single(
            published.Deliveries,
            delivery => delivery.Capability == ExtensionScanCapabilities.MeasurementCapture);

        Assert.Equal(
            ExtensionScanSubmissionDisposition.Accepted,
            broker.Submit(Submission(order, 1, ExtensionScanResultStatus.Found, '2')).Disposition);
        Assert.Equal(ExtensionScanDeliveryState.Completed, Assert.Single(
            broker.GetSnapshot(),
            delivery => delivery.DeliveryId == order.DeliveryId).State);
        Assert.Equal(ExtensionScanDeliveryState.Pending, Assert.Single(
            broker.GetSnapshot(),
            delivery => delivery.DeliveryId == measurement.DeliveryId).State);
    }

    [Fact]
    public void Publish_RejectsUnknownRequestedCapabilityInsteadOfSilentlyDroppingIt()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 0));
        var broker = new ExtensionScanTaskBroker(time);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => broker.Publish(
            Event(time, "task-0013") with { RequestedCapabilities = ["order.lookpu"] },
            [Target("erp-extension-001", ExtensionScanCapabilities.OrderLookup)]));

        Assert.Contains("order.lookpu", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Submit_RejectsFinalStatusFromAnotherCapability()
    {
        var time = new MutableTimeProvider(Utc(8, 0, 0));
        var broker = new ExtensionScanTaskBroker(time);
        ExtensionScanDelivery measurement = Assert.Single(broker.Publish(
            Event(time, "task-0014"),
            [Target("scale-extension-001", ExtensionScanCapabilities.MeasurementCapture)]).Deliveries);

        Assert.Throws<InvalidDataException>(() => broker.Submit(
            Submission(measurement, 1, ExtensionScanResultStatus.Found, '3')));
        Assert.Equal(0, Assert.Single(broker.GetSnapshot()).LatestRevision);
    }

    private static ExtensionScanEvent Event(MutableTimeProvider time, string taskId) => new()
    {
        TaskId = taskId,
        OriginNodeId = "recording-node-001",
        RecordingSessionId = "recording-session-001",
        TrackingNumber = "yt123456",
        RecordingMode = "shipping",
        OccurredAtUtc = time.GetUtcNow(),
        SoftDeadlineUtc = time.GetUtcNow().AddSeconds(5),
        ExpiresAtUtc = time.GetUtcNow().AddSeconds(30),
        RequestedCapabilities =
        [
            ExtensionScanCapabilities.OrderLookup,
            ExtensionScanCapabilities.MeasurementCapture
        ]
    };

    private static ExtensionScanTarget Target(string id, params string[] capabilities) => new()
    {
        ExtensionInstanceId = id,
        Capabilities = capabilities
    };

    private static ExtensionScanSubmission Submission(
        ExtensionScanDelivery delivery,
        long revision,
        ExtensionScanResultStatus status,
        char fingerprintCharacter) => new()
    {
        ExtensionInstanceId = delivery.ExtensionInstanceId,
        DeliveryId = delivery.DeliveryId,
        TaskId = delivery.ScanEvent.TaskId,
        Revision = revision,
        Status = status,
        PayloadFingerprint = Fingerprint(fingerprintCharacter)
    };

    private static string Fingerprint(char value) => new(value, 64);

    private static T RequireNotNull<T>(T? value) where T : class
    {
        Assert.NotNull(value);
        return value!;
    }

    private static DateTimeOffset Utc(int hour, int minute, int second) =>
        new(2026, 8, 23, hour, minute, second, TimeSpan.Zero);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
