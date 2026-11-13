using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using System.Globalization;
using System.IO;
using System.Text;

namespace ExpressPackingMonitoring.Services;

internal sealed class VisionObbTriggerEventLog
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private static readonly object FileGate = new();
    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;
    private string _pendingStartEventId = "";
    private string _activeRecordingEventId = "";
    private string _lastStopTriggerEventId = "";
    private long _activeRecordId;
    private DateTime _activeRecordingStartedAt = DateTime.MinValue;
    private DateTime _activeStopRequestedAt = DateTime.MinValue;
    private string _activeStopOrigin = "";
    private double? _pendingObbDiagonalPixels;
    private double? _pendingObbDiagonalPercent;
    private double? _activeObbDiagonalPixels;
    private double? _activeObbDiagonalPercent;
    private double _activePreRecordSeconds;

    internal VisionObbTriggerEventLog(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.Now);
    }

    internal string? RecordRecognition(
        bool decodedByVisionObb,
        string action,
        string reason,
        bool wasRecording,
        double? obbDiagonalPixels,
        double? obbDiagonalPercent)
    {
        if (!decodedByVisionObb)
            return null;

        lock (_gate)
        {
            string triggerEventId = Guid.NewGuid().ToString("N");
            if (IsStartAction(action) && IsExecutableStartReason(reason))
            {
                _pendingStartEventId = triggerEventId;
                _pendingObbDiagonalPixels = obbDiagonalPixels;
                _pendingObbDiagonalPercent = obbDiagonalPercent;
                Write(
                    "recognition_start",
                    triggerEventId,
                    triggerEventId,
                    0,
                    action,
                    reason,
                    "",
                    DateTime.MinValue,
                    DateTime.MinValue,
                    0,
                    wasRecording,
                    obbDiagonalPixels,
                    obbDiagonalPercent);
            }
            else if (IsStopAction(action) && IsExecutableStopReason(reason))
            {
                _lastStopTriggerEventId = triggerEventId;
                Write(
                    "recognition_stop",
                    triggerEventId,
                    _activeRecordingEventId,
                    _activeRecordId,
                    action,
                    reason,
                    "",
                    _activeRecordingStartedAt,
                    DateTime.MinValue,
                    0,
                    wasRecording,
                    _activeObbDiagonalPixels,
                    _activeObbDiagonalPercent);
            }
            else
            {
                Write(
                    "recognition_ignored",
                    triggerEventId,
                    _activeRecordingEventId,
                    _activeRecordId,
                    action,
                    reason,
                    "",
                    _activeRecordingStartedAt,
                    DateTime.MinValue,
                    0,
                    wasRecording,
                    _activeObbDiagonalPixels,
                    _activeObbDiagonalPercent);
            }
            return triggerEventId;
        }
    }

    internal void RecordRecordingStarted(
        long recordId,
        double preRecordSeconds)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_pendingStartEventId))
                return;

            _activeRecordingEventId = _pendingStartEventId;
            _pendingStartEventId = "";
            _activeRecordId = recordId;
            _activeRecordingStartedAt = _clock();
            _activeStopRequestedAt = DateTime.MinValue;
            _activeStopOrigin = "";
            _lastStopTriggerEventId = "";
            _activePreRecordSeconds = Math.Max(0, preRecordSeconds);
            _activeObbDiagonalPixels = _pendingObbDiagonalPixels;
            _activeObbDiagonalPercent = _pendingObbDiagonalPercent;
            _pendingObbDiagonalPixels = null;
            _pendingObbDiagonalPercent = null;
            Write(
                "recording_started",
                _activeRecordingEventId,
                _activeRecordingEventId,
                recordId,
                "Start",
                "",
                "",
                _activeRecordingStartedAt,
                DateTime.MinValue,
                preRecordSeconds,
                wasRecording: false,
                obbDiagonalPixels: _activeObbDiagonalPixels,
                obbDiagonalPercent: _activeObbDiagonalPercent);
        }
    }

    internal void RecordPendingStartFailed(string reason)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_pendingStartEventId))
                return;

            Write(
                "recording_start_failed",
                _pendingStartEventId,
                _pendingStartEventId,
                0,
                "Start",
                reason,
                "",
                DateTime.MinValue,
                DateTime.MinValue,
                0,
                wasRecording: false,
                obbDiagonalPixels: _pendingObbDiagonalPixels,
                obbDiagonalPercent: _pendingObbDiagonalPercent);
            _pendingStartEventId = "";
            _pendingObbDiagonalPixels = null;
            _pendingObbDiagonalPercent = null;
        }
    }

    internal void RecordStopRequested(string origin)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_activeRecordingEventId)
                || _activeStopRequestedAt != DateTime.MinValue)
            {
                return;
            }

            _activeStopRequestedAt = _clock();
            _activeStopOrigin = string.IsNullOrWhiteSpace(origin) ? "unknown" : origin;
            Write(
                "stop_requested",
                string.IsNullOrWhiteSpace(_lastStopTriggerEventId)
                    ? _activeRecordingEventId
                    : _lastStopTriggerEventId,
                _activeRecordingEventId,
                _activeRecordId,
                "Stop",
                "",
                _activeStopOrigin,
                _activeRecordingStartedAt,
                _activeStopRequestedAt,
                _activePreRecordSeconds,
                wasRecording: true,
                obbDiagonalPixels: _activeObbDiagonalPixels,
                obbDiagonalPercent: _activeObbDiagonalPercent);
        }
    }

    internal void RecordRecordingFinished(
        long recordId,
        DateTime fileStartedAt,
        DateTime stoppedAt,
        string finalReason,
        double minRecordingSeconds)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_activeRecordingEventId))
                return;
            if (recordId > 0 && _activeRecordId > 0 && recordId != _activeRecordId)
                return;

            DateTime liveStartedAt = _activeRecordingStartedAt == DateTime.MinValue
                ? fileStartedAt
                : _activeRecordingStartedAt;
            DateTime stopRequestedAt = _activeStopRequestedAt == DateTime.MinValue
                ? stoppedAt
                : _activeStopRequestedAt;
            string stopOrigin = string.IsNullOrWhiteSpace(_activeStopOrigin)
                ? finalReason
                : _activeStopOrigin;
            double liveDurationSeconds = Math.Max(
                0,
                (stoppedAt - liveStartedAt).TotalSeconds);
            double fileDurationSeconds = fileStartedAt == DateTime.MinValue
                ? liveDurationSeconds
                : Math.Max(0, (stoppedAt - fileStartedAt).TotalSeconds);
            bool manualStop = string.Equals(
                stopOrigin,
                VisionObbStopOrigin.ManualButton,
                StringComparison.Ordinal);
            bool shortRecording =
                minRecordingSeconds > 0
                && liveDurationSeconds < minRecordingSeconds;
            Write(
                "recording_finished",
                string.IsNullOrWhiteSpace(_lastStopTriggerEventId)
                    ? _activeRecordingEventId
                    : _lastStopTriggerEventId,
                _activeRecordingEventId,
                recordId,
                "Stop",
                finalReason,
                stopOrigin,
                liveStartedAt,
                stopRequestedAt,
                _activePreRecordSeconds,
                wasRecording: true,
                stoppedAt: stoppedAt,
                liveDurationSeconds: liveDurationSeconds,
                fileDurationSeconds: fileDurationSeconds,
                minRecordingSeconds: minRecordingSeconds,
                manualStop: manualStop,
                shortRecording: shortRecording,
                obbDiagonalPixels: _activeObbDiagonalPixels,
                obbDiagonalPercent: _activeObbDiagonalPercent);

            _activeRecordingEventId = "";
            _activeRecordId = 0;
            _activeRecordingStartedAt = DateTime.MinValue;
            _activeStopRequestedAt = DateTime.MinValue;
            _activeStopOrigin = "";
            _lastStopTriggerEventId = "";
            _activeObbDiagonalPixels = null;
            _activeObbDiagonalPercent = null;
            _activePreRecordSeconds = 0;
        }
    }

    private void Write(
        string phase,
        string triggerEventId,
        string recordingEventId,
        long recordId,
        string action,
        string reason,
        string stopOrigin,
        DateTime recordingStartedAt,
        DateTime stopRequestedAt,
        double preRecordSeconds,
        bool wasRecording,
        double? obbDiagonalPixels = null,
        double? obbDiagonalPercent = null,
        DateTime? stoppedAt = null,
        double liveDurationSeconds = 0,
        double fileDurationSeconds = 0,
        double minRecordingSeconds = 0,
        bool manualStop = false,
        bool shortRecording = false)
    {
        lock (FileGate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                RotateIfNeeded();
                bool writeHeader = !File.Exists(AppPaths.VisionObbTriggerDiagnosticLogPath)
                    || new FileInfo(AppPaths.VisionObbTriggerDiagnosticLogPath).Length == 0;
                using var stream = new FileStream(
                    AppPaths.VisionObbTriggerDiagnosticLogPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                if (writeHeader)
                {
                    writer.WriteLine(
                        "timestamp\tsession\tphase\ttrigger_event_id\trecording_event_id\t"
                        + "record_id\taction\treason\tstop_origin\trecording_started_at\t"
                        + "stop_requested_at\trecording_stopped_at\t"
                        + "obb_diagonal_px\tobb_diagonal_percent\tpre_record_seconds\t"
                        + "live_duration_ms\tfile_duration_ms\tmin_recording_ms\t"
                        + "was_recording\tmanual_stop\tshort_recording");
                }

                writer.Write(_clock().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                writer.Write('\t');
                writer.Write(RuntimeLog.CurrentSessionId);
                writer.Write('\t');
                writer.Write(phase);
                writer.Write('\t');
                writer.Write(triggerEventId);
                writer.Write('\t');
                writer.Write(recordingEventId);
                writer.Write('\t');
                WriteNumber(writer, recordId);
                writer.Write('\t');
                writer.Write(action);
                writer.Write('\t');
                writer.Write(reason);
                writer.Write('\t');
                writer.Write(stopOrigin);
                writer.Write('\t');
                WriteDateTime(writer, recordingStartedAt);
                writer.Write('\t');
                WriteDateTime(writer, stopRequestedAt);
                writer.Write('\t');
                WriteDateTime(writer, stoppedAt ?? DateTime.MinValue);
                writer.Write('\t');
                WriteOptionalNumber(writer, obbDiagonalPixels, "F1");
                writer.Write('\t');
                WriteOptionalNumber(writer, obbDiagonalPercent, "F2");
                writer.Write('\t');
                WriteNumber(writer, preRecordSeconds, "F3");
                writer.Write('\t');
                WriteNumber(writer, liveDurationSeconds * 1000, "F0");
                writer.Write('\t');
                WriteNumber(writer, fileDurationSeconds * 1000, "F0");
                writer.Write('\t');
                WriteNumber(writer, minRecordingSeconds * 1000, "F0");
                writer.Write('\t');
                writer.Write(wasRecording ? '1' : '0');
                writer.Write('\t');
                writer.Write(manualStop ? '1' : '0');
                writer.Write('\t');
                writer.Write(shortRecording ? '1' : '0');
                writer.WriteLine();
            }
            catch
            {
                // Diagnostics must never affect recording.
            }
        }
    }

    private static bool IsStartAction(string action) =>
        action is "Start" or "Switch" or "StartRecording";

    private static bool IsStopAction(string action) =>
        string.Equals(action, "Stop", StringComparison.Ordinal);

    private static bool IsExecutableStartReason(string reason) =>
        reason is "Ready" or "StartCommand";

    private static bool IsExecutableStopReason(string reason) =>
        reason is "SameCodeMatched" or "StopCommand";

    private static void WriteNumber(TextWriter writer, double value, string format = "F0") =>
        writer.Write(value.ToString(format, CultureInfo.InvariantCulture));

    private static void WriteOptionalNumber(
        TextWriter writer,
        double? value,
        string format) =>
        writer.Write(value.HasValue
            ? value.Value.ToString(format, CultureInfo.InvariantCulture)
            : "");

    private static void WriteDateTime(TextWriter writer, DateTime value) =>
        writer.Write(value == DateTime.MinValue
            ? ""
            : value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));

    private static void RotateIfNeeded()
    {
        string path = AppPaths.VisionObbTriggerDiagnosticLogPath;
        if (!File.Exists(path) || new FileInfo(path).Length <= MaxLogBytes)
            return;

        string oldPath = Path.Combine(
            AppPaths.LogDir,
            "vision_obb_trigger_events.old.log");
        if (File.Exists(oldPath))
            File.Delete(oldPath);
        File.Move(path, oldPath);
    }
}

internal static class VisionObbStopOrigin
{
    internal const string ManualButton = "manual_button";
    internal const string SameCode = "vision_obb_same_code";
    internal const string StopCommand = "vision_obb_stop_command";
    internal const string Switch = "vision_obb_switch";
}
