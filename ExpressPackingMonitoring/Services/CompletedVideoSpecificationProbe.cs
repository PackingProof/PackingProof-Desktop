using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace ExpressPackingMonitoring.Services;

internal readonly record struct ExpectedRecordingSpecification(
    int Width,
    int Height,
    int Fps,
    double DurationSeconds);

internal readonly record struct CompletedVideoMetadata(
    int Width,
    int Height,
    double DurationSeconds,
    double AverageFrameRate);

internal readonly record struct CompletedVideoSpecificationEvaluation(
    bool ShouldEvaluate,
    bool MeetsSpecification,
    string Reason);

internal static partial class CompletedVideoSpecificationProbe
{
    internal const double MinimumEvaluationDurationSeconds = 10;
    internal const double MinimumDurationRatio = 0.90;
    internal const double MinimumFrameRateRatio = 0.85;
    private const int ProbeTimeoutMs = 5000;

    [GeneratedRegex(@"Duration:\s*(\d{1,2}):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"(?<!\d)(\d{2,5})x(\d{2,5})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex DimensionsRegex();

    [GeneratedRegex(@"(?<![\d.])(\d+(?:\.\d+)?)\s+fps\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FpsRegex();

    internal static bool TryRead(
        string ffmpegPath,
        string filePath,
        out CompletedVideoMetadata metadata)
    {
        metadata = default;
        if (string.IsNullOrWhiteSpace(ffmpegPath)
            || string.IsNullOrWhiteSpace(filePath)
            || !File.Exists(ffmpegPath)
            || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-hide_banner -nostdin -i \"{filePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using Process? process = Process.Start(startInfo);
            if (process == null)
                return false;
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            bool exited = process.WaitForExit(ProbeTimeoutMs);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { process.WaitForExit(1000); } catch { }
                return false;
            }

            string stderr = ReadOutput(stderrTask);
            _ = ReadOutput(stdoutTask);
            return TryParse(stderr, out metadata);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryParse(string probeOutput, out CompletedVideoMetadata metadata)
    {
        metadata = default;
        if (string.IsNullOrWhiteSpace(probeOutput))
            return false;

        Match durationMatch = DurationRegex().Match(probeOutput);
        string? videoLine = probeOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line =>
                line.Contains("Stream #", StringComparison.OrdinalIgnoreCase)
                && line.Contains("Video:", StringComparison.OrdinalIgnoreCase));
        if (!durationMatch.Success || string.IsNullOrWhiteSpace(videoLine))
            return false;

        Match dimensionsMatch = DimensionsRegex().Match(videoLine);
        Match fpsMatch = FpsRegex().Match(videoLine);
        if (!dimensionsMatch.Success || !fpsMatch.Success)
            return false;

        if (!int.TryParse(dimensionsMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(dimensionsMatch.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int height)
            || !int.TryParse(durationMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(durationMatch.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
            || !double.TryParse(durationMatch.Groups[3].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds)
            || !double.TryParse(fpsMatch.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double averageFps))
        {
            return false;
        }

        double durationSeconds = hours * 3600 + minutes * 60 + seconds;
        if (width <= 0 || height <= 0 || durationSeconds <= 0 || averageFps <= 0)
            return false;

        metadata = new CompletedVideoMetadata(width, height, durationSeconds, averageFps);
        return true;
    }

    internal static CompletedVideoSpecificationEvaluation Evaluate(
        ExpectedRecordingSpecification expected,
        CompletedVideoMetadata actual)
    {
        if (expected.DurationSeconds < MinimumEvaluationDurationSeconds
            || actual.DurationSeconds < MinimumEvaluationDurationSeconds)
        {
            return new CompletedVideoSpecificationEvaluation(
                false,
                true,
                "录像过短，不判断规格");
        }

        var reasons = new List<string>();
        if (actual.Width != expected.Width || actual.Height != expected.Height)
        {
            reasons.Add(
                $"分辨率 expected={expected.Width}x{expected.Height}, actual={actual.Width}x{actual.Height}");
        }

        if (actual.DurationSeconds < expected.DurationSeconds * MinimumDurationRatio)
        {
            reasons.Add(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "时长 expected={0:F1}s, actual={1:F1}s",
                    expected.DurationSeconds,
                    actual.DurationSeconds));
        }

        if (actual.AverageFrameRate < expected.Fps * MinimumFrameRateRatio)
        {
            reasons.Add(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "平均帧率 expected={0}, actual={1:F2}",
                    expected.Fps,
                    actual.AverageFrameRate));
        }

        return new CompletedVideoSpecificationEvaluation(
            true,
            reasons.Count == 0,
            string.Join("; ", reasons));
    }

    private static string ReadOutput(Task<string> task)
    {
        try
        {
            return task.Wait(TimeSpan.FromSeconds(2)) ? task.Result ?? "" : "";
        }
        catch
        {
            return "";
        }
    }
}
