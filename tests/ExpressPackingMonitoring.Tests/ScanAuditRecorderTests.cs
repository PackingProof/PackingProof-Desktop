using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using System.Globalization;
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
            stopOrigin: "同码停录",
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
    public void RecordingStopPassesStableOriginCode()
    {
        string source = RepositorySource.ReadMainViewModelParts("Recording")
            .ReplaceLineEndings("\n");

        Assert.Contains(
            "_scanAudit?.RecordStopRequested(_stopOrigin);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RecordStopRequested(_stopReason)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleNonTerminalEventIsMarkedAbandoned()
    {
        using var fixture = new Fixture();
        string eventId = fixture.Recorder.Begin(
            ScanInputSource.CameraZxing,
            "JD005",
            "JD005",
            wasRecording: false,
            priorRecordId: 0);
        fixture.Recorder.RecordDecision(
            eventId,
            "Ignore",
            "CannotProcess",
            "blocked",
            priorRecordId: 0);
        await fixture.Recorder.FlushForTestsAsync(TestContext.Current.CancellationToken);

        fixture.Clock.Advance(
            ScanAuditRecorder.NonTerminalEventRetention + TimeSpan.FromMinutes(1));
        await fixture.Recorder.FlushForTestsAsync(TestContext.Current.CancellationToken);

        using var store = new ScanEventStore(fixture.DatabasePath);
        ScanEventRecord row = Assert.Single(store.Query(normalizedOrder: "JD005"));
        Assert.Equal(ScanAuditRecorder.AbandonedOutcome, row.Outcome);
    }

    [Fact]
    public async Task TerminalUpdateDuringFlushIsNotLost()
    {
        using var fixture = new Fixture();
        string eventId = fixture.Recorder.Begin(
            ScanInputSource.CameraZxing,
            "JD006",
            "JD006",
            wasRecording: false,
            priorRecordId: 0);
        fixture.Recorder.RecordDecision(
            eventId,
            "Start",
            "Ready",
            "start_requested",
            priorRecordId: 0);

        using var persistEntered = new ManualResetEventSlim();
        using var releasePersist = new ManualResetEventSlim();
        fixture.Recorder.BeforePersistForTests = () =>
        {
            persistEntered.Set();
            releasePersist.Wait(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
        };

        Task flush = fixture.Recorder.FlushForTestsAsync(TestContext.Current.CancellationToken);
        Assert.True(persistEntered.Wait(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));
        fixture.Recorder.RecordDecision(
            eventId,
            "Stop",
            "SameCodeMatched",
            "completed",
            priorRecordId: 0);
        fixture.Clock.Advance(TimeSpan.FromSeconds(11));
        releasePersist.Set();
        await flush;
        fixture.Recorder.BeforePersistForTests = null;
        await fixture.Recorder.FlushForTestsAsync(TestContext.Current.CancellationToken);

        using var store = new ScanEventStore(fixture.DatabasePath);
        ScanEventRecord row = Assert.Single(store.Query(normalizedOrder: "JD006"));
        Assert.Equal("completed", row.Outcome);
    }

    [Fact]
    public void StorePrunesEventsOlderThanRetention()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "PackingProof-ScanStore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "scan-events.db");
        try
        {
            DateTimeOffset old = DateTimeOffset.UtcNow
                .AddDays(-ScanEventStore.RetentionDays - 1);
            var record = new ScanEventRecord
            {
                EventId = Guid.NewGuid().ToString("N"),
                OccurredAtUtc = old.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                LastOccurredAtUtc = old.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                UpdatedAtUtc = old.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                Source = "test",
                NormalizedOrder = "JDOLD",
                Outcome = "completed"
            };

            using (var store = new ScanEventStore(databasePath))
                store.UpsertBatch([record]);

            using var reopened = new ScanEventStore(databasePath);
            Assert.Empty(reopened.Query());
            Assert.Equal(
                0,
                reopened.PruneOlderThan(
                    DateTimeOffset.UtcNow.AddDays(-ScanEventStore.RetentionDays)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
