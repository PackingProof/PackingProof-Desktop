using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionRecordingQueryServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "epm-extension-recording-query-" + Guid.NewGuid().ToString("N"));

    public ExtensionRecordingQueryServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task LocalRecording_BecomesReadyWithoutCacheCopy()
    {
        string videoPath = Path.Combine(_directory, "local.mp4");
        await File.WriteAllBytesAsync(videoPath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        using var database = new VideoDatabase(Path.Combine(_directory, "local.db"));
        AddCompleted(database, "TRACK-LOCAL", videoPath, archivePath: "");
        using var service = new ExtensionRecordingQueryService(database, Path.Combine(_directory, "cache"));

        ExtensionRecordingQuerySnapshot created = service.Create("extension-owner", "TRACK-LOCAL");
        ExtensionRecordingQuerySnapshot ready = await WaitForTerminalAsync(service, created.QueryId, "extension-owner");

        ExtensionRecordingSnapshot recording = Assert.Single(ready.Recordings);
        Assert.Equal("ready", recording.Status);
        Assert.Equal(4, recording.FileSizeBytes);
        Assert.Equal(60, recording.DurationSeconds);
        Assert.Equal("local.mp4", recording.FileName);
        Assert.False(Directory.Exists(Path.Combine(_directory, "cache", "extension-recording-queries", created.QueryId)));
        Assert.True(service.TryBeginDownload(created.QueryId, recording.RecordingId, "extension-owner", out string downloadPath));
        Assert.Equal(videoPath, downloadPath);
    }

    [Fact]
    public async Task ArchiveOnlyRecording_IsCopiedToCacheAndBoundToOwner()
    {
        string missingLocalPath = Path.Combine(_directory, "missing.mp4");
        string archivePath = Path.Combine(_directory, "archive.mp4");
        await File.WriteAllBytesAsync(archivePath, [5, 6, 7, 8, 9], TestContext.Current.CancellationToken);
        using var database = new VideoDatabase(Path.Combine(_directory, "archive.db"));
        long id = AddCompleted(database, "TRACK-NAS", missingLocalPath, archivePath);
        database.UpdateArchiveState(id, VideoArchiveStatus.Verified, completedAt: DateTime.Now);
        using var service = new ExtensionRecordingQueryService(database, Path.Combine(_directory, "cache"));

        ExtensionRecordingQuerySnapshot created = service.Create("extension-owner", "TRACK-NAS");
        ExtensionRecordingQuerySnapshot ready = await WaitForTerminalAsync(service, created.QueryId, "extension-owner");

        ExtensionRecordingSnapshot recording = Assert.Single(ready.Recordings);
        Assert.Equal("ready", recording.Status);
        Assert.Equal(100, recording.Progress);
        Assert.False(service.TryGet(created.QueryId, "different-owner", out _));
        Assert.True(service.TryBeginDownload(created.QueryId, recording.RecordingId, "extension-owner", out string cachedPath));
        Assert.StartsWith(Path.Combine(_directory, "cache"), cachedPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(await File.ReadAllBytesAsync(archivePath, TestContext.Current.CancellationToken), await File.ReadAllBytesAsync(cachedPath, TestContext.Current.CancellationToken));
        service.FinishDownload(created.QueryId, recording.RecordingId, completed: false);
        Assert.True(service.TryBeginDownload(created.QueryId, recording.RecordingId, "extension-owner", out _));
    }

    [Fact]
    public async Task IdentifierContainsMatch_IsUsedWhenExactQueryHasNoResult()
    {
        string videoPath = Path.Combine(_directory, "contains.mp4");
        await File.WriteAllBytesAsync(videoPath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        using var database = new VideoDatabase(Path.Combine(_directory, "contains.db"));
        AddCompleted(database, "ORDER-6974412900385-01", videoPath, archivePath: "");
        using var service = new ExtensionRecordingQueryService(database, Path.Combine(_directory, "cache"));

        ExtensionRecordingQuerySnapshot created = service.Create("extension-owner", "6974412900385");
        ExtensionRecordingQuerySnapshot ready = await WaitForTerminalAsync(service, created.QueryId, "extension-owner");

        Assert.Equal("ready", ready.Status);
        Assert.Single(ready.Recordings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AB")]
    [InlineData("TRACK/INVALID")]
    public void InvalidTrackingNumber_IsRejected(string value)
    {
        Assert.Throws<InvalidDataException>(() => ExtensionRecordingQueryService.NormalizeTrackingNumber(value));
    }

    private static long AddCompleted(VideoDatabase database, string orderId, string filePath, string archivePath)
    {
        DateTime start = DateTime.Now.AddMinutes(-2);
        long id = database.InsertVideoRecord(orderId, "发货", "h264", "", filePath, start, archivePath: archivePath);
        database.UpdateVideoRecordOnStop(id, start.AddMinutes(1), 60, 0, "手动");
        return id;
    }

    private static async Task<ExtensionRecordingQuerySnapshot> WaitForTerminalAsync(
        ExtensionRecordingQueryService service,
        string queryId,
        string ownerId)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            Assert.True(service.TryGet(queryId, ownerId, out ExtensionRecordingQuerySnapshot? snapshot));
            if (snapshot!.Status is "ready" or "completed" or "not_found" or "failed") return snapshot;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("录像查询任务未完成");
    }

    public void Dispose()
    {
        SqliteTestPool.ClearPoolFor(_directory);
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}

public sealed class TargetSizeTranscodeBitrateTests
{
    [Fact]
    public void TryCalculate_UsesTargetSizeAndActualDuration()
    {
        const long targetBytes = 190L * 1024 * 1024;

        Assert.True(TargetSizeTranscodeBitrate.TryCalculate(60, targetBytes, out TargetSizeTranscodeBitrate? oneMinute));
        Assert.True(TargetSizeTranscodeBitrate.TryCalculate(120, targetBytes, out TargetSizeTranscodeBitrate? twoMinutes));

        Assert.NotNull(oneMinute);
        Assert.NotNull(twoMinutes);
        Assert.True(oneMinute.VideoBitsPerSecond > twoMinutes.VideoBitsPerSecond);
        Assert.InRange(oneMinute.AudioBitsPerSecond, 32_000, 128_000);
    }

    [Fact]
    public void TryCalculate_RejectsMissingDurationAndImpossibleBudget()
    {
        Assert.False(TargetSizeTranscodeBitrate.TryCalculate(0, 190L * 1024 * 1024, out _));
        Assert.False(TargetSizeTranscodeBitrate.TryCalculate(3600, 1024 * 1024, out _));
    }
}

public sealed class TargetSizeTranscodeEngineTests
{
    [Fact]
    public void EstimateTranscodeSeconds_UsesEncoderAndRetryFactor()
    {
        double x265First = TargetSizeTranscodeEngine.EstimateTranscodeSeconds(150, "libx265", 1);
        double x265Second = TargetSizeTranscodeEngine.EstimateTranscodeSeconds(150, "libx265", 2);
        double x264First = TargetSizeTranscodeEngine.EstimateTranscodeSeconds(150, "libx264", 1);

        Assert.True(x265First > 150, "软编 h265 两遍应比录像本身长");
        Assert.Equal(x265First * 2, x265Second);
        Assert.True(x264First < x265First, "h264 源比 h265 源快");
        Assert.Equal(0, TargetSizeTranscodeEngine.EstimateTranscodeSeconds(0, "libx265", 1));
        Assert.Equal(0, TargetSizeTranscodeEngine.EstimateTranscodeSeconds(double.NaN, "libx265", 1));
    }

    [Fact]
    public void GetTranscodeTimeout_UsesEstimateWithFloor()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), TargetSizeTranscodeEngine.GetTranscodeTimeout(60));
        Assert.Equal(TimeSpan.FromMinutes(60), TargetSizeTranscodeEngine.GetTranscodeTimeout(1200));
    }

    /// <summary>
    /// 看门狗的两个原因必须分开报：进度不推进是"卡住"，一直推进但超了硬超时是"压不完"。
    /// 两者共用同一个错误码时，现场排查分不清是 ffmpeg 卡了还是机器不行。
    /// </summary>
    [Fact]
    public void GetTranscodeWatchdogFailure_DistinguishesStallFromTimeout()
    {
        TimeSpan timeout = TimeSpan.FromMinutes(30);

        Assert.Equal(
            TargetSizeTranscodeFailure.None,
            TargetSizeTranscodeEngine.GetTranscodeWatchdogFailure(
            elapsed: TimeSpan.FromMinutes(5),
            sinceProgress: TimeSpan.FromMinutes(1),
            timeout));
        Assert.Equal(
            TargetSizeTranscodeFailure.Stalled,
            TargetSizeTranscodeEngine.GetTranscodeWatchdogFailure(
                elapsed: TimeSpan.FromMinutes(40),
                sinceProgress: TimeSpan.FromMinutes(11),
                timeout));
        // 进度一直在推、只是慢：这不是卡死，报超时。
        Assert.Equal(
            TargetSizeTranscodeFailure.Timeout,
            TargetSizeTranscodeEngine.GetTranscodeWatchdogFailure(
                elapsed: TimeSpan.FromMinutes(40),
                sinceProgress: TimeSpan.FromSeconds(5),
                timeout));
    }

    [Fact]
    public void X265Pass_UsesX265StatsFileInsteadOfFfmpegPassOption()
    {
        string passOne = TargetSizeTranscodeEngine.BuildTwoPassArguments("libx265", @"C:\cache\out.mp4.pass", 1);
        string passTwo = TargetSizeTranscodeEngine.BuildTwoPassArguments("libx265", @"C:\cache\out.mp4.pass", 2);

        Assert.Contains("-x265-params \"pass=1:stats=x265_2pass.log:cutree=0\"", passOne);
        Assert.Contains("-x265-params \"pass=2:stats=x265_2pass.log:cutree=0\"", passTwo);
        Assert.DoesNotContain("-passlogfile", passOne);
        Assert.DoesNotContain("-passlogfile", passTwo);
    }

    [Fact]
    public void X265Pass_KeepsStatsFileRelative()
    {
        string passOne = TargetSizeTranscodeEngine.BuildTwoPassArguments("libx265", @"C:\cache\out.mp4.pass", 1);

        int index = passOne.IndexOf("stats=", StringComparison.Ordinal) + "stats=".Length;
        Assert.Equal("x265_2pass.log", passOne[index..].TrimEnd('"').Split(':')[0]);
    }

    [Fact]
    public void X264Pass_KeepsNativeFfmpegPassOptions()
    {
        string passTwo = TargetSizeTranscodeEngine.BuildTwoPassArguments("libx264", @"C:\cache\out.mp4.pass", 2);

        Assert.Contains("-passlogfile \"C:\\cache\\out.mp4.pass\"", passTwo);
        Assert.Contains("-pass 2", passTwo);
        Assert.DoesNotContain("-x265-params", passTwo);
    }
}
