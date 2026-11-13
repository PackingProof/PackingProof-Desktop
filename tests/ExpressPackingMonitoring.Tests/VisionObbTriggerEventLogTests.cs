using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VisionObbTriggerEventLogTests
{
    [Fact]
    public void RecognitionStartManualStopAndShortDurationShareOneEventId()
    {
        var clock = new MutableClock(new DateTime(2026, 11, 13, 12, 0, 0));
        var log = new VisionObbTriggerEventLog(() => clock.Now);

        string? triggerEventId = log.RecordRecognition(
            decodedByVisionObb: true,
            action: "Start",
            reason: "Ready",
            wasRecording: false,
            obbDiagonalPixels: 350.5,
            obbDiagonalPercent: 15.91);
        Assert.NotNull(triggerEventId);

        clock.Advance(TimeSpan.FromSeconds(1));
        DateTime recordingStartedAt = clock.Now;
        log.RecordRecordingStarted(recordId: 42, preRecordSeconds: 0.5);

        clock.Advance(TimeSpan.FromSeconds(2));
        log.RecordStopRequested(VisionObbStopOrigin.ManualButton);

        clock.Advance(TimeSpan.FromMilliseconds(200));
        DateTime stoppedAt = clock.Now;
        log.RecordRecordingFinished(
            recordId: 42,
            fileStartedAt: recordingStartedAt.AddSeconds(-1),
            stoppedAt: stoppedAt,
            finalReason: "手动",
            minRecordingSeconds: 3);

        string[] rows = File.ReadAllLines(AppPaths.VisionObbTriggerDiagnosticLogPath)
            .Where(line => line.Contains(triggerEventId!, StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(4, rows.Length);
        Assert.Equal(
            ["recognition_start", "recording_started", "stop_requested", "recording_finished"],
            rows.Select(row => row.Split('\t')[2]).ToArray());

        string[] finished = rows[^1].Split('\t');
        Assert.Equal("42", finished[5]);
        Assert.Equal(VisionObbStopOrigin.ManualButton, finished[8]);
        Assert.Equal("350.5", finished[12]);
        Assert.Equal("15.91", finished[13]);
        Assert.Equal("2200", finished[15]);
        Assert.Equal("3000", finished[17]);
        Assert.Equal("1", finished[19]);
        Assert.Equal("1", finished[20]);
    }

    [Fact]
    public void NonObbRecognitionDoesNotCreateARecordingEvent()
    {
        var log = new VisionObbTriggerEventLog(
            () => new DateTime(2026, 11, 13, 12, 0, 0));

        Assert.Null(log.RecordRecognition(
            decodedByVisionObb: false,
            action: "Start",
            reason: "Ready",
            wasRecording: false,
            obbDiagonalPixels: null,
            obbDiagonalPercent: null));
    }

    private sealed class MutableClock(DateTime initial)
    {
        internal DateTime Now { get; private set; } = initial;

        internal void Advance(TimeSpan duration) => Now += duration;
    }
}
