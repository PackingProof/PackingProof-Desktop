using System.IO;
using System.Text.Json;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionMeasurementResultApplierTests
{
    [Fact]
    public void Apply_PersistsStableMeasurementAndPublishesFixedTextFields()
    {
        using var fixture = new ApplierFixture();
        IReadOnlyList<RecordingExtensionField>? changed = null;
        var applier = new ExtensionMeasurementResultApplier(
            fixture.Database,
            "recording-node-001",
            (_, fields) => changed = fields);

        int updated = applier.Apply(fixture.Item(
            revision: 1,
            measurements:
            [
                Measurement("weight", "1.25", "kg", stable: true),
                Measurement("length", "30", "cm", stable: true)
            ]));

        Assert.Equal(2, updated);
        Assert.NotNull(changed);
        Assert.Contains(changed!, field => field.FieldName == "weight" && field.Value == "1.25 kg");
        Assert.Contains(changed!, field => field.FieldName == "length" && field.Value == "30 cm");
        Assert.All(changed!, field => Assert.Equal("example.scale", field.Namespace));
    }

    [Fact]
    public void Apply_LateOlderRevisionCannotOverwriteNewerMeasurement()
    {
        using var fixture = new ApplierFixture();
        var applier = new ExtensionMeasurementResultApplier(
            fixture.Database,
            "recording-node-001");

        Assert.Equal(1, applier.Apply(fixture.Item(
            revision: 2,
            measurements: [Measurement("weight", "2.00", "kg", stable: true)])));
        Assert.Equal(0, applier.Apply(fixture.Item(
            revision: 1,
            measurements: [Measurement("weight", "1.00", "kg", stable: true)])));

        RecordingExtensionField field = Assert.Single(
            fixture.Database.GetRecordingExtensionFields("recording-session-001"));
        Assert.Equal("2 kg", field.Value);
    }

    [Fact]
    public void Apply_LateOlderRevisionCannotOverwriteLaterLegacyFieldUpdate()
    {
        using var fixture = new ApplierFixture();
        var applier = new ExtensionMeasurementResultApplier(
            fixture.Database,
            "recording-node-001");
        applier.Apply(fixture.Item(
            revision: 2,
            measurements: [Measurement("weight", "2", "kg", stable: true)]));
        fixture.Database.UpsertRecordingExtensionFields(
            "recording-session-001",
            "example.scale",
            "example.scale",
            new Dictionary<string, string> { ["weight"] = "3 kg" },
            Utc(8, 0, 3).UtcDateTime);

        Assert.Equal(0, applier.Apply(fixture.Item(
            revision: 1,
            measurements: [Measurement("weight", "1", "kg", stable: true)])));
        Assert.Equal(
            "3 kg",
            Assert.Single(fixture.Database.GetRecordingExtensionFields("recording-session-001")).Value);
    }

    [Fact]
    public void Apply_UnstableMeasurementDoesNotEnterFinalRecordingFields()
    {
        using var fixture = new ApplierFixture();
        var applier = new ExtensionMeasurementResultApplier(
            fixture.Database,
            "recording-node-001");

        int updated = applier.Apply(fixture.Item(
            revision: 1,
            measurements: [Measurement("weight", "0.80", "kg", stable: false)]));

        Assert.Equal(0, updated);
        Assert.Empty(fixture.Database.GetRecordingExtensionFields("recording-session-001"));
    }

    [Fact]
    public void Apply_RejectsWrongOriginSessionAndTrackingCorrelation()
    {
        using var fixture = new ApplierFixture();
        var applier = new ExtensionMeasurementResultApplier(
            fixture.Database,
            "recording-node-001");
        ExtensionResultInboxItem item = fixture.Item(
            revision: 1,
            measurements: [Measurement("weight", "1.25", "kg", stable: true)]);

        Assert.Throws<InvalidDataException>(() => applier.Apply(item with
        {
            OriginNodeId = "other-node-001"
        }));
        Assert.Throws<InvalidDataException>(() => applier.Apply(item with
        {
            RecordingSessionId = "missing-session-001"
        }));
        Assert.Throws<InvalidDataException>(() => applier.Apply(item with
        {
            TrackingNumber = "OTHER123"
        }));
    }

    [Fact]
    public void Apply_RevalidatesPersistedPayloadBeforeDatabaseWrite()
    {
        using var fixture = new ApplierFixture();
        var applier = new ExtensionMeasurementResultApplier(
            fixture.Database,
            "recording-node-001");
        ExtensionResultInboxItem item = fixture.Item(
            revision: 1,
            measurements: [Measurement("weight", "1.25", "kg", stable: true)]);
        ExtensionNormalizedResultPayload payload = Payload(
            [Measurement("weight", "1.25", "shell", stable: true)]);

        Assert.Throws<InvalidDataException>(() => applier.Apply(item with
        {
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions)
        }));
        Assert.Empty(fixture.Database.GetRecordingExtensionFields("recording-session-001"));
    }

    private static ExtensionNormalizedMeasurement Measurement(
        string type,
        string value,
        string unit,
        bool stable) => new()
    {
        MeasurementType = type,
        Value = value,
        Unit = unit,
        Stable = stable,
        CapturedAtUtc = Utc(8, 0, 2)
    };

    private static ExtensionNormalizedResultPayload Payload(
        IReadOnlyList<ExtensionNormalizedMeasurement> measurements) => new()
    {
        SchemaVersion = 1,
        Measurements = measurements
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static DateTimeOffset Utc(int hour, int minute, int second) =>
        new(2026, 8, 23, hour, minute, second, TimeSpan.Zero);

    private sealed class ApplierFixture : IDisposable
    {
        internal ApplierFixture()
        {
            Directory = Path.Combine(
                Path.GetTempPath(),
                "packingproof-extension-measurement-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Database = new VideoDatabase(Path.Combine(Directory, "videos.db"));
            Database.InsertVideoRecord(
                "YT123456",
                "发货",
                "h264",
                "libx264",
                Path.Combine(Directory, "video.mp4"),
                Utc(8, 0, 0).LocalDateTime,
                recordingSessionId: "recording-session-001");
        }

        internal string Directory { get; }
        internal VideoDatabase Database { get; }

        internal ExtensionResultInboxItem Item(
            long revision,
            IReadOnlyList<ExtensionNormalizedMeasurement> measurements) => new()
        {
            Id = revision,
            ExtensionInstanceId = "scale-extension-001",
            ProviderId = "example.scale",
            ResultId = $"measurement-result-{revision:000}",
            DeliveryId = "measurement-delivery-001",
            TaskId = "measurement-task-001",
            OriginNodeId = "recording-node-001",
            RecordingSessionId = "recording-session-001",
            TrackingNumber = "YT123456",
            Capability = ExtensionScanCapabilities.MeasurementCapture,
            Revision = revision,
            Status = ExtensionScanResultStatus.Completed,
            ObservedAtUtc = Utc(8, 0, 2),
            PayloadJson = JsonSerializer.Serialize(Payload(measurements), JsonOptions),
            State = ExtensionResultInboxStates.Applying,
            CreatedAtUtc = Utc(8, 0, 2),
            UpdatedAtUtc = Utc(8, 0, 2)
        };

        public void Dispose()
        {
            Database.Dispose();
            SqliteTestPool.ClearPoolFor(Directory);
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
