using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ScanAuditRecorderTests
{
    [Fact]
    public async Task InputPathOnlyQueuesInMemoryUntilBackgroundFlush()
    {
        using var fixture = new Fixture();
        string eventId = fixture.Recorder.Begin(
            ScanInputSource.ScannerGlobal,
            "JD000",
            "JD000",
            wasRecording: false,
            priorRecordId: 0);
        fixture.Recorder.RecordDecision(
            eventId,
            "Start",
            "Ready",
            "start_requested",
            priorRecordId: 0);

        Assert.False(File.Exists(fixture.DatabasePath));

        await fixture.Recorder.FlushForTestsAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(fixture.DatabasePath));
    }

    [Fact]
    public async Task DuplicateIgnoredInputsWithinWindowAreCoalesced()
    {
        using var fixture = new Fixture();
        string firstId = fixture.Recorder.Begin(
            ScanInputSource.CameraObb,
            "JD001",
            "JD001",
            wasRecording: true,
            priorRecordId: 10);
        fixture.Recorder.RecordDecision(
            firstId,
            action: "Ignore",
            reason: "CameraCurrentCodeIgnored",
            outcome: "ignored",
            priorRecordId: 10);

        fixture.Clock.Advance(TimeSpan.FromMilliseconds(800));
        string duplicateId = fixture.Recorder.Begin(
            ScanInputSource.CameraObb,
            "JD001",
            "JD001",
            wasRecording: true,
            priorRecordId: 10);
        fixture.Recorder.RecordDecision(
            duplicateId,
            action: "Ignore",
            reason: "CameraCurrentCodeIgnored",
            outcome: "ignored",
            priorRecordId: 10);

        await fixture.Recorder.FlushForTestsAsync(TestContext.Current.CancellationToken);

        using var store = new ScanEventStore(fixture.DatabasePath);
        ScanEventRecord row = Assert.Single(store.Query());
        Assert.Equal(2, row.RepeatCount);
        Assert.Equal("camera_obb", row.Source);
        Assert.Equal("JD001", row.NormalizedOrder);
        Assert.Equal("ignored", row.Outcome);
        Assert.Equal(10, row.PriorRecordId);
    }

    [Fact]
    public async Task DifferentActionsOnSameCodeAreNotCoalesced()
    {
        using var fixture = new Fixture();
        string startId = fixture.Recorder.Begin(
            ScanInputSource.ScannerGlobal,
            "JD002",
            "JD002",
            wasRecording: false,
            priorRecordId: 0);
        fixture.Recorder.RecordDecision(
            startId,
            "Start",
            "Ready",
            "start_requested",
            priorRecordId: 0);
        fixture.Recorder.PrepareStart(startId);
        fixture.Recorder.RecordRecordingStarted(42, "session-42", @"D:\Videos\JD002.mp4");

        string stopId = fixture.Recorder.Begin(
            ScanInputSource.ScannerGlobal,
            "JD002",
            "JD002",
            wasRecording: true,
            priorRecordId: 42);
        fixture.Recorder.RecordStopFromInput(stopId, 42, "SameCodeMatched");

        await fixture.Recorder.FlushForTestsAsync(TestContext.Current.CancellationToken);

        using var store = new ScanEventStore(fixture.DatabasePath);
        IReadOnlyList<ScanEventRecord> rows = store.Query();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.StartedRecordId == 42 && row.Outcome == "started");
        Assert.Contains(rows, row => row.PriorRecordId == 42 && row.Outcome == "stopped");
    }

    [Fact]
    public async Task RecordingLifecycleUpdatesTheOriginatingScanEvent()
    {
        using var fixture = new Fixture();
        string eventId = fixture.Recorder.Begin(
            ScanInputSource.CameraZxing,
            "JD003",
            "JD003",
            wasRecording: false,
            priorRecordId: 0);
        fixture.Recorder.RecordDecision(
            eventId,
            "Start",
            "Ready",
            "start_requested",
            priorRecordId: 0);
        fixture.Recorder.PrepareStart(eventId);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Recorder.RecordRecordingStarted(55, "session-55", @"D:\Videos\JD003.mp4");
        fixture.Recorder.RecordStopRequested(ScanStopOrigin.SameCode);
        fixture.Clock.Advance(TimeSpan.FromSeconds(12));
        fixture.Recorder.RecordRecordingFinished(
            recordId: 55,
            stopOrigin: ScanStopOrigin.SameCode,
            outcome: "completed",
            durationSeconds: 12.3,
            manualStop: false,
            shortRecording: false);

        await fixture.Recorder.FlushForTestsAsync(TestContext.Current.CancellationToken);

        using var store = new ScanEventStore(fixture.DatabasePath);
        ScanEventRecord row = Assert.Single(store.Query(normalizedOrder: "JD003"));
        Assert.Equal(55, row.StartedRecordId);
        Assert.Equal("session-55", row.SourceSessionId);
        Assert.Equal("JD003.mp4", row.FileName);
        Assert.Equal("completed", row.Outcome);
        Assert.Equal(ScanStopOrigin.SameCode, row.StopOrigin);
        Assert.Equal(12300, row.DurationMs);
    }

    [Fact]
    public async Task FeedbackSnapshotContainsPersistedScanEvents()
    {
        using var fixture = new Fixture();
        string eventId = fixture.Recorder.Begin(
            ScanInputSource.ManualInput,
            "JD004",
            "JD004",
            wasRecording: false,
            priorRecordId: 0);
        fixture.Recorder.RecordDecision(
            eventId,
            "Ignore",
            "InvalidOrderNumber",
            "ignored",
            priorRecordId: 0);
        await fixture.Recorder.FlushForTestsAsync(TestContext.Current.CancellationToken);

        string snapshotPath = Path.Combine(fixture.DirectoryPath, "feedback-scan-events.db");
        Assert.True(ScanEventStore.CreateFeedbackSnapshot(fixture.DatabasePath, snapshotPath));

        using var snapshot = new ScanEventStore(snapshotPath);
        Assert.Single(snapshot.Query(normalizedOrder: "JD004"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "PackingProof-ScanAudit-" + Guid.NewGuid().ToString("N"));

        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            DatabasePath = Path.Combine(_directory, "scan-events.db");
            Recorder = new ScanAuditRecorder(
                DatabasePath,
                () => Clock.Now,
                startWorker: false);
        }

        internal string DirectoryPath => _directory;
        internal string DatabasePath { get; }
        internal MutableClock Clock { get; } = new();
        internal ScanAuditRecorder Recorder { get; }

        public void Dispose()
        {
            Recorder.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }
    }

    private sealed class MutableClock
    {
        internal DateTimeOffset Now { get; private set; } =
            new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

        internal void Advance(TimeSpan duration) => Now += duration;
    }
}
