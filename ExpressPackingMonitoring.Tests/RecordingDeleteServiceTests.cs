using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 回放“删除录像”的回归：删本地副本、记录按用户删除留档、备份位置归档不被顺手删掉，
/// 以及正在录制/正在备份/文件删不掉时绝不能只改数据库。
/// </summary>
[Collection("Recording lifecycle")]
public sealed class RecordingDeleteServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "epm-recording-delete-" + Guid.NewGuid().ToString("N"));
    private readonly VideoDatabase _database;

    public RecordingDeleteServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _database = new VideoDatabase(Path.Combine(_directory, "videos.db"));
    }

    public void Dispose()
    {
        _database.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private (long Id, string FilePath) InsertFinishedRecording(
        string orderId = "单号A",
        string archivePath = "")
    {
        string filePath = Path.Combine(_directory, $"{orderId}-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(filePath, new byte[64]);
        DateTime start = DateTime.Now.AddMinutes(-5);
        long id = _database.InsertVideoRecord(
            orderId,
            "发货",
            "h264",
            "libx264",
            filePath,
            start,
            archivePath: archivePath);
        _database.UpdateVideoRecordOnStop(id, start.AddMinutes(1), 10, 64, "手动");
        return (id, filePath);
    }

    private RecordingDeleteService CreateService() =>
        new(_database, TimeSpan.FromMilliseconds(200));

    private Task<RecordingDeleteResult> DeleteAsync(long recordId) =>
        CreateService().DeleteRecordingAsync(recordId, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Delete_RemovesLocalFileAndKeepsDeletionRecord()
    {
        (long id, string filePath) = InsertFinishedRecording();

        RecordingDeleteResult result = await DeleteAsync(id);

        Assert.True(result.Deleted, result.Message);
        Assert.False(File.Exists(filePath));
        Assert.Null(_database.GetVideoById(id));

        VideoRecord deleted = _database.QueryVideos(null, null).Single(record => record.Id == id);
        Assert.True(deleted.IsDeleted);
        Assert.Equal(RecordingDeletionReasonCode.UserRequested, deleted.DeleteReasonCode);
        Assert.Equal("用户删除", deleted.DeleteReason);

        DeleteLogEntry log = Assert.Single(
            _database.GetDeleteLogs(10),
            entry => entry.OrderId == "单号A");
        Assert.Equal("用户删除", log.Reason);
    }

    [Fact]
    public async Task Delete_KeepsArchiveCopyOnBackupLocation()
    {
        string archivePath = Path.Combine(_directory, "nas", "归档.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        File.WriteAllBytes(archivePath, new byte[64]);
        (long id, string filePath) = InsertFinishedRecording(archivePath: archivePath);
        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Verified,
            contentSha256: "abc123",
            completedAt: DateTime.Now);

        RecordingDeleteResult result = await DeleteAsync(id);

        Assert.True(result.Deleted, result.Message);
        Assert.False(File.Exists(filePath));
        Assert.True(File.Exists(archivePath), "备份位置上的归档副本只上传不删除，必须保留");
        Assert.True(_database.QueryVideos(null, null).Single(record => record.Id == id).IsDeleted);
    }

    [Fact]
    public async Task Delete_RefusesWhileRecordingIsStillRunning()
    {
        string filePath = Path.Combine(_directory, "recording.mp4");
        File.WriteAllBytes(filePath, new byte[64]);
        long id = _database.InsertVideoRecord(
            "单号录制中",
            "发货",
            "h264",
            "libx264",
            filePath,
            DateTime.Now);

        RecordingDeleteResult result = await DeleteAsync(id);

        Assert.False(result.Deleted);
        Assert.Contains("正在录制", result.Message);
        Assert.True(File.Exists(filePath));
        Assert.NotNull(_database.GetVideoById(id));
    }

    [Fact]
    public async Task Delete_RefusesWhileArchiveTransferIsRunning()
    {
        (long id, string filePath) = InsertFinishedRecording();
        _database.MarkArchivePending(id);
        _database.UpdateArchiveState(id, VideoArchiveStatus.Copying, attemptedAt: DateTime.Now);

        RecordingDeleteResult result = await DeleteAsync(id);

        Assert.False(result.Deleted);
        Assert.Contains("正在备份", result.Message);
        Assert.True(File.Exists(filePath));
        Assert.NotNull(_database.GetVideoById(id));
    }

    [Fact]
    public async Task Delete_RefusesWhenRecordingIsStoredOnRecordingHost()
    {
        (long id, _) = InsertFinishedRecording(orderId: "单号在主机");
        _database.MarkVideoCacheDeleted(id, 4242);
        Assert.Equal("Remote", _database.GetVideoById(id)!.StorageState);

        RecordingDeleteResult result = await DeleteAsync(id);

        Assert.False(result.Deleted);
        Assert.Contains("录像主机", result.Message);
        Assert.NotNull(_database.GetVideoById(id));
    }

    [Fact]
    public async Task Delete_RefusesWhenLocalFileIsMissingWithoutArchiveEvidence()
    {
        string filePath = Path.Combine(_directory, "gone.mp4");
        DateTime start = DateTime.Now.AddMinutes(-5);
        long id = _database.InsertVideoRecord(
            "单号文件丢失",
            "发货",
            "h264",
            "libx264",
            filePath,
            start);
        _database.UpdateVideoRecordOnStop(id, start.AddMinutes(1), 10, 64, "手动");

        RecordingDeleteResult result = await DeleteAsync(id);

        Assert.False(result.Deleted);
        Assert.Contains("找不到录像文件", result.Message);
        Assert.NotNull(_database.GetVideoById(id));
    }

    [Fact]
    public async Task Delete_RefusesWhileAnotherTaskOwnsTheRecord()
    {
        (long id, string filePath) = InsertFinishedRecording(orderId: "单号归档中");

        using IDisposable lease = await VideoLifecycleCoordinator.EnterAsync(
            id,
            TestContext.Current.CancellationToken);
        RecordingDeleteResult result = await DeleteAsync(id);

        Assert.False(result.Deleted);
        Assert.Contains("稍后再试", result.Message);
        Assert.True(File.Exists(filePath));
        Assert.NotNull(_database.GetVideoById(id));
    }

    [Fact]
    public async Task Delete_KeepsDatabaseUntouchedWhenLocalFileCannotBeRemoved()
    {
        // 句柄占用只在 Windows 上真的挡住删除；其它平台没有这个语义就不做这条断言。
        if (!OperatingSystem.IsWindows())
            return;

        (long id, string filePath) = InsertFinishedRecording(orderId: "单号被占用");
        using (var hold = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            RecordingDeleteResult result = await DeleteAsync(id);

            Assert.False(result.Deleted);
            Assert.Contains("删除失败", result.Message);
        }

        // 文件还在就绝不能改数据库：否则记录消失、文件永远留在磁盘上。
        Assert.True(File.Exists(filePath));
        VideoRecord record = _database.QueryVideos(null, null).Single(item => item.Id == id);
        Assert.False(record.IsDeleted);
        Assert.NotNull(_database.GetVideoById(id));
    }

    [Fact]
    public async Task Delete_IsNotRepeatable()
    {
        (long id, _) = InsertFinishedRecording(orderId: "单号重复删");
        RecordingDeleteService service = CreateService();

        Assert.True((await service.DeleteRecordingAsync(id, TestContext.Current.CancellationToken)).Deleted);
        RecordingDeleteResult second = await service.DeleteRecordingAsync(
            id,
            TestContext.Current.CancellationToken);

        Assert.False(second.Deleted);
        Assert.Contains("已删除", second.Message);
    }

    [Fact]
    public void Policy_AllowsFinishedLocalRecording()
    {
        var record = new VideoRecord
        {
            FilePath = @"C:\video\a.mp4",
            EndTime = DateTime.Now,
            ArchiveStatus = VideoArchiveStatus.LocalOnly
        };

        Assert.Null(RecordingDeletePolicy.DescribeRefusal(record, localFileExists: true));
        Assert.True(RecordingDeletePolicy.CanDelete(record, localFileExists: true));
    }

    [Fact]
    public void Policy_AllowsRecordWhoseLocalCopyWasCleanedAfterArchive()
    {
        var record = new VideoRecord
        {
            FilePath = @"C:\video\a.mp4",
            EndTime = DateTime.Now,
            ArchiveStatus = VideoArchiveStatus.LocalDeleted,
            ArchivePath = @"\\nas\share\a.mp4",
            ArchiveCompletedAt = DateTime.Now
        };

        Assert.True(RecordingDeletePolicy.CanDelete(record, localFileExists: false));
    }

    [Fact]
    public void Policy_RejectsMissingFileWithoutArchiveEvidence()
    {
        var record = new VideoRecord
        {
            FilePath = @"C:\video\a.mp4",
            EndTime = DateTime.Now,
            ArchiveStatus = VideoArchiveStatus.LocalOnly
        };

        Assert.False(RecordingDeletePolicy.CanDelete(record, localFileExists: false));
    }

    [Fact]
    public void Policy_RejectsDeletedRecord()
    {
        var record = new VideoRecord
        {
            FilePath = @"C:\video\a.mp4",
            EndTime = DateTime.Now,
            ArchiveStatus = VideoArchiveStatus.LocalDeleted,
            IsDeleted = true
        };

        Assert.False(RecordingDeletePolicy.CanDelete(record, localFileExists: false));
    }
}
