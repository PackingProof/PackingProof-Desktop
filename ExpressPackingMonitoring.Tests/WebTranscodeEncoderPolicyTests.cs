using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 网页转码编码器选择：曾经写死 h264_nvenc，没 NVIDIA 的机器（AMD/Intel 显卡、macOS）
/// 每次播放都要先失败一次再回退 CPU。这里守住"按平台与可用性选择 + CPU 兜底"。
/// </summary>
public sealed class WebTranscodeEncoderPolicyTests
{
    [Fact]
    public void HardwareCandidates_FollowCurrentPlatform()
    {
        IReadOnlyList<string> candidates = WebTranscodeEncoderPolicy.HardwareCandidates();

        Assert.NotEmpty(candidates);
        Assert.Equal(candidates.Distinct(StringComparer.Ordinal), candidates);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(new[] { "h264_nvenc", "h264_qsv", "h264_amf" }, candidates);
        }
        else
        {
            Assert.DoesNotContain("h264_nvenc", candidates);
            Assert.Contains("h264_videotoolbox", candidates);
        }
    }

    [Fact]
    public void SelectHardwareEncoder_PrefersEarlierCandidate()
    {
        const string output = " V....D h264_qsv              H.264 (Intel Quick Sync Video)\n"
            + " V....D h264_nvenc            NVIDIA NVENC H.264 encoder\n";

        Assert.Equal(
            "h264_nvenc",
            WebTranscodeEncoderPolicy.SelectHardwareEncoder(output, ["h264_nvenc", "h264_qsv"]));
        Assert.Equal(
            "h264_qsv",
            WebTranscodeEncoderPolicy.SelectHardwareEncoder(output, ["h264_videotoolbox", "h264_qsv"]));
    }

    [Fact]
    public void SelectHardwareEncoder_ReturnsEmptyWhenNothingAvailable()
    {
        Assert.Equal("", WebTranscodeEncoderPolicy.SelectHardwareEncoder("", ["h264_nvenc"]));
        Assert.Equal("", WebTranscodeEncoderPolicy.SelectHardwareEncoder(null, ["h264_nvenc"]));
        Assert.Equal(
            "",
            WebTranscodeEncoderPolicy.SelectHardwareEncoder(" V....D libx264  H.264", ["h264_nvenc"]));
        Assert.Equal("", WebTranscodeEncoderPolicy.SelectHardwareEncoder(" V....D h264_nvenc", null));
    }

    [Fact]
    public void BuildHardwareVideoArguments_MatchEncoderFamily()
    {
        string nvenc = WebTranscodeEncoderPolicy.BuildHardwareVideoArguments("h264_nvenc");
        Assert.Contains("-c:v h264_nvenc", nvenc);
        Assert.Contains("-preset p1 -cq 30", nvenc);

        // macOS 没有 NVENC，硬件编码只能走 VideoToolbox，参数也必须换成它认的那套
        string videotoolbox = WebTranscodeEncoderPolicy.BuildHardwareVideoArguments("h264_videotoolbox");
        Assert.Contains("-c:v h264_videotoolbox", videotoolbox);
        Assert.Contains("-q:v 45", videotoolbox);
        Assert.DoesNotContain("h264_nvenc", videotoolbox);
        Assert.DoesNotContain("-cq 30", videotoolbox);

        Assert.Equal("", WebTranscodeEncoderPolicy.BuildHardwareVideoArguments("h264_unknown"));
        Assert.Equal("", WebTranscodeEncoderPolicy.BuildHardwareVideoArguments(null));
    }

    [Fact]
    public void BuildCpuArguments_FallBackToLibx264AndStreamToPipe()
    {
        string args = WebTranscodeEncoderPolicy.BuildCpuArguments(
            @"C:\recordings\有 空格\录像.mp4",
            "-vf scale=-2:480");

        Assert.Contains("\"C:\\recordings\\有 空格\\录像.mp4\"", args);
        Assert.Contains("-vf scale=-2:480", args);
        Assert.Contains("-c:v libx264", args);
        Assert.Contains("-preset ultrafast", args);
        Assert.Contains("-crf 28", args);
        Assert.Contains("-f mp4 pipe:1", args);
        Assert.DoesNotContain("nvenc", args);
    }

    [Fact]
    public void BuildHardwareArguments_ReturnsEmptyWhenFfmpegMissing()
    {
        string missing = Path.Combine(Path.GetTempPath(), "no-such-ffmpeg-for-test");

        Assert.Equal("", WebTranscodeEncoderPolicy.BuildHardwareArguments(missing, "video.mp4", "-vf scale=-2:480"));
        Assert.Equal("", WebTranscodeEncoderPolicy.BuildHardwareArguments("", "video.mp4", "-vf scale=-2:480"));
        Assert.Equal("", WebTranscodeEncoderPolicy.ResolveHardwareEncoder(null));
    }

    [Fact]
    public void WebServerTranscode_DelegatesEncoderChoiceInsteadOfHardcodingNvenc()
    {
        string source = File.ReadAllText(FindProjectFile(@"Services\WebServer.cs"));

        Assert.Contains("WebTranscodeEncoderPolicy.BuildHardwareArguments", source);
        Assert.Contains("WebTranscodeEncoderPolicy.BuildCpuArguments", source);
        Assert.DoesNotContain("-c:v h264_nvenc", source);
        Assert.DoesNotContain("-c:v libx264", source);
    }

    /// <summary>
    /// 随包 FFmpeg 必须认识生成的每个选项：不同主版本的编码器参数差别很大，
    /// 4.4.1 上不存在的选项会让转码直接失败。
    /// </summary>
    [Fact]
    public void TranscodeArguments_EveryOptionIsRecognizedByPinnedFfmpeg()
    {
        string ffmpegPath = AppPaths.FindFFmpeg();
        Assert.True(File.Exists(ffmpegPath), "ffmpeg.exe 不存在于测试输出目录");

        string fullHelp = RunFfmpegFullHelp(ffmpegPath);
        var argumentSets = new List<string>
        {
            WebTranscodeEncoderPolicy.BuildCpuArguments("video.mp4", "-vf scale=-2:480")
        };

        // 只校验这台机器上真实存在的硬件编码器；Windows 上没有 VideoToolbox。
        string hardware = WebTranscodeEncoderPolicy.ResolveHardwareEncoder(ffmpegPath);
        if (hardware.Length > 0)
        {
            string args = WebTranscodeEncoderPolicy.BuildHardwareArguments(
                ffmpegPath,
                "video.mp4",
                "-vf scale=-2:480");
            Assert.Contains($"-c:v {hardware}", args);
            argumentSets.Add(args);
        }

        foreach (string args in argumentSets)
        {
            foreach (string option in ExtractOptionNames(args))
            {
                Assert.True(
                    IsRecognizedByFullHelp(fullHelp, option),
                    $"随包 FFmpeg 不识别选项 -{option}：{args}");
            }
        }
    }

    private static string RunFfmpegFullHelp(string ffmpegPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = "-hide_banner -h full",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 ffmpeg -h full");
        string output = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30000), "ffmpeg -h full 超时");
        return output;
    }

    private static IEnumerable<string> ExtractOptionNames(string args) =>
        Regex.Matches(args, "(^|\\s)-([A-Za-z_]+)(?=\\s|<|\")")
            .Select(match => match.Groups[2].Value)
            .Distinct(StringComparer.Ordinal);

    private static bool IsRecognizedByFullHelp(string fullHelp, string option) =>
        Regex.IsMatch(fullHelp, "(^|\\s)-" + Regex.Escape(option) + "(\\s|<|\\[)");

    private static string FindProjectFile(string relativePath)
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(startPath));
            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "ExpressPackingMonitoring", relativePath);
                if (File.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"找不到源码文件：{relativePath}");
    }
}
