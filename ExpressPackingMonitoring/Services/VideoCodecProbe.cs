using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

internal static partial class VideoCodecProbe
{
    internal static async Task<string> ProbeAsync(string filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return "";

        string ffmpegPath = AppPaths.FindFFmpeg();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            return "";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(filePath);

        try
        {
            if (!process.Start())
                return "";
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string output = await errorTask.ConfigureAwait(false) + await outputTask.ConfigureAwait(false);
            return Parse(output);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("VideoCodecProbe", $"Codec probe failed: {ex.Message}");
            return "";
        }
    }

    internal static string Parse(string output)
    {
        Match match = VideoCodecRegex().Match(output ?? "");
        return match.Success
            ? MobileBackupService.NormalizeVideoCodec(match.Groups[1].Value)
            : "";
    }

    [GeneratedRegex(@"Video:\s*(h264|avc|hevc|h265|av1)\b", RegexOptions.IgnoreCase)]
    private static partial Regex VideoCodecRegex();
}
