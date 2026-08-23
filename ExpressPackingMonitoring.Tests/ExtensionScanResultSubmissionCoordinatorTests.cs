using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionScanResultSubmissionCoordinatorTests
{
    [Fact]
    public void Submit_PersistsBeforeCompletingBrokerAndAcknowledgesDuplicate()
    {
        using var fixture = new CoordinatorFixture();
        ExtensionScanDelivery delivery = fixture.Publish("scan-task-submit-001");
        ExtensionScanResultRequest request = fixture.Request(delivery);

        ExtensionScanResultSubmissionOutcome accepted = fixture.Coordinator.Submit(
            fixture.Authorization,
            request);
        ExtensionScanResultSubmissionOutcome duplicate = fixture.Coordinator.Submit(
            fixture.Authorization,
            request);

        Assert.Equal(ExtensionScanResultSubmissionDisposition.Accepted, accepted.Disposition);
        Assert.NotNull(accepted.InboxId);
        Assert.Equal(ExtensionResultInboxStates.Pending, fixture.Inbox.Get(accepted.InboxId!.Value)!.State);
        Assert.Equal(ExtensionScanDeliveryState.Completed, fixture.Broker.GetDelivery(delivery.DeliveryId)!.State);
        Assert.Equal(ExtensionScanResultSubmissionDisposition.Duplicate, duplicate.Disposition);
        Assert.Equal(accepted.InboxId, duplicate.InboxId);
    }

    [Fact]
    public void Submit_RejectsExpiredNewRevisionButAcknowledgesPersistedDuplicate()
    {
        using var fixture = new CoordinatorFixture();
        ExtensionScanDelivery delivery = fixture.Publish("scan-task-submit-002");
        ExtensionScanResultRequest request = fixture.Request(delivery);
        fixture.Coordinator.Submit(fixture.Authorization, request);
        fixture.Time.Advance(TimeSpan.FromSeconds(31));

        ExtensionScanResultSubmissionOutcome duplicate = fixture.Coordinator.Submit(
            fixture.Authorization,
            request);
        request.Revision = 2;
        request.ResultId = "stable-result-002";
        request.Measurements[0].Value = "2";
        request.ObservedAtUtc = fixture.Time.GetUtcNow();
        request.Measurements[0].CapturedAtUtc = fixture.Time.GetUtcNow();
        ExtensionScanResultSubmissionOutcome expired = fixture.Coordinator.Submit(
            fixture.Authorization,
            request);

        Assert.Equal(ExtensionScanResultSubmissionDisposition.Duplicate, duplicate.Disposition);
        Assert.Equal(ExtensionScanResultSubmissionDisposition.Expired, expired.Disposition);
    }

    [Fact]
    public void Submit_MapsRevisionAndResultIdConflictsWithoutAdvancingBroker()
    {
        using var fixture = new CoordinatorFixture();
        ExtensionScanDelivery delivery = fixture.Publish("scan-task-submit-003");
        ExtensionScanResultRequest request = fixture.Request(delivery, status: "in_progress");
        fixture.Coordinator.Submit(fixture.Authorization, request);

        request.Measurements[0].Value = "2";
        ExtensionScanResultSubmissionOutcome revisionConflict = fixture.Coordinator.Submit(
            fixture.Authorization,
            request);
        request.Revision = 2;
        ExtensionScanResultSubmissionOutcome resultIdConflict = fixture.Coordinator.Submit(
            fixture.Authorization,
            request);

        Assert.Equal(ExtensionScanResultSubmissionDisposition.RevisionConflict, revisionConflict.Disposition);
        Assert.Equal(ExtensionScanResultSubmissionDisposition.ResultIdConflict, resultIdConflict.Disposition);
        Assert.Equal(1, fixture.Broker.GetDelivery(delivery.DeliveryId)!.LatestRevision);
    }

    [Fact]
    public void Submit_ReturnsDeliveryNotFoundWithoutWritingInbox()
    {
        using var fixture = new CoordinatorFixture();
        ExtensionScanResultRequest request = fixture.Request(fixture.Publish("scan-task-submit-004"));
        request.DeliveryId = "missing-delivery-001";

        ExtensionScanResultSubmissionOutcome outcome = fixture.Coordinator.Submit(
            fixture.Authorization,
            request);

        Assert.Equal(ExtensionScanResultSubmissionDisposition.DeliveryNotFound, outcome.Disposition);
        Assert.Null(fixture.Inbox.ClaimNext());
    }

    private sealed class CoordinatorFixture : IDisposable
    {
        private readonly string _directory;

        internal CoordinatorFixture()
        {
            _directory = Path.Combine(Path.GetTempPath(), "packingproof-extension-coordinator-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 23, 8, 0, 0, TimeSpan.Zero));
            Broker = new ExtensionScanTaskBroker(Time);
            Inbox = new ExtensionResultInboxStore(Path.Combine(_directory, "videos.db"), Time);
            Coordinator = new ExtensionScanResultSubmissionCoordinator(
                Broker,
                new ExtensionScanResultValidator(Time),
                Inbox);
            Authorization = new ExtensionAuthorizationContext
            {
                ExtensionInstanceId = "scale-extension-001",
                ProviderId = "example.scale",
                DisplayName = "测试称重扩展",
                Version = "1.0",
                Source = "test",
                Permissions =
                [
                    ExtensionPermissions.ScanTasksRead,
                    ExtensionPermissions.ScanResultsWrite,
                    ExtensionPermissions.RecordingFieldsWrite
                ],
                Capabilities = [ExtensionScanCapabilities.MeasurementCapture],
                RoutingScope = ExtensionRoutingScope.SelectedRecordingNodes,
                BoundOriginNodeIds = ["recording-node-001"],
                CredentialGeneration = 1,
                ApprovedAtUtc = Time.GetUtcNow(),
                UpdatedAtUtc = Time.GetUtcNow()
            };
        }

        internal MutableTimeProvider Time { get; }
        internal ExtensionScanTaskBroker Broker { get; }
        internal ExtensionResultInboxStore Inbox { get; }
        internal ExtensionScanResultSubmissionCoordinator Coordinator { get; }
        internal ExtensionAuthorizationContext Authorization { get; }

        internal ExtensionScanDelivery Publish(string taskId) => Assert.Single(Broker.Publish(
            new ExtensionScanEvent
            {
                TaskId = taskId,
                OriginNodeId = "recording-node-001",
                RecordingSessionId = "recording-session-001",
                TrackingNumber = "YT123456",
                RecordingMode = "shipping",
                OccurredAtUtc = Time.GetUtcNow(),
                SoftDeadlineUtc = Time.GetUtcNow().AddSeconds(5),
                ExpiresAtUtc = Time.GetUtcNow().AddSeconds(30),
                RequestedCapabilities = [ExtensionScanCapabilities.MeasurementCapture]
            },
            [new ExtensionScanTarget
            {
                ExtensionInstanceId = Authorization.ExtensionInstanceId,
                Capabilities = [ExtensionScanCapabilities.MeasurementCapture]
            }]).Deliveries);

        internal ExtensionScanResultRequest Request(
            ExtensionScanDelivery delivery,
            string status = "completed") => new()
        {
            DeliveryId = delivery.DeliveryId,
            TaskId = delivery.ScanEvent.TaskId,
            ProviderId = Authorization.ProviderId,
            ResultId = "stable-result-001",
            Revision = 1,
            Status = status,
            ObservedAtUtc = Time.GetUtcNow(),
            Measurements =
            [
                new ExtensionMeasurementResult
                {
                    MeasurementType = "weight",
                    Value = "1.25",
                    Unit = "kg",
                    Stable = status == "completed",
                    CapturedAtUtc = Time.GetUtcNow()
                }
            ]
        };

        public void Dispose()
        {
            Inbox.Dispose();
            SqliteTestPool.ClearPoolFor(_directory);
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        internal void Advance(TimeSpan duration) => utcNow += duration;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
