using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using System.Globalization;
using System.IO;
using System.Text;

namespace ExpressPackingMonitoring.Services;

internal readonly record struct VisionObbCandidateDiagnostic(
    int FrameWidth,
    int FrameHeight,
    double FrameDiagonalPx,
    double CenterX,
    double CenterY,
    double Width,
    double Height,
    double AngleDegrees,
    double DiagonalPx,
    double DiagonalPercent,
    float Score,
    double ThresholdPercent,
    bool DecodeAllowed,
    bool Decoded);

internal static class VisionObbDiagnosticLog
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();

    internal static void Append(
        IReadOnlyList<VisionObbCandidateDiagnostic> candidates)
    {
        if (candidates.Count == 0)
            return;

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                RotateIfNeeded();
                bool writeHeader = !File.Exists(AppPaths.VisionObbDiagnosticLogPath)
                    || new FileInfo(AppPaths.VisionObbDiagnosticLogPath).Length == 0;
                using var stream = new FileStream(
                    AppPaths.VisionObbDiagnosticLogPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                if (writeHeader)
                {
                    writer.WriteLine(
                        "timestamp\tsession\tframe_width\tframe_height\tframe_diagonal_px\t"
                        + "center_x\tcenter_y\twidth\theight\tangle_deg\tdiagonal_px\t"
                        + "diagonal_percent\tscore\tthreshold_percent\tdecode_allowed\tdecoded");
                }

                string session = RuntimeLog.CurrentSessionId;
                string timestamp = DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture);
                foreach (VisionObbCandidateDiagnostic candidate in candidates)
                {
                    writer.Write(timestamp);
                    writer.Write('\t');
                    writer.Write(session);
                    writer.Write('\t');
                    WriteNumber(writer, candidate.FrameWidth);
                    writer.Write('\t');
                    WriteNumber(writer, candidate.FrameHeight);
                    writer.Write('\t');
                    WriteNumber(writer, candidate.FrameDiagonalPx, "F1");
                    writer.Write('\t');
                    WriteNumber(writer, candidate.CenterX, "F1");
                    writer.Write('\t');
                    WriteNumber(writer, candidate.CenterY, "F1");
                    writer.Write('\t');
                    WriteNumber(writer, candidate.Width, "F1");
                    writer.Write('\t');
                    WriteNumber(writer, candidate.Height, "F1");
                    writer.Write('\t');
                    WriteNumber(writer, candidate.AngleDegrees, "F1");
                    writer.Write('\t');
                    WriteNumber(writer, candidate.DiagonalPx, "F1");
                    writer.Write('\t');
                    WriteNumber(writer, candidate.DiagonalPercent, "F2");
                    writer.Write('\t');
                    WriteNumber(writer, candidate.Score, "F3");
                    writer.Write('\t');
                    WriteNumber(writer, candidate.ThresholdPercent, "F2");
                    writer.Write('\t');
                    writer.Write(candidate.DecodeAllowed ? '1' : '0');
                    writer.Write('\t');
                    writer.Write(candidate.Decoded ? '1' : '0');
                    writer.WriteLine();
                }
            }
            catch
            {
                // Diagnostic logging must never affect barcode recognition.
            }
        }
    }

    private static void WriteNumber(TextWriter writer, double value, string format = "F0") =>
        writer.Write(value.ToString(format, CultureInfo.InvariantCulture));

    private static void RotateIfNeeded()
    {
        string path = AppPaths.VisionObbDiagnosticLogPath;
        if (!File.Exists(path) || new FileInfo(path).Length <= MaxLogBytes)
            return;

        string oldPath = Path.Combine(
            AppPaths.LogDir,
            "vision_obb_diagnostics.old.log");
        if (File.Exists(oldPath))
            File.Delete(oldPath);
        File.Move(path, oldPath);
    }
}
