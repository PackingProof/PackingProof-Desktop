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
