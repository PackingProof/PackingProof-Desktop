using System.Collections.Concurrent;
using System.Diagnostics;
using ExpressPackingMonitoring.Logging;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 网页转码用的 H.264 编码器选择。
///
/// 以前这里写死 h264_nvenc：只有 NVIDIA 显卡的机器才可能命中，AMD/Intel 显卡和 macOS
/// 每次播放都要先失败一次再回退 CPU，Mac 上更是永远拿不到硬件编码。现在按平台给出候选，
/// 用 ffmpeg -encoders 探测一次并缓存；探不到硬件编码器就直接走 CPU，不做无谓的尝试。
/// </summary>
internal static class WebTranscodeEncoderPolicy
{
    internal const string CpuEncoder = "libx264";

    /// <summary>除编码器本身以外的输出参数：音频、碎片化 MP4 与推流目标。</summary>
    internal const string CommonOutputArguments =
        "-c:a aac -b:a 96k -movflags frag_keyframe+empty_moov+default_base_moof -f mp4 pipe:1";

    private static readonly ConcurrentDictionary<string, string> HardwareEncoderCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>平台相关的硬件编码器候选，顺序即优先级。</summary>
    internal static IReadOnlyList<string> HardwareCandidates() =>
        OperatingSystem.IsWindows()
            ? ["h264_nvenc", "h264_qsv", "h264_amf"]
            : ["h264_videotoolbox"];

    /// <summary>从 <c>ffmpeg -encoders</c> 输出里挑第一个可用候选；纯函数，便于测试。</summary>
    internal static string SelectHardwareEncoder(string? encodersOutput, IReadOnlyList<string>? candidates)
    {
        if (string.IsNullOrEmpty(encodersOutput) || candidates == null)
            return "";

        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate)
                && encodersOutput.Contains(candidate, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return "";
    }

    /// <summary>硬件编码器的编码参数；未知编码器返回空串。</summary>
    internal static string BuildHardwareVideoArguments(string? encoder) => encoder switch
    {
        "h264_nvenc" => "-c:v h264_nvenc -preset p1 -cq 30",
        "h264_qsv" => "-c:v h264_qsv -preset veryfast -global_quality 30",
        "h264_amf" => "-c:v h264_amf -quality speed -rc cqp -qp_i 30 -qp_p 30",
        // macOS 没有 NVENC，硬件编码走 VideoToolbox；-q:v 45 的体积与 CPU 侧 crf 28 相当
        "h264_videotoolbox" => "-c:v h264_videotoolbox -q:v 45 -realtime true -allow_sw true",
        _ => ""
    };

    internal static string BuildCpuArguments(string filePath, string scaleFilter) =>
        $"-loglevel warning -i {Quote(filePath)} {scaleFilter} "
        + $"-c:v {CpuEncoder} -preset ultrafast -tune zerolatency -crf 28 {CommonOutputArguments}";

    /// <summary>硬件转码参数；机器上没有可用硬件编码器时返回空串，调用方直接走 CPU。</summary>
    internal static string BuildHardwareArguments(string? ffmpegPath, string filePath, string scaleFilter)
    {
        string encoder = ResolveHardwareEncoder(ffmpegPath);
        if (encoder.Length == 0)
            return "";

        return $"-loglevel warning -hwaccel auto -i {Quote(filePath)} {scaleFilter} "
            + $"{BuildHardwareVideoArguments(encoder)} {CommonOutputArguments}";
    }

    /// <summary>探测结果按 ffmpeg 路径缓存：转码是热路径，不能每次播放都拉起一个进程。</summary>
    internal static string ResolveHardwareEncoder(string? ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            return "";

        return HardwareEncoderCache.GetOrAdd(
            ffmpegPath,
            path => SelectHardwareEncoder(QueryEncoders(path), HardwareCandidates()));
    }

    private static string QueryEncoders(string ffmpegPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -encoders",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using Process? process = Process.Start(startInfo);
            if (process == null)
                return "";

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                RuntimeLog.Warn("WebTranscode", "ffmpeg -encoders 超时，网页转码改用 CPU 编码");
                return "";
            }

            return outputTask.Wait(TimeSpan.FromSeconds(2)) ? outputTask.Result ?? "" : "";
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("WebTranscode", $"ffmpeg -encoders 失败，网页转码改用 CPU 编码：{ex.Message}");
            return "";
        }
    }

    private static string Quote(string path) => $"\"{path}\"";
}
