using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Vision;
using System.Globalization;
using System.IO;
using System.Text;

namespace ExpressPackingMonitoring.Services;

internal readonly record struct VisionObbTimingDiagnostic(
    string Stage,
    int FrameWidth,
    int FrameHeight,
    double DetectionMs,
    int DetectionCount,
    VisionObbDetectorTiming DetectorTiming,
    int CandidateIndex,
    double CandidateWidth,
    double CandidateHeight,
    double CandidateAngleDegrees,
    double CandidateDiagonalPercent,
    string PatchMode,
    double PatchMs,
    double DecodeMs,
    int ZxingOrientation,
    string ZxingMode,
    int ZxingAttempts,
    bool Decoded,
    double TotalDecodeMs);

internal static class VisionObbTimingDiagnosticLog
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();

    internal static void Append(IReadOnlyList<VisionObbTimingDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
            return;

        AppendCore(writer =>
        {
            foreach (VisionObbTimingDiagnostic diagnostic in diagnostics)
                WriteDiagnostic(writer, diagnostic);
        });
    }

    internal static void AppendZxingSuccess(
        int imageWidth,
        int imageHeight,
        int orientation,
        bool slowReader,
        int attempts)
    {
        AppendCore(writer =>
        {
            WriteDiagnostic(
                writer,
                new VisionObbTimingDiagnostic(
                    Stage: "zxing_success",
                    FrameWidth: imageWidth,
                    FrameHeight: imageHeight,
                    DetectionMs: 0,
                    DetectionCount: 0,
                    DetectorTiming: default,
                    CandidateIndex: -1,
                    CandidateWidth: imageWidth,
                    CandidateHeight: imageHeight,
                    CandidateAngleDegrees: 0,
                    CandidateDiagonalPercent: 0,
                    PatchMode: "",
                    PatchMs: 0,
                    DecodeMs: 0,
                    ZxingOrientation: orientation * 90,
                    ZxingMode: slowReader ? "slow" : "fast",
                    ZxingAttempts: attempts,
                    Decoded: true,
                    TotalDecodeMs: 0));
        });
    }

    private static void AppendCore(Action<TextWriter> writeRows)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                RotateIfNeeded();
                bool writeHeader =
                    !File.Exists(AppPaths.VisionObbTimingLogPath)
                    || new FileInfo(AppPaths.VisionObbTimingLogPath).Length == 0;
                using var stream = new FileStream(
                    AppPaths.VisionObbTimingLogPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                if (writeHeader)
                {
                    writer.WriteLine(
                        "timestamp\tsession\tstage\tframe_width\tframe_height\t"
                        + "detection_ms\tpreprocess_ms\tinference_ms\tpostprocess_ms\t"
                        + "detection_count\tcandidate_index\tcandidate_width\tcandidate_height\t"
                        + "angle_deg\tdiagonal_percent\tpatch_mode\tpatch_ms\tdecode_ms\t"
                        + "total_decode_ms\tzxing_orientation_deg\tzxing_mode\t"
                        + "zxing_attempts\tdecoded");
                }

                writeRows(writer);
            }
            catch
            {
                // Diagnostic logging must never affect barcode recognition.
            }
        }
    }

    private static void WriteDiagnostic(
        TextWriter writer,
        in VisionObbTimingDiagnostic diagnostic)
    {
        writer.Write(DateTime.Now.ToString(
            "yyyy-MM-dd HH:mm:ss.fff",
            CultureInfo.InvariantCulture));
        writer.Write('\t');
        writer.Write(RuntimeLog.CurrentSessionId);
        writer.Write('\t');
        writer.Write(diagnostic.Stage);
        writer.Write('\t');
        WriteNumber(writer, diagnostic.FrameWidth);
        writer.Write('\t');
        WriteNumber(writer, diagnostic.FrameHeight);
        writer.Write('\t');
        WriteNumber(writer, diagnostic.DetectionMs, "F1");
        writer.Write('\t');
        WriteNumber(writer, diagnostic.DetectorTiming.PreprocessMs, "F1");
        writer.Write('\t');
        WriteNumber(writer, diagnostic.DetectorTiming.InferenceMs, "F1");
        writer.Write('\t');
        WriteNumber(writer, diagnostic.DetectorTiming.PostprocessMs, "F1");
        writer.Write('\t');
        WriteNumber(writer, diagnostic.DetectionCount);
        writer.Write('\t');
        WriteNumber(writer, diagnostic.CandidateIndex);
        writer.Write('\t');
        WriteNumber(writer, diagnostic.CandidateWidth, "F1");
        writer.Write('\t');
        WriteNumber(writer, diagnostic.CandidateHeight, "F1");
        writer.Write('\t');
        WriteNumber(writer, diagnostic.CandidateAngleDegrees, "F1");
        writer.Write('\t');
        WriteNumber(writer, diagnostic.CandidateDiagonalPercent, "F2");
        writer.Write('\t');
        writer.Write(diagnostic.PatchMode);
        writer.Write('\t');
        WriteNumber(writer, diagnostic.PatchMs, "F1");
        writer.Write('\t');
        WriteNumber(writer, diagnostic.DecodeMs, "F1");
        writer.Write('\t');
        WriteNumber(writer, diagnostic.TotalDecodeMs, "F1");
        writer.Write('\t');
        WriteNumber(writer, diagnostic.ZxingOrientation);
        writer.Write('\t');
        writer.Write(diagnostic.ZxingMode);
        writer.Write('\t');
        WriteNumber(writer, diagnostic.ZxingAttempts);
        writer.Write('\t');
        writer.Write(diagnostic.Decoded ? '1' : '0');
        writer.WriteLine();
    }

    private static void WriteNumber(
        TextWriter writer,
        double value,
        string format = "F0") =>
        writer.Write(value.ToString(format, CultureInfo.InvariantCulture));

    private static void RotateIfNeeded()
    {
        string path = AppPaths.VisionObbTimingLogPath;
        if (!File.Exists(path) || new FileInfo(path).Length <= MaxLogBytes)
            return;

        string oldPath = Path.Combine(
            AppPaths.LogDir,
            "vision_obb_timing.old.log");
        if (File.Exists(oldPath))
            File.Delete(oldPath);
        File.Move(path, oldPath);
    }
}
