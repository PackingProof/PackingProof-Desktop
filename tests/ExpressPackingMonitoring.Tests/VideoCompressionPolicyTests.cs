using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.Data;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VideoCompressionPolicyTests
{
    [Fact]
    public void DefaultTarget_LeavesUsefulSpaceBelowTheOriginal()
    {
        long sourceBytes = 120L * VideoCompressionPolicy.Mebibyte;

        long h264Target = VideoCompressionPolicy.GetDefaultTargetBytes(
            sourceBytes,
            VideoCompressionCodecMode.H264);
        long h265Target = VideoCompressionPolicy.GetDefaultTargetBytes(
            sourceBytes,
            VideoCompressionCodecMode.H265);
        long av1Target = VideoCompressionPolicy.GetDefaultTargetBytes(
            sourceBytes,
            VideoCompressionCodecMode.Av1);

        Assert.Equal(72L * VideoCompressionPolicy.Mebibyte, h264Target);
        Assert.Equal(48L * VideoCompressionPolicy.Mebibyte, h265Target);
        Assert.Equal(38L * VideoCompressionPolicy.Mebibyte, av1Target);
        Assert.True(h265Target < h264Target);
        Assert.True(av1Target < h265Target);
        Assert.True(h264Target < sourceBytes);
        Assert.Equal(40, VideoCompressionPolicy.CalculateReductionPercent(
            sourceBytes,
            h264Target));
        Assert.Equal(60, VideoCompressionPolicy.CalculateReductionPercent(
            sourceBytes,
            h265Target));
        Assert.Equal(68, VideoCompressionPolicy.CalculateReductionPercent(
            sourceBytes,
            av1Target));
    }

    [Fact]
    public void TargetValidation_RejectsLargerOrImpossibleOutputs()
    {
        long sourceBytes = 50L * VideoCompressionPolicy.Mebibyte;

        Assert.True(VideoCompressionPolicy.TryValidateTarget(
            sourceBytes,
            50L * VideoCompressionPolicy.Mebibyte,
            60,
            out VideoCompressionFailureKind equalToSource));
        Assert.Equal(VideoCompressionFailureKind.None, equalToSource);

        Assert.False(VideoCompressionPolicy.TryValidateTarget(
            sourceBytes,
            51L * VideoCompressionPolicy.Mebibyte,
            60,
            out VideoCompressionFailureKind tooLarge));
        Assert.Equal(VideoCompressionFailureKind.TargetTooLarge, tooLarge);

        Assert.False(VideoCompressionPolicy.TryValidateTarget(
            sourceBytes,
            VideoCompressionPolicy.Mebibyte,
            3_600,
            out VideoCompressionFailureKind tooSmall));
        Assert.Equal(VideoCompressionFailureKind.TargetTooSmall, tooSmall);

        Assert.True(VideoCompressionPolicy.TryValidateTarget(
            sourceBytes,
            20L * VideoCompressionPolicy.Mebibyte,
            60,
            out VideoCompressionFailureKind valid));
        Assert.Equal(VideoCompressionFailureKind.None, valid);
    }

    [Theory]
    [InlineData((int)VideoCompressionCodecMode.H264, "H264")]
    [InlineData((int)VideoCompressionCodecMode.H265, "H265")]
    [InlineData((int)VideoCompressionCodecMode.Av1, "AV1")]
    public void DefaultFileName_AppendsSelectedCodec(
        int mode,
        string token)
    {
        string fileName = VideoCompressionPolicy.BuildDefaultFileName(
            "订单 Ａ-001/测试",
            "SF001_20261120_Ship.mkv",
            (VideoCompressionCodecMode)mode,
            new DateTime(2026, 11, 20, 13, 14, 15));

        Assert.Equal(
            $"SF001_20261120_Ship_{token}_compressed.mp4",
            fileName);
        Assert.All(fileName, character => Assert.True(character <= 0x7f));
    }

    [Theory]
    [InlineData("h264", false, (int)VideoCompressionCodecMode.H264)]
    [InlineData("avc", false, (int)VideoCompressionCodecMode.H264)]
    [InlineData("h265", false, (int)VideoCompressionCodecMode.H265)]
    [InlineData("hevc", false, (int)VideoCompressionCodecMode.H265)]
    [InlineData("av1", true, (int)VideoCompressionCodecMode.Av1)]
    [InlineData("av1", false, (int)VideoCompressionCodecMode.H265)]
    [InlineData("unknown", false, (int)VideoCompressionCodecMode.H264)]
    public void DefaultCodecMode_FollowsSourceWhenExplicitModeIsAvailable(
        string sourceCodec,
        bool av1Available,
        int expected)
    {
        Assert.Equal(
            (VideoCompressionCodecMode)expected,
            VideoCompressionPolicy.ResolveDefaultCodecMode(
                sourceCodec,
                av1Available));
    }

    [Fact]
    public void Av1Capability_UsesOnlyValidatedEncoderAndPriority()
    {
        Assert.Equal(
            "",
            VideoCompressionPolicy.ResolveValidatedAv1Encoder([]));
        Assert.Equal(
            "av1_nvenc",
            VideoCompressionPolicy.ResolveValidatedAv1Encoder(
                ["libsvtav1", "av1_nvenc", "av1_qsv"]));
        Assert.Equal(
            "av1_amf",
            VideoCompressionPolicy.ResolveValidatedAv1Encoder(
                ["av1_amf"]));
        Assert.Contains(
            "-c:v av1_nvenc",
            TargetSizeTranscodeEngine.BuildAv1VideoArguments("av1_nvenc"));
        Assert.Contains(
            "tune=0",
            TargetSizeTranscodeEngine.BuildAv1VideoArguments("libsvtav1"));
        Assert.Equal(
            "",
            TargetSizeTranscodeEngine.BuildAv1VideoArguments("av1_unknown"));
    }

    [Fact]
    public void TargetTranscodeEngine_UsesOneAudioTrackAndNonBlockingCancelCallback()
    {
        string engine = ReadProjectFile(
            Path.Combine("Services", "TargetSizeTranscodeEngine.cs"));

        Assert.Contains("-map 0:a:0?", engine);
        Assert.DoesNotContain("-map 0:a? ", engine);
        Assert.Contains(
            "() => _ = Task.Run(() => TryKill(process))",
            engine);
        Assert.Contains("WaitForExitAfterKill(process);", engine);
        int tryKillStart = engine.IndexOf(
            "private static void TryKill(",
            StringComparison.Ordinal);
        int waitHelperStart = engine.IndexOf(
            "private static void WaitForExitAfterKill(",
            StringComparison.Ordinal);
        Assert.True(tryKillStart >= 0);
        Assert.True(waitHelperStart > tryKillStart);
        Assert.DoesNotContain(
            "WaitForExit",
            engine[tryKillStart..waitHelperStart]);
        Assert.True(TargetSizeTranscodeEngine.ContainsEncoder(
            " V....D av1_qsv AV1 encoder",
            "av1_qsv"));
        Assert.False(TargetSizeTranscodeEngine.ContainsEncoder(
            " V....D av1_qsv AV1 encoder",
            "av1_nvenc"));
    }

    [Fact]
    public void TargetTranscodeEngine_RangesOutputToTheSelectedClip()
    {
        string arguments = TargetSizeTranscodeEngine.BuildRangeInputArguments(
            @"D:\recordings\source.mp4",
            25.6,
            12.3);

        Assert.Equal(
            "-i \"D:\\recordings\\source.mp4\" -ss 12.3 -t 25.6",
            arguments);
    }

    [Fact]
    public void ClipSourceRange_UsesActualFileBytesAndNeverRoundsUp()
    {
        Assert.Equal(
            5_000_000,
            VideoClipCompressionRunner.CalculateRangeSourceBytes(
                10_000_000,
                100,
                50));
        Assert.Equal(
            7_777_777,
            VideoClipCompressionRunner.CalculateRangeSourceBytes(
                10_000_000,
                90,
                70));
        Assert.Equal(
            10_000_000,
            VideoClipCompressionRunner.CalculateRangeSourceBytes(
                10_000_000,
                90,
                120));
    }

    [Fact]
    public void ConcurrentClipTasks_UseDistinctOutputFileNames()
    {
        DateTime createdAt = new(2026, 11, 21, 21, 29, 0);
        var options = new ClipOutputOptions(
            ClipOutputMode.Compressed,
            2 * VideoCompressionPolicy.Mebibyte,
            VideoCompressionCodecMode.H264);

        string first = VideoClipFilePolicy.BuildOutputFileName(
            "order_clip",
            "0123456789abcdef0123456789abcdef",
            createdAt,
            options);
        string second = VideoClipFilePolicy.BuildOutputFileName(
            "order_clip",
            "fedcba9876543210fedcba9876543210",
            createdAt,
            options);

        Assert.NotEqual(first, second);
        Assert.Equal(
            "order_clip_clip_20261121_212900_01234567_H264_compressed.mp4",
            first);
    }

    [Fact]
    public void ClipTargetValidation_UsesTheActualRangeBeforeQueueing()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"packingproof-clip-target-{Guid.NewGuid():N}.mp4");
        try
        {
            using (FileStream stream = File.Create(path))
                stream.SetLength(2L * VideoCompressionPolicy.Mebibyte);

            var record = new VideoRecord
            {
                FilePath = path,
                FileSizeBytes = 20L * VideoCompressionPolicy.Mebibyte,
                DurationSeconds = 60
            };
            var options = new ClipOutputOptions(
                ClipOutputMode.Compressed,
                2L * VideoCompressionPolicy.Mebibyte + 1,
                VideoCompressionCodecMode.H264);

            ArgumentException error = Assert.Throws<ArgumentException>(() =>
                VideoClipCompressionRunner.ValidateTarget(
                    record,
                    path,
                    60,
                    0,
                    60,
                    options));

            Assert.Equal("目标大小不能超过当前片段", error.Message);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void LargeTargetRetry_DoesNotOverflowInt64()
    {
        long target = 3L * 1024 * 1024 * 1024;
        long actual = target + 120L * VideoCompressionPolicy.Mebibyte;

        long retry = VideoCompressionPolicy.CalculateRetryTargetBytes(
            target,
            actual);

        Assert.InRange(retry, 1, target);
    }

    [Fact]
    public void SecondaryActions_DeferFirstFrameWithoutBlockingPreparedPlayback()
    {
        string controls = ReadPlaybackFile("PlaybackWindow.Controls.cs");
        string preparation = ReadPlaybackFile("PlaybackWindow.Preparation.cs");
        string playback = ReadPlaybackFile("PlaybackWindow.Playback.cs");

        Assert.Contains("_secondaryActionState.Begin();", controls);
        Assert.Contains("CancelAwaitingFirstFrame();", controls);
        Assert.DoesNotContain(
            "if (_secondaryActionState.IsActive)",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains("BtnTogglePlay.IsEnabled = true;", preparation);
        Assert.Contains("TimelineSlider.IsEnabled = true;", preparation);
        Assert.Contains(
            "PlaybackInteractionPolicy.ShouldUnpauseAfterFirstFrame",
            playback);
        Assert.Contains(
            "await Task.Run(() => WindowsShellFileLocator.Locate(dialog.OutputPath));",
            controls);
    }

    [Fact]
    public void ExportDialog_SerializesCancellationBeforeSuccess()
    {
        string dialog = ReadPlaybackFile("VideoCompressionDialog.xaml.cs");

        Assert.Contains("_cancelRequested = true;", dialog);
        Assert.Contains("if (_cancelRequested)", dialog);
        Assert.Contains("_cancelRequested = false;", dialog);
        Assert.Contains("_cancelConfirmationOpen = true;", dialog);
        Assert.Contains("_hasPendingSuccess", dialog);
        Assert.Contains("CompleteSuccessfulExport();", dialog);
        Assert.Contains("_encoderProbeCancellation?.Cancel();", dialog);
        Assert.Contains("probeCancellation.Token", dialog);
        Assert.Contains("AV1 编码器预探测失败", dialog);
        Assert.Contains(
            "catch (OperationCanceledException)",
            dialog);
        Assert.Contains(
            "_service.IsEncoderAvailableAsync",
            dialog);
    }

    [Theory]
    [InlineData(28, 30)]
    [InlineData(30, 30)]
    [InlineData(32, 30)]
    [InlineData(48, 50)]
    [InlineData(52, 50)]
    [InlineData(68, 70)]
    [InlineData(72, 70)]
    [InlineData(33, null)]
    [InlineData(45, null)]
    public void PresetHighlight_UsesTwoPercentagePointTolerance(
        int targetPercent,
        int? expectedPreset)
    {
        const long sourceBytes = 100L * VideoCompressionPolicy.Mebibyte;
        long targetBytes = sourceBytes * targetPercent / 100;

        Assert.Equal(
            expectedPreset,
            VideoCompressionPolicy.ResolvePresetPercent(
                sourceBytes,
                targetBytes));
    }

    [Fact]
    public void PlaybackAndDialog_ExposeExportWithoutPermanentToolbarButton()
    {
        string playbackXaml = ReadPlaybackFile("PlaybackWindow.xaml");
        string dialogXaml = ReadPlaybackFile("VideoCompressionDialog.xaml");
        string dialogSource = ReadPlaybackFile("VideoCompressionDialog.xaml.cs");
        string mainPlaybackSource = ReadProjectFile(
            Path.Combine("ViewModels", "MainViewModel.Media.cs"));
        string backupHostSource = ReadProjectFile(
            Path.Combine("Workstations", "PrintWorkstationWindow.xaml.cs"));
        string playbackControls = ReadPlaybackFile("PlaybackWindow.Controls.cs");
        string compressionService = ReadProjectFile(
            Path.Combine("Services", "VideoCompressionService.cs"));

        Assert.Contains("Header=\"{loc:Localized 导出压缩版本…}\"", playbackXaml);
        Assert.Contains("Click=\"CompressVideo_Click\"", playbackXaml);
        Assert.Contains("IsEnabled=\"{Binding CanCompress}\"", playbackXaml);
        Assert.Contains("Header=\"{loc:Localized 剪辑}\"", playbackXaml);
        Assert.Contains("Click=\"OpenWebClipMenu_Click\"", playbackXaml);
        Assert.True(
            playbackXaml.IndexOf(
                "Header=\"{loc:Localized 剪辑}\"",
                StringComparison.Ordinal)
            < playbackXaml.IndexOf(
                "Header=\"{loc:Localized 导出压缩版本…}\"",
                StringComparison.Ordinal),
            "剪辑应当排在导出压缩版本上方");
        Assert.DoesNotContain("x:Name=\"BtnCompressVideo\"", playbackXaml);
        Assert.DoesNotContain("<TextBox", dialogXaml);
        Assert.Contains("x:Name=\"TargetSizeSlider\"", dialogXaml);
        Assert.Contains(
            "Style=\"{StaticResource AccentSliderStyle}\"",
            dialogXaml);
        Assert.Contains("DisplayMemberPath=\"DisplayName\"", dialogXaml);
        Assert.Contains("SelectedValuePath=\"Value\"", dialogXaml);
        Assert.DoesNotContain("PreserveCodecItem", dialogXaml);
        Assert.Contains(
            "GpuEncoderOption.BuildDisplayName(\"h264\")",
            dialogSource);
        Assert.Contains(
            "GpuEncoderOption.BuildDisplayName(\"h265\")",
            dialogSource);
        Assert.Contains(
            "GpuEncoderOption.BuildDisplayName(\"av1\")",
            dialogSource);
        Assert.Contains("x:Name=\"TargetPreset30Button\"", dialogXaml);
        Assert.Contains("x:Name=\"TargetPreset50Button\"", dialogXaml);
        Assert.Contains("x:Name=\"TargetPreset70Button\"", dialogXaml);
        Assert.Contains(
            "x:Key=\"PresetButtonActiveStyle\"",
            dialogXaml);
        Assert.Contains("x:Name=\"EstimatedSizeText\"", dialogXaml);
        Assert.Contains(
            "Style=\"{StaticResource RoundedSmoothProgressBar}\"",
            dialogXaml);
        Assert.Contains("WindowStyle=\"SingleBorderWindow\"", dialogXaml);
        Assert.Contains(
            "VideoCompressionPolicy.GetDefaultTargetBytes",
            dialogSource);
        Assert.Contains(
            "VideoCompressionPolicy.EstimateSeconds",
            dialogSource);
        Assert.Contains(
            "VideoCompressionPolicy.ResolveValidatedAv1Encoder",
            dialogSource);
        Assert.Contains(
            "validatedEncoders: Config.ValidatedEncodersCache",
            mainPlaybackSource);
        Assert.Contains(
            "validatedEncoders: _config.ValidatedEncodersCache",
            backupHostSource);
        Assert.Contains(
            "validatedEncoders: _validatedEncoders",
            playbackControls);
        Assert.Contains(
            "BeginSecondaryActionPause();",
            playbackControls);
        Assert.Equal(
            2,
            playbackControls.Split(
                "BeginSecondaryActionPause();",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "_mediaPlayer.Pause();",
            playbackControls);
        Assert.Contains(
            "UpdatePlayState(false);",
            playbackControls);
        Assert.Contains(
            "Environment.SpecialFolder.MyVideos",
            dialogSource);
        Assert.Contains(
            "FileSystemIdentity.AreSameFile",
            compressionService);
        Assert.Contains(
            "CleanupStaleWorkDirectories(outputDirectory);",
            compressionService);
        Assert.Contains(
            "MarkDirectoryHidden(workDirectory);",
            compressionService);
    }

    private static string ReadPlaybackFile(string fileName)
    {
        return ReadProjectFile(Path.Combine("UI", fileName));
    }

    private static string ReadProjectFile(string relativePath)
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring",
            relativePath);
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "ExpressPackingMonitoring repository root was not found.");
    }
}
