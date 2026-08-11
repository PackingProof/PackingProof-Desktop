using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ArchiveServiceTests : IDisposable
{
    private sealed class GatedProvider : IArchiveProvider
    {
        private readonly NasArchiveProvider _inner = new();
        public TaskCompletionSource<bool> Gate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PublishFileAsync(
            string sourcePath,
            string destinationPath,
            long recordId,
            string expectedSha256,
            string attemptToken,
            CancellationToken cancellationToken)
        {
            await Gate.Task;
            await _inner.PublishFileAsync(
                sourcePath,
                destinationPath,
                recordId,
                expectedSha256,
                attemptToken,
                cancellationToken);
        }

        public Task<RemoteProbeResult> ProbeAsync(
            string path,
            long expectedSize,
            CancellationToken cancellationToken) =>
            _inner.ProbeAsync(path, expectedSize, cancellationToken);

        public Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken) =>
            _inner.ComputeSha256Async(path, cancellationToken);

        public Task RenameAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            _inner.RenameAsync(sourcePath, destinationPath, cancellationToken);
    }

    private sealed class RecordingProvider : IArchiveProvider
    {
        private readonly NasArchiveProvider _inner = new();
        public List<string> PublishedPaths { get; } = new();

        public Task PublishFileAsync(
            string sourcePath,
            string destinationPath,
            long recordId,
            string expectedSha256,
            string attemptToken,
            CancellationToken cancellationToken)
        {
            PublishedPaths.Add(destinationPath);
            return _inner.PublishFileAsync(
                sourcePath,
                destinationPath,
                recordId,
                expectedSha256,
                attemptToken,
                cancellationToken);
        }

        public Task<RemoteProbeResult> ProbeAsync(
            string path,
            long expectedSize,
            CancellationToken cancellationToken) =>
            _inner.ProbeAsync(path, expectedSize, cancellationToken);

        public Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken) =>
            _inner.ComputeSha256Async(path, cancellationToken);

        public Task RenameAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            _inner.RenameAsync(sourcePath, destinationPath, cancellationToken);
    }

    private sealed class CorruptVerifyProvider : IArchiveProvider
    {
        private readonly NasArchiveProvider _inner = new();

        public Task PublishFileAsync(
            string sourcePath,
            string destinationPath,
            long recordId,
            string expectedSha256,
            string attemptToken,
            CancellationToken cancellationToken) =>
            _inner.PublishFileAsync(
                sourcePath,
                destinationPath,
                recordId,
                expectedSha256,
                attemptToken,
                cancellationToken);

        public Task<RemoteProbeResult> ProbeAsync(
            string path,
            long expectedSize,
            CancellationToken cancellationToken) =>
            _inner.ProbeAsync(path, expectedSize, cancellationToken);

        public Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new string('0', 64));

        public Task RenameAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            _inner.RenameAsync(sourcePath, destinationPath, cancellationToken);

    }

    private sealed class DiskFullProvider : IArchiveProvider
    {
        public Task PublishFileAsync(
            string sourcePath,
            string destinationPath,
            long recordId,
            string expectedSha256,
            string attemptToken,
            CancellationToken cancellationToken) =>
            Task.FromException(new IOException("磁盘空间不足，无法写入归档目标", 112));

        public Task<RemoteProbeResult> ProbeAsync(
            string path,
            long expectedSize,
            CancellationToken cancellationToken) =>
            Task.FromResult(RemoteProbeResult.NotExists);

        public Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new string('0', 64));

        public Task RenameAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "epm-archive-service-" + Guid.NewGuid().ToString("N"));
    private readonly string _localRoot;
    private readonly string _nasRoot;
    private readonly string _dbPath;
    private readonly VideoDatabase _database;

    public ArchiveServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _localRoot = Path.Combine(_directory, "local");
        _nasRoot = Path.Combine(_directory, "nas");
        Directory.CreateDirectory(_localRoot);
        Directory.CreateDirectory(_nasRoot);
        _dbPath = Path.Combine(_directory, "videos.db");
        _database = new VideoDatabase(_dbPath);
    }

    public void Dispose()
    {
        _database.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private long InsertPendingRecord(string fileName, string content)
    {
        string localPath = Path.Combine(_localRoot, fileName);
        File.WriteAllText(localPath, content);
        DateTime now = DateTime.Now;
        long id = _database.InsertVideoRecord(
            "单号" + fileName,
            "发货",
            "h264",
            "libx264",
            localPath,
            now.AddMinutes(-5),
            archivePath: Path.Combine(_nasRoot, now.ToString("yyyy-MM-dd"), fileName));
        _database.UpdateVideoRecordOnStop(id, now, 10, content.Length, "手动");
        _database.MarkArchivePending(id);
        return id;
    }

    private ArchiveService CreateService(int batchSize = 20) =>
        new(
            _database,
            new NasArchiveProvider(),
            new ArchiveWorkerOptions
            {
                AutomaticWorkerEnabled = false,
                BatchSize = batchSize
            });

    [Fact]
    public async Task HappyPath_CopiesVerifiesAndMarksVerified()
    {
        long id = InsertPendingRecord("a.mp4", "hello-archive");
        using ArchiveService service = CreateService();

        int completed = await service.ProcessPendingOnceAsync(CancellationToken.None);

        Assert.Equal(1, completed);
        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.Verified, record.ArchiveStatus);
        Assert.Equal(64, record.ContentSha256.Length);
        Assert.NotNull(record.ArchiveCompletedAt);
        Assert.NotNull(record.LastArchiveProbeAt);
        Assert.True(File.Exists(record.ArchivePath));
        Assert.Equal("hello-archive", File.ReadAllText(record.ArchivePath));
    }

    [Fact]
    public async Task ExistingSameContent_IsIdempotentVerified()
    {
        long id = InsertPendingRecord("b.mp4", "same-content");
        DateTime now = DateTime.Now;
        string dest = Path.Combine(_nasRoot, now.ToString("yyyy-MM-dd"), "b.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, "same-content");
        using ArchiveService service = CreateService();

        await service.ProcessPendingOnceAsync(CancellationToken.None);

        Assert.Equal(VideoArchiveStatus.Verified, _database.GetVideoById(id)!.ArchiveStatus);
        Assert.Equal("same-content", File.ReadAllText(dest));
    }

    [Fact]
    public async Task ExistingDifferentSize_MarksConflict()
    {
        long id = InsertPendingRecord("c.mp4", "source-content");
        DateTime now = DateTime.Now;
        string dest = Path.Combine(_nasRoot, now.ToString("yyyy-MM-dd"), "c.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, "a-much-longer-conflicting-content");
        using ArchiveService service = CreateService();

        await service.ProcessPendingOnceAsync(CancellationToken.None);

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.Conflict, record.ArchiveStatus);
        Assert.Contains("禁止覆盖", record.ArchiveError);
        Assert.True(File.Exists(record.FilePath), "本地源必须保留");
        Assert.True(File.Exists(dest), "NAS 原文件必须保留");
        Assert.False(File.Exists(dest + ".corrupt"), "冲突不得重命名/覆盖 NAS 旧文件");
    }

    [Fact]
    public async Task ExistingSameSizeDifferentContent_MarksConflict()
    {
        long id = InsertPendingRecord("d.mp4", "abc");
        DateTime now = DateTime.Now;
        string dest = Path.Combine(_nasRoot, now.ToString("yyyy-MM-dd"), "d.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, "abd");
        using ArchiveService service = CreateService();

        await service.ProcessPendingOnceAsync(CancellationToken.None);

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.Conflict, record.ArchiveStatus);
        Assert.Equal("abd", File.ReadAllText(record.ArchivePath));
        Assert.False(File.Exists(record.ArchivePath + ".corrupt"), "冲突不得重命名/覆盖 NAS 旧文件");
    }

    [Fact]
    public async Task MissingLocalSource_MarksFailedWithRetry()
    {
        string localPath = Path.Combine(_localRoot, "missing.mp4");
        DateTime now = DateTime.Now;
        long id = _database.InsertVideoRecord(
            "单号missing",
            "发货",
            "h264",
            "libx264",
            localPath,
            now.AddMinutes(-5),
            archivePath: Path.Combine(_nasRoot, now.ToString("yyyy-MM-dd"), "missing.mp4"));
        _database.UpdateVideoRecordOnStop(id, now, 10, 100, "手动");
        _database.MarkArchivePending(id);
        using ArchiveService service = CreateService();

        await service.ProcessPendingOnceAsync(CancellationToken.None);

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.Failed, record.ArchiveStatus);
        Assert.Equal(1, record.ArchiveRetryCount);
        Assert.NotNull(record.NextRetryAt);
    }

    [Fact]
    public async Task UserDelete_KeepsNasArchiveFile()
    {
        long id = InsertPendingRecord("keep-on-nas.mp4", "archive-kept");
        VideoRecord record = _database.GetVideoById(id)!;
        using ArchiveService service = CreateService();

        await service.ProcessPendingOnceAsync(CancellationToken.None);
        Assert.Equal(VideoArchiveStatus.Verified, _database.GetVideoById(id)!.ArchiveStatus);

        _database.MarkRecordDeletedById(id, "用户删除", RecordingDeletionReasonCode.UserRequested);

        Assert.Null(_database.GetVideoById(id));
        Assert.True(File.Exists(record.ArchivePath), "NAS 归档文件必须保留，程序只上传不删除");
    }

    [Fact]
    public async Task BatchSize_LimitsItemsPerPass()
    {
        InsertPendingRecord("e1.mp4", "one");
        InsertPendingRecord("e2.mp4", "two");
        InsertPendingRecord("e3.mp4", "three");
        using ArchiveService service = CreateService(batchSize: 2);

        int completed = await service.ProcessPendingOnceAsync(CancellationToken.None);

        Assert.Equal(2, completed);
        IReadOnlyList<VideoRecord> remaining = _database.GetPendingArchives(20, DateTime.Now);
        VideoRecord untouched = Assert.Single(remaining);
        Assert.Equal("e3.mp4", Path.GetFileName(untouched.FilePath));
        Assert.Equal(VideoArchiveStatus.Pending, untouched.ArchiveStatus);
    }

    [Fact]
    public async Task CopyingLeftoverTemp_IsCleanedAndArchived()
    {
        long id = InsertPendingRecord("recover.mp4", "recover-content");
        DateTime now = DateTime.Now;
        VideoRecord record = _database.GetVideoById(id)!;
        string tempPath = record.ArchivePath + $".{id}.1-stale.uploading";
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        File.WriteAllText(tempPath, "incomplete");
        File.SetLastWriteTimeUtc(tempPath, DateTime.UtcNow.AddHours(-25));
        _database.UpdateArchiveState(id, VideoArchiveStatus.Copying, attemptedAt: now);
        using ArchiveService service = CreateService();

        await service.ProcessPendingOnceAsync(CancellationToken.None);

        Assert.Equal(VideoArchiveStatus.Verified, _database.GetVideoById(id)!.ArchiveStatus);
        Assert.False(File.Exists(tempPath));
        Assert.True(File.Exists(record.ArchivePath));
    }

    [Fact]
    public async Task DiskFull_MarksNasFullWithoutRetry()
    {
        long id = InsertPendingRecord("nas-full.mp4", "nas-full-content");
        using var service = new ArchiveService(
            _database,
            new DiskFullProvider(),
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false });

        await service.ProcessPendingOnceAsync(CancellationToken.None);

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.NASFull, record.ArchiveStatus);
        Assert.Equal(0, record.ArchiveRetryCount);
        Assert.Null(record.NextRetryAt);
        Assert.Empty(_database.GetPendingArchives(20, DateTime.Now));
    }

    [Fact]
    public async Task HashMismatch_RenamesCorruptTargetAndMarksFailed()
    {
        long id = InsertPendingRecord("corrupt.mp4", "corrupt-content");
        VideoRecord record = _database.GetVideoById(id)!;
        using var service = new ArchiveService(
            _database,
            new CorruptVerifyProvider(),
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false });

        await service.ProcessPendingOnceAsync(CancellationToken.None);

        VideoRecord failed = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.Failed, failed.ArchiveStatus);
        Assert.Contains("HashMismatch", failed.ArchiveError);
        Assert.False(File.Exists(record.ArchivePath));
        Assert.True(File.Exists(record.ArchivePath + ".corrupt"));
        Assert.True(File.Exists(record.FilePath), "本地源必须保留");
    }

    [Fact]
    public async Task OfflineAccumulation_RecoversOldestFirst()
    {
        DateTime now = DateTime.Now;
        long idA = InsertPendingRecord("a.mp4", "aaa");
        long idB = InsertPendingRecord("b.mp4", "bbb");
        _database.UpdateVideoRecordOnStop(idA, now.AddMinutes(-30), 10, 3, "手动");
        _database.MarkArchivePending(idA);

        var provider = new RecordingProvider();
        using var service = new ArchiveService(
            _database,
            provider,
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false });

        await service.ProcessPendingOnceAsync(CancellationToken.None);

        Assert.Equal(VideoArchiveStatus.Verified, _database.GetVideoById(idA)!.ArchiveStatus);
        Assert.Equal(VideoArchiveStatus.Verified, _database.GetVideoById(idB)!.ArchiveStatus);
        Assert.Equal(2, provider.PublishedPaths.Count);
        Assert.Equal(_database.GetVideoById(idA)!.ArchivePath, provider.PublishedPaths[0]);
        Assert.Equal(_database.GetVideoById(idB)!.ArchivePath, provider.PublishedPaths[1]);
    }

    [Fact]
    public async Task SlowArchiveWorker_DoesNotBlockRecordingPath()
    {
        long id = InsertPendingRecord("slow.mp4", "slow-content");
        var provider = new GatedProvider();
        using var service = new ArchiveService(
            _database,
            provider,
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false });

        Task<int> processing = service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200);
        Assert.False(processing.IsCompleted, "Worker 阻塞在慢 Provider 时不影响其他流程");

        // 模拟录像完成：新记录走完本地定稿流程，不经过 Provider
        DateTime now = DateTime.Now;
        long secondId = _database.InsertVideoRecord(
            "单号B",
            "发货",
            "h264",
            "libx264",
            Path.Combine(_localRoot, "second.mp4"),
            now.AddMinutes(-5),
            archivePath: Path.Combine(_nasRoot, now.ToString("yyyy-MM-dd"), "second.mp4"));
        _database.UpdateVideoRecordOnStop(secondId, now, 10, 3, "手动");
        _database.MarkArchivePending(secondId);
        Assert.Equal(VideoArchiveStatus.Pending, _database.GetVideoById(secondId)!.ArchiveStatus);

        provider.Gate.TrySetResult(true);
        await processing;
        Assert.Equal(VideoArchiveStatus.Verified, _database.GetVideoById(id)!.ArchiveStatus);
    }
}
