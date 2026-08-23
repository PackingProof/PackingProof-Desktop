using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionRuntimeTests
{
    [Fact]
    public void Publish_WithoutAuthorizedExtensionCreatesNoDelivery()
    {
        using var fixture = new RuntimeFixture(authorizeMeasurement: false);

        ExtensionScanPublishResult published = fixture.Publish();

        Assert.Empty(published.Deliveries);
        Assert.Empty(published.SkippedTargets);
        Assert.Empty(fixture.Runtime.Broker.GetSnapshot());
    }

    [Fact]
    public void MeasurementResult_PersistsAndRaisesWatermarkCallback()
    {
        using var fixture = new RuntimeFixture(authorizeMeasurement: true);
        ExtensionScanDelivery delivery = Assert.Single(fixture.Publish().Deliveries);
        Assert.Equal(delivery.DeliveryId, fixture.Runtime.Broker.Poll("scale-extension-001")!.DeliveryId);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        ExtensionScanResultSubmissionOutcome accepted = fixture.Runtime.Coordinator.Submit(
            fixture.Authorization!,
            new ExtensionScanResultRequest
            {
                DeliveryId = delivery.DeliveryId,
                TaskId = delivery.ScanEvent.TaskId,
                ProviderId = "example.scale",
                ResultId = "measurement-result-001",
                Revision = 1,
                Status = "completed",
                ObservedAtUtc = now,
                Measurements =
                [
                    new ExtensionMeasurementResult
                    {
                        MeasurementType = "weight",
                        Value = "1.25",
                        Unit = "kg",
                        Stable = true,
                        CapturedAtUtc = now
                    }
                ]
            });
        fixture.Runtime.ProcessAvailableResults();

        Assert.Equal(ExtensionScanResultSubmissionDisposition.Accepted, accepted.Disposition);
        Assert.True(SpinWait.SpinUntil(
            () => fixture.Database.GetRecordingExtensionFields(RuntimeFixture.SessionId).Count == 1,
            TimeSpan.FromSeconds(3)));
        RecordingExtensionField field = Assert.Single(
            fixture.Database.GetRecordingExtensionFields(RuntimeFixture.SessionId));
        Assert.Equal("weight", field.FieldName);
        Assert.Equal("1.25 kg", field.Value);
        Assert.True(SpinWait.SpinUntil(() => fixture.ChangedFields.Count == 1, TimeSpan.FromSeconds(1)));
        Assert.Equal("1.25 kg", fixture.ChangedFields[0].Value);
    }

    private sealed class RuntimeFixture : IDisposable
    {
        internal const string SessionId = "recording-session-001";
        private readonly string _directory;

        internal RuntimeFixture(bool authorizeMeasurement)
        {
            _directory = Path.Combine(Path.GetTempPath(), "packingproof-extension-runtime-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            string databasePath = Path.Combine(_directory, "videos.db");
            Database = new VideoDatabase(databasePath);
            Database.InsertVideoRecord(
                "YT123456",
                "发货",
                "h264",
                "libx264",
                Path.Combine(_directory, "video.mp4"),
                DateTime.Now,
                recordingSessionId: SessionId);
            var authorizations = new ExtensionAuthorizationStore(_directory);
            if (authorizeMeasurement)
            {
                Authorization = authorizations.Approve(new ExtensionAuthorizationApproval
                {
                    ExtensionInstanceId = "scale-extension-001",
                    ProviderId = "example.scale",
                    DisplayName = "示例称重扩展",
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
                    BoundOriginNodeIds = ["recording-node-001"]
                }).Authorization;
            }
            Runtime = new ExtensionRuntime(
                Database,
                databasePath,
                "recording-node-001",
                authorizations,
                (_, fields) => ChangedFields = fields,
                _ => { });
        }

        internal VideoDatabase Database { get; }
        internal ExtensionRuntime Runtime { get; }
        internal ExtensionAuthorizationContext? Authorization { get; }
        internal IReadOnlyList<RecordingExtensionField> ChangedFields { get; private set; } = [];

        internal ExtensionScanPublishResult Publish() => Runtime.Publish(
            "recording-node-001",
            SessionId,
            "YT123456",
            "发货");

        public void Dispose()
        {
            Runtime.Dispose();
            Database.Dispose();
            SqliteTestPool.ClearPoolFor(_directory);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
