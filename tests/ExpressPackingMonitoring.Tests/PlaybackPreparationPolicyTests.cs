using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class PlaybackPreparationPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "packingproof-playback-preparation-" + Guid.NewGuid().ToString("N"));

    public PlaybackPreparationPolicyTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task CompletedMp4WithoutItsSourceMkvIsUsedDirectly()
    {
        string mp4Path = CreateFile("recording.mp4", "mp4");
        int conversionCalls = 0;

        PlaybackPreparationResult result =
            await PlaybackPreparationPolicy.PrepareAsync(
                mp4Path,
                _ =>
                {
                    conversionCalls++;
                    return Task.FromResult(
                        MkvConversionResult.Ok(mp4Path));
                },
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

        Assert.Equal(PlaybackPreparationState.Ready, result.State);
        Assert.Equal(mp4Path, result.FilePath);
        Assert.Equal(0, conversionCalls);
    }

    [Fact]
    public async Task RecordedMp4PathWithAdjacentMkvUsesLocalValidationWithoutSidecar()
    {
        string mkvPath = CreateFile("recording.mkv", "mkv");
        string mp4Path = CreateFile("recording.mp4", "mp4");
        bool converterCalled = false;

        PlaybackPreparationResult result =
            await PlaybackPreparationPolicy.PrepareAsync(
                mp4Path,
                _ =>
                {
                    converterCalled = true;
                    return Task.FromResult(
                        MkvConversionResult.Ok(mp4Path));
                },
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

        Assert.True(converterCalled);
        Assert.Equal(mkvPath, PlaybackPreparationPolicy.ResolveSourcePath(mp4Path));
        Assert.Equal(PlaybackPreparationState.Ready, result.State);
        Assert.Equal(mp4Path, result.FilePath);
    }

    [Fact]
    public async Task RemotePreparationUsesExistingMp4WhenOnlyTheHostCanValidateIt()
    {
        _ = CreateFile("recording.mkv", "mkv");
        string mp4Path = CreateFile("recording.mp4", "mp4");

        PlaybackPreparationResult result =
            await PlaybackPreparationPolicy.PrepareAsync(
                mp4Path,
                converter: null,
                waitTimeout: TimeSpan.FromSeconds(1),
                cancellationToken: CancellationToken.None);

        Assert.Equal(PlaybackPreparationState.Ready, result.State);
        Assert.Equal(mp4Path, result.FilePath);
    }

    [Fact]
    public async Task RecordedMp4PathWithRecoverySidecarWaitsForSourceMkvHostConversion()
    {
        string mkvPath = CreateFile("recording.mkv", "mkv");
        string mp4Path = CreateFile("recording.mp4", "incomplete");
        CreateFile("recording.wav", "audio");

        PlaybackPreparationResult result =
            await PlaybackPreparationPolicy.PrepareAsync(
                mp4Path,
                converter: null,
                waitTimeout: TimeSpan.FromMilliseconds(40),
                cancellationToken: CancellationToken.None,
                pollInterval: TimeSpan.FromMilliseconds(10));

        Assert.Equal(mkvPath, PlaybackPreparationPolicy.ResolveSourcePath(mp4Path));
        Assert.Equal(
            PlaybackPreparationState.FallbackSource,
            result.State);
        Assert.True(result.TimedOut);
        Assert.Equal(mkvPath, result.FilePath);
    }

    [Fact]
    public void AudioDiagnosticWithoutWavIsNotARecoverySidecar()
    {
        string mkvPath = CreateFile("recording.mkv", "mkv");
        CreateFile("recording.audio.log", "diagnostic only");

        Assert.False(PlaybackPreparationPolicy.HasRecoverySidecar(mkvPath));
    }

    [Fact]
    public async Task RecordedMp4PathWithRecoverySidecarUsesValidatedLocalConversion()
    {
        string mkvPath = CreateFile("recording.mkv", "mkv");
        string mp4Path = CreateFile("recording.mp4", "incomplete");
        CreateFile("recording.embedded-audio", "audio");
        bool converterCalled = false;

        PlaybackPreparationResult result =
            await PlaybackPreparationPolicy.PrepareAsync(
                mp4Path,
                _ =>
                {
                    converterCalled = true;
                    File.WriteAllText(mp4Path, "mp4");
                    return Task.FromResult(
                        MkvConversionResult.Ok(mp4Path));
                },
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

        Assert.True(converterCalled);
        Assert.Equal(mkvPath, PlaybackPreparationPolicy.ResolveSourcePath(mp4Path));
        Assert.Equal(PlaybackPreparationState.Ready, result.State);
        Assert.Equal(mp4Path, result.FilePath);
    }

    [Fact]
    public async Task AmbiguousMkvAndMp4UseLocalValidationBeforePlayback()
    {
        string mkvPath = CreateFile("recording.mkv", "mkv");
        string mp4Path = CreateFile("recording.mp4", "mp4");
        bool converterCalled = false;

        PlaybackPreparationResult result =
            await PlaybackPreparationPolicy.PrepareAsync(
                mkvPath,
                _ =>
                {
                    converterCalled = true;
                    return Task.FromResult(
                        MkvConversionResult.Ok(mp4Path));
                },
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

        Assert.True(converterCalled);
        Assert.Equal(PlaybackPreparationState.Ready, result.State);
        Assert.Equal(mp4Path, result.FilePath);
    }

    [Fact]
    public async Task LocalMkvWaitsForConverterAndUsesCompletedMp4()
    {
        string mkvPath = CreateFile("recording.mkv", "mkv");
        string mp4Path = Path.ChangeExtension(mkvPath, ".mp4");

        PlaybackPreparationResult result =
            await PlaybackPreparationPolicy.PrepareAsync(
                mkvPath,
                _ =>
                {
                    File.WriteAllText(mp4Path, "mp4");
                    return Task.FromResult(MkvConversionResult.Ok(mp4Path));
                },
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

        Assert.Equal(PlaybackPreparationState.Ready, result.State);
        Assert.Equal(mp4Path, result.FilePath);
    }

    [Fact]
    public async Task LanWaitPollsForTheHostToPublishMp4()
    {
        string mkvPath = CreateFile("network-recording.mkv", "mkv");
        string mp4Path = Path.ChangeExtension(mkvPath, ".mp4");
        _ = Task.Run(
            async () =>
            {
                await Task.Delay(
                    80,
                    TestContext.Current.CancellationToken);
                await File.WriteAllTextAsync(
                    mp4Path,
                    "mp4",
                    TestContext.Current.CancellationToken);
            },
            TestContext.Current.CancellationToken);

        PlaybackPreparationResult result =
            await PlaybackPreparationPolicy.PrepareAsync(
                mkvPath,
                converter: null,
                waitTimeout: TimeSpan.FromSeconds(2),
                cancellationToken: CancellationToken.None,
                pollInterval: TimeSpan.FromMilliseconds(10));

        Assert.Equal(PlaybackPreparationState.Ready, result.State);
        Assert.Equal(mp4Path, result.FilePath);
    }

    [Fact]
    public async Task ConversionFailureFallsBackWithTheOriginalPreserved()
    {
        string mkvPath = CreateFile("recording.mkv", "mkv");
        CreateFile("recording.mp4", "incomplete");

        PlaybackPreparationResult result =
            await PlaybackPreparationPolicy.PrepareAsync(
                mkvPath,
                _ => Task.FromResult(
                    MkvConversionResult.Fail("conversion failed", mkvPath)),
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

        Assert.Equal(
            PlaybackPreparationState.FallbackSource,
            result.State);
        Assert.True(result.ConversionFailed);
        Assert.Equal(mkvPath, result.FilePath);
    }

    [Fact]
    public async Task ExistingMp4IsNotTrustedWhileMuxSidecarsRemain()
    {
        string mkvPath = CreateFile("recording.mkv", "mkv");
        CreateFile("recording.mp4", "incomplete");
        CreateFile("recording.wav", "audio");

        PlaybackPreparationResult result =
            await PlaybackPreparationPolicy.PrepareAsync(
                mkvPath,
                converter: null,
                waitTimeout: TimeSpan.FromMilliseconds(40),
                cancellationToken: CancellationToken.None,
                pollInterval: TimeSpan.FromMilliseconds(10));

        Assert.Equal(
            PlaybackPreparationState.FallbackSource,
            result.State);
        Assert.True(result.TimedOut);
        Assert.Equal(mkvPath, result.FilePath);
    }

    [Fact]
    public async Task MissingDatabaseMp4FallsBackToItsMkvAndCanBeConverted()
    {
        string mkvPath = CreateFile("recording.mkv", "mkv");
        string staleMp4Path = Path.ChangeExtension(mkvPath, ".mp4");
        bool converterCalled = false;

        PlaybackPreparationResult result =
            await PlaybackPreparationPolicy.PrepareAsync(
                staleMp4Path,
                _ =>
                {
                    converterCalled = true;
                    File.WriteAllText(staleMp4Path, "mp4");
                    return Task.FromResult(
                        MkvConversionResult.Ok(staleMp4Path));
                },
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

        Assert.True(converterCalled);
        Assert.Equal(PlaybackPreparationState.Ready, result.State);
        Assert.Equal(staleMp4Path, result.FilePath);
    }

    [Fact]
    public async Task MissingConversionTimesOutWithoutCallingTheFileCorrupt()
    {
        string mkvPath = CreateFile("recording.mkv", "mkv");

        PlaybackPreparationResult result =
            await PlaybackPreparationPolicy.PrepareAsync(
                mkvPath,
                converter: null,
                waitTimeout: TimeSpan.FromMilliseconds(40),
                cancellationToken: CancellationToken.None,
                pollInterval: TimeSpan.FromMilliseconds(10));

        Assert.Equal(
            PlaybackPreparationState.FallbackSource,
            result.State);
        Assert.True(result.TimedOut);
        Assert.False(result.ConversionFailed);
    }

    [Fact]
    public void UncPlaybackWaitsForTheHostInsteadOfStartingAClientConversion()
    {
        Assert.False(PlaybackPreparationPolicy.CanConvertOnThisMachine(
            @"\\recording-host\share\2026-11-10\recording.mkv"));
        Assert.True(PlaybackPreparationPolicy.CanConvertOnThisMachine(
            Path.Combine(_directory, "recording.mkv")));
    }

    [Fact]
    public async Task PreparationWaitCanBeCancelledWithoutReportingFailure()
    {
        string mkvPath = CreateFile("recording.mkv", "mkv");
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PlaybackPreparationPolicy.PrepareAsync(
                mkvPath,
                converter: null,
                waitTimeout: TimeSpan.FromSeconds(5),
                cancellationToken: cancellation.Token,
                pollInterval: TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void PlaybackWindowShowsPreparationStateAndRetriesInTheSameWindow()
    {
        string code = ReadRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "PlaybackWindow.Preparation.cs");
        string xaml = ReadRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "PlaybackWindow.xaml");
        string mainMedia = ReadRepositoryFile(
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Media.cs");

        Assert.Contains(
            "PreparePlaybackPathAsync(",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "PlaybackPreparationPolicy.PrepareAsync(",
            code,
            StringComparison.Ordinal);
        int sourceResolve = code.IndexOf(
            "PlaybackPreparationPolicy.ResolveSourcePath(video.FullPath)",
            StringComparison.Ordinal);
        int preparationCall = code.IndexOf(
            "PlaybackPreparationPolicy.PrepareAsync(",
            StringComparison.Ordinal);
        int mediaSourceCreate = code.IndexOf(
            "PlaybackMediaSource.Create(",
            StringComparison.Ordinal);
        Assert.True(
            sourceResolve >= 0
            && code.LastIndexOf(
                "await Task.Run(",
                sourceResolve,
                StringComparison.Ordinal) >= 0,
            "远程路径探测必须在后台线程执行");
        Assert.True(
            preparationCall >= 0
            && code.LastIndexOf(
                "await Task.Run(",
                preparationCall,
                StringComparison.Ordinal) >= 0,
            "转码等待中的文件探测必须在后台线程执行");
        Assert.True(
            mediaSourceCreate >= 0
            && code.LastIndexOf(
                "await Task.Run(",
                mediaSourceCreate,
                StringComparison.Ordinal) >= 0,
            "UNC 媒体文件打开必须在后台线程执行");
        Assert.True(
            code.IndexOf(
                "await PreparePlaybackPathAsync(",
                StringComparison.Ordinal)
            < code.IndexOf(
                "PlaybackMediaSource.Create(",
                StringComparison.Ordinal),
            "准备/转码完成后必须继续走同一个播放器启动流程");
        Assert.Contains(
            "PlaybackPreparationProgress.Visibility = Visibility.Visible",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"PlaybackPreparationProgress\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "prepareMkvForPlayback: PreparePlaybackVideoAsync",
            mainMedia,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "请确认视频文件完整",
            code,
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }

    private string CreateFile(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "ExpressPackingMonitoring.sln")))
            {
                return File.ReadAllText(
                    Path.Combine([directory.FullName, .. parts]));
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            "ExpressPackingMonitoring repository root was not found.");
    }
}
