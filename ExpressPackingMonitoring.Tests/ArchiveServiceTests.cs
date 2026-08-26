using ExpressPackingMonitoring.Config;
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

        public Task<IArchiveProvider.DeleteOutcome> DeleteAsync(
            string path,
            IReadOnlyList<string> allowedRoots,
            CancellationToken cancellationToken) =>
            _inner.DeleteAsync(path, allowedRoots, cancellationToken);
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

        public Task<IArchiveProvider.DeleteOutcome> DeleteAsync(
            string path,
            IReadOnlyList<string> allowedRoots,
            CancellationToken cancellationToken) =>
            _inner.DeleteAsync(path, allowedRoots, cancellationToken);
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

        public Task<IArchiveProvider.DeleteOutcome> DeleteAsync(
            string path,
            IReadOnlyList<string> allowedRoots,
            CancellationToken cancellationToken) =>
            _inner.DeleteAsync(path, allowedRoots, cancellationToken);
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

        public Task<IArchiveProvider.DeleteOutcome> DeleteAsync(
            string path,
            IReadOnlyList<string> allowedRoots,
            CancellationToken cancellationToken) =>
            Task.FromException<IArchiveProvider.DeleteOutcome>(
                new NotSupportedException("测试 Provider 未实现删除"));
    }

    private sealed class SelectiveDiskFullProvider : IArchiveProvider
    {
        private readonly NasArchiveProvider _inner = new();
        private readonly string _fullRoot;

        public SelectiveDiskFullProvider(string fullRoot)
        {
            _fullRoot = fullRoot;
        }

        public Task PublishFileAsync(
            string sourcePath,
            string destinationPath,
            long recordId,
            string expectedSha256,
            string attemptToken,
            CancellationToken cancellationToken) =>
            IsUnderRoot(_fullRoot, destinationPath)
                ? Task.FromException(new IOException("磁盘空间不足，无法写入归档目标", 112))
                : _inner.PublishFileAsync(
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

        public Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken) =>
            _inner.ComputeSha256Async(path, cancellationToken);

        public Task RenameAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            _inner.RenameAsync(sourcePath, destinationPath, cancellationToken);

        public Task<IArchiveProvider.DeleteOutcome> DeleteAsync(
            string path,
            IReadOnlyList<string> allowedRoots,
            CancellationToken cancellationToken) =>
            _inner.DeleteAsync(path, allowedRoots, cancellationToken);

        private static bool IsUnderRoot(string root, string path) =>
            string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(
                root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private sealed class UnreachableProvider : IArchiveProvider
    {
        private readonly NasArchiveProvider _inner = new();
        public int PublishAttempts;
        public bool Unreachable { get; set; } = true;

        public Task PublishFileAsync(
            string sourcePath,
            string destinationPath,
            long recordId,
            string expectedSha256,
            string attemptToken,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref PublishAttempts);
            if (Unreachable)
            {
                return Task.FromException(new IOException(
                    $"找不到网络路径。 : '{Path.GetDirectoryName(destinationPath)}'",
                    unchecked((int)0x80070035)));
            }

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

        public Task<IArchiveProvider.DeleteOutcome> DeleteAsync(
            string path,
            IReadOnlyList<string> allowedRoots,
            CancellationToken cancellationToken) =>
            _inner.DeleteAsync(path, allowedRoots, cancellationToken);
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
    public async Task MissingLocalSource_EntersUnifiedDispositionWithoutRetry()
    {
        string localPath = Path.Combine(_localRoot, "missing.mp4");
        DateTime now = DateTime.Now;
        string archivePath = Path.Combine(_nasRoot, now.ToString("yyyy-MM-dd"), "missing.mp4");
        long id = _database.InsertVideoRecord(
            "单号missing",
            "发货",
            "h264",
            "libx264",
            localPath,
            now.AddMinutes(-5),
            archivePath: archivePath);
        _database.UpdateVideoRecordOnStop(id, now, 10, 100, "手动");
        _database.MarkArchivePending(id);
        using ArchiveService service = CreateService();

        int completed = await service.ProcessPendingOnceAsync(
            TestContext.Current.CancellationToken);

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(0, completed);
        Assert.Equal(VideoArchiveStatus.BackupLost, record.ArchiveStatus);
        Assert.Equal(0, record.ArchiveRetryCount);
        Assert.Null(record.NextRetryAt);

        // NAS 上存在候选文件但无完成证据 → 待核实，同样不重试
        string pendingPath = Path.Combine(_localRoot, "pending-missing.mp4");
        string pendingArchive = Path.Combine(_nasRoot, now.ToString("yyyy-MM-dd"), "pending-missing.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(pendingArchive)!);
        File.WriteAllText(pendingArchive, new string('x', 100));
        long pendingId = _database.InsertVideoRecord(
            "单号pending-missing",
            "发货",
            "h264",
            "libx264",
            pendingPath,
            now.AddMinutes(-4),
            archivePath: pendingArchive);
        _database.UpdateVideoRecordOnStop(pendingId, now, 10, 100, "手动");
        _database.MarkArchivePending(pendingId);

        await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);

        VideoRecord pendingRecord = _database.GetVideoById(pendingId)!;
        Assert.Equal(VideoArchiveStatus.LocalMissingUnverified, pendingRecord.ArchiveStatus);
        Assert.Equal(0, pendingRecord.ArchiveRetryCount);
        Assert.Null(pendingRecord.NextRetryAt);
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
    public async Task DiskFull_ReRoutesToNextAvailableTarget()
    {
        DateTime now = DateTime.Now;
        string nasRoot1 = Path.Combine(_directory, "nas1");
        string nasRoot2 = Path.Combine(_directory, "nas2");
        Directory.CreateDirectory(nasRoot1);
        Directory.CreateDirectory(nasRoot2);

        string localPath = Path.Combine(_localRoot, "reroute.mp4");
        File.WriteAllText(localPath, "reroute-content");
        long id = _database.InsertVideoRecord(
            "单号切换",
            "发货",
            "h264",
            "libx264",
            localPath,
            now.AddMinutes(-120),
            archivePath: Path.Combine(nasRoot1, now.ToString("yyyy-MM-dd"), "reroute.mp4"));
        _database.UpdateVideoRecordOnStop(id, now.AddMinutes(-60), 10, localPath.Length, "手动");
        _database.MarkArchivePending(id);

        using var service = new ArchiveService(
            _database,
            new SelectiveDiskFullProvider(nasRoot1),
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false },
            archiveTargetResolver: () =>
                new List<StorageLocation>
                {
                    new() { Path = nasRoot1, Priority = 0 },
                    new() { Path = nasRoot2, Priority = 1 }
                });

        await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);

        VideoRecord rerouted = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.Pending, rerouted.ArchiveStatus);
        Assert.StartsWith(nasRoot2, rerouted.ArchivePath);
        Assert.Contains("切换", rerouted.ArchiveError);

        await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);

        VideoRecord verified = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.Verified, verified.ArchiveStatus);
        Assert.StartsWith(nasRoot2, verified.ArchivePath);
        Assert.True(File.Exists(verified.ArchivePath));
    }

    [Fact]
    public async Task NoArchiveTarget_SkipsProcessingAndKeepsStatus()
    {
        long id = InsertPendingRecord("skip-no-target.mp4", "skip-content");
        using var service = new ArchiveService(
            _database,
            new NasArchiveProvider(),
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false },
            archiveTargetResolver: () => Array.Empty<StorageLocation>());

        int completed = await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, completed);
        Assert.Equal(VideoArchiveStatus.Pending, _database.GetVideoById(id)!.ArchiveStatus);
        Assert.False(File.Exists(_database.GetVideoById(id)!.ArchivePath));
    }

    [Fact]
    public async Task ValidArchiveTarget_ResolverAllowsProcessing()
    {
        long id = InsertPendingRecord("resume-target.mp4", "resume-content");
        using var service = new ArchiveService(
            _database,
            new NasArchiveProvider(),
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false },
            archiveTargetResolver: () =>
                new List<StorageLocation> { new() { Path = _nasRoot } });

        int completed = await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, completed);
        Assert.Equal(VideoArchiveStatus.Verified, _database.GetVideoById(id)!.ArchiveStatus);
    }

    [Fact]
    public async Task ArchiveTargetResolverThrows_SkipsRound()
    {
        long id = InsertPendingRecord("resolver-throws.mp4", "resolver-content");
        using var service = new ArchiveService(
            _database,
            new NasArchiveProvider(),
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false },
            archiveTargetResolver: () => throw new InvalidOperationException("配置无效"));

        int completed = await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, completed);
        Assert.Equal(VideoArchiveStatus.Pending, _database.GetVideoById(id)!.ArchiveStatus);
    }

    [Fact]
    public void Wake_ConcurrentCallsDoNotThrow()
    {
        using var service = new ArchiveService(
            _database,
            new NasArchiveProvider(),
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false });

        Parallel.For(0, 20, _ => service.Wake());
    }

    [Fact]
    public void Wake_AfterDisposeIsSafe()
    {
        var service = new ArchiveService(
            _database,
            new NasArchiveProvider(),
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false });
        service.Dispose();
        service.Wake();
    }

    [Fact]
    public async Task BackfillHistoricalArchives_FillsPathAndArchives()
    {
        DateTime now = DateTime.Now;
        string localPath = Path.Combine(_localRoot, "historical.mp4");
        File.WriteAllText(localPath, "historical-content");
        long id = _database.InsertVideoRecord(
            "单号历史",
            "发货",
            "h264",
            "libx264",
            localPath,
            now.AddMinutes(-120),
            archivePath: "");
        _database.UpdateVideoRecordOnStop(id, now.AddMinutes(-60), 10, localPath.Length, "手动");
        using var service = new ArchiveService(
            _database,
            new NasArchiveProvider(),
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false },
            archiveTargetResolver: () =>
                new List<StorageLocation> { new() { Path = _nasRoot } });

        int completed = await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(1, completed);
        Assert.Equal(VideoArchiveStatus.Verified, record.ArchiveStatus);
        string expected = ArchivePathBuilder.BuildLocalRecordingArchivePath(
            _nasRoot,
            record.StartTime,
            Path.GetFileName(record.FilePath));
        Assert.Equal(expected, record.ArchivePath);
        Assert.True(File.Exists(record.ArchivePath));
    }

    [Fact]
    public async Task BackfillHistoricalArchives_SkipsMissingLocalFile()
    {
        DateTime now = DateTime.Now;
        string localPath = Path.Combine(_localRoot, "missing-historical.mp4");
        long id = _database.InsertVideoRecord(
            "单号历史缺失",
            "发货",
            "h264",
            "libx264",
            localPath,
            now.AddMinutes(-120),
            archivePath: "");
        _database.UpdateVideoRecordOnStop(id, now.AddMinutes(-60), 10, 100, "手动");
        using var service = new ArchiveService(
            _database,
            new NasArchiveProvider(),
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false },
            archiveTargetResolver: () =>
                new List<StorageLocation> { new() { Path = _nasRoot } });

        await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(VideoArchiveStatus.LocalOnly, record.ArchiveStatus);
        Assert.Equal("", record.ArchivePath);
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
    public async Task UnreachableTarget_TripsCircuitAndSkipsRemainingBatch()
    {
        for (int i = 0; i < 8; i++)
            InsertPendingRecord($"unreachable-{i}.mp4", "content-" + i);

        var provider = new UnreachableProvider();
        using var service = new ArchiveService(
            _database,
            provider,
            new ArchiveWorkerOptions
            {
                AutomaticWorkerEnabled = false,
                BatchSize = 8,
                UnreachableFailureThreshold = 3,
                UnreachableCooldown = TimeSpan.FromMinutes(5),
                MaxUnreachableCooldown = TimeSpan.FromMinutes(10)
            });

        int completed = await service.ProcessPendingOnceAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(0, completed);
        Assert.Equal(3, provider.PublishAttempts);
        IReadOnlyList<VideoRecord> pending =
            _database.GetPendingArchives(20, DateTime.Now);
        Assert.Equal(5, pending.Count);
        Assert.All(
            pending,
            record => Assert.Equal(VideoArchiveStatus.Pending, record.ArchiveStatus));

        await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, provider.PublishAttempts);
    }

    [Fact]
    public async Task UnreachableTarget_RecoversAfterCooldown()
    {
        long id = InsertPendingRecord("recoverable.mp4", "recover-content");
        var provider = new UnreachableProvider();
        using var service = new ArchiveService(
            _database,
            provider,
            new ArchiveWorkerOptions
            {
                AutomaticWorkerEnabled = false,
                BatchSize = 20,
                UnreachableFailureThreshold = 1,
                UnreachableCooldown = TimeSpan.FromMilliseconds(150),
                MaxUnreachableCooldown = TimeSpan.FromMilliseconds(400)
            });

        int first = await service.ProcessPendingOnceAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(0, first);
        Assert.Equal(1, provider.PublishAttempts);
        Assert.Equal(VideoArchiveStatus.Failed, _database.GetVideoById(id)!.ArchiveStatus);

        int second = await service.ProcessPendingOnceAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(0, second);
        Assert.Equal(1, provider.PublishAttempts);

        provider.Unreachable = false;
        await Task.Delay(350);
        int third = await service.ProcessPendingOnceAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(1, third);
        Assert.Equal(VideoArchiveStatus.Verified, _database.GetVideoById(id)!.ArchiveStatus);
    }

    [Fact]
    public async Task OfflineAccumulation_RecoversNewestFirst()
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
        Assert.Equal(_database.GetVideoById(idB)!.ArchivePath, provider.PublishedPaths[0]);
        Assert.Equal(_database.GetVideoById(idA)!.ArchivePath, provider.PublishedPaths[1]);
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

    private sealed class ConcurrencyTrackingProvider : IArchiveProvider
    {
        private readonly NasArchiveProvider _inner = new();
        private int _activePublishes;
        private int _maxConcurrentPublishes;

        public int MaxConcurrentPublishes => Volatile.Read(ref _maxConcurrentPublishes);

        public async Task PublishFileAsync(
            string sourcePath,
            string destinationPath,
            long recordId,
            string expectedSha256,
            string attemptToken,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _activePublishes);
            int observed;
            do
            {
                observed = Volatile.Read(ref _maxConcurrentPublishes);
                if (active <= observed)
                    break;
            }
            while (Interlocked.CompareExchange(
                       ref _maxConcurrentPublishes,
                       active,
                       observed) != observed);

            try
            {
                await Task.Delay(30, cancellationToken);
                await _inner.PublishFileAsync(
                    sourcePath,
                    destinationPath,
                    recordId,
                    expectedSha256,
                    attemptToken,
                    cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activePublishes);
            }
        }

        public Task<RemoteProbeResult> ProbeAsync(
            string path,
            long expectedSize,
            CancellationToken cancellationToken) =>
            _inner.ProbeAsync(path, expectedSize, cancellationToken);

        public Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken) =>
            _inner.ComputeSha256Async(path, cancellationToken);

        public Task RenameAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            _inner.RenameAsync(sourcePath, destinationPath, cancellationToken);

        public Task<IArchiveProvider.DeleteOutcome> DeleteAsync(
            string path,
            IReadOnlyList<string> allowedRoots,
            CancellationToken cancellationToken) =>
            _inner.DeleteAsync(path, allowedRoots, cancellationToken);
    }

    private sealed class ThrottleAwareProvider : IArchiveProvider, IArchiveTransferThrottleAware
    {
        private readonly NasArchiveProvider _inner = new();
        private ArchiveTransferThrottle? _throttle;
        public bool SawActiveThrottle { get; private set; }
        public bool Unreachable { get; set; } = true;

        public void SetTransferThrottle(ArchiveTransferThrottle? throttle) => _throttle = throttle;

        public Task PublishFileAsync(
            string sourcePath,
            string destinationPath,
            long recordId,
            string expectedSha256,
            string attemptToken,
            CancellationToken cancellationToken)
        {
            SawActiveThrottle |= _throttle?.IsActive == true;
            if (Unreachable)
                return Task.FromException(new IOException(
                    "找不到网络路径。",
                    unchecked((int)0x80070035)));
            return _inner.PublishFileAsync(
                sourcePath,
                destinationPath,
                recordId,
                expectedSha256,
                attemptToken,
                cancellationToken);
        }

        public Task<RemoteProbeResult> ProbeAsync(string path, long expectedSize, CancellationToken cancellationToken) =>
            _inner.ProbeAsync(path, expectedSize, cancellationToken);

        public Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken) =>
            _inner.ComputeSha256Async(path, cancellationToken);

        public Task<IArchiveProvider.DeleteOutcome> DeleteAsync(
            string path,
            IReadOnlyList<string> allowedRoots,
            CancellationToken cancellationToken) =>
            _inner.DeleteAsync(path, allowedRoots, cancellationToken);

        public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken) =>
            _inner.RenameAsync(sourcePath, destinationPath, cancellationToken);
    }

    [Fact]
    public async Task RealtimeBusy_SkipsArchiveWithoutTouchingNas()
    {
        long id = InsertPendingRecord("busy.mp4", "busy-content");
        bool realtimeBusy = true;
        var provider = new RecordingProvider();
        using var service = new ArchiveService(
            _database,
            provider,
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false },
            loadStateProvider: () => realtimeBusy
                ? ArchiveLoadState.Paused
                : ArchiveLoadState.Healthy);

        Assert.Equal(0, await service.ProcessPendingOnceAsync(CancellationToken.None));
        Assert.Empty(provider.PublishedPaths);
        Assert.Equal(VideoArchiveStatus.Pending, _database.GetVideoById(id)!.ArchiveStatus);

        realtimeBusy = false;
        Assert.Equal(
            1,
            await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken));
        Assert.Single(provider.PublishedPaths);
    }

    [Fact]
    public async Task RecoveredBacklog_RampsBatchSizeAndWaitsBetweenRounds()
    {
        for (int i = 0; i < 6; i++)
            InsertPendingRecord($"ramp-{i}.mp4", "ramp-content-" + i);

        var provider = new UnreachableProvider();
        using var service = new ArchiveService(
            _database,
            provider,
            new ArchiveWorkerOptions
            {
                AutomaticWorkerEnabled = false,
                BatchSize = 20,
                UnreachableFailureThreshold = 1,
                UnreachableCooldown = TimeSpan.FromMilliseconds(20),
                MaxUnreachableCooldown = TimeSpan.FromMilliseconds(20),
                RecoveryInitialBatchSize = 1,
                RecoveryMaxBatchSize = 4,
                RecoveryInterBatchDelay = TimeSpan.FromMilliseconds(40)
            });

        Assert.Equal(0, await service.ProcessPendingOnceAsync(CancellationToken.None));
        provider.Unreachable = false;
        await WaitUntilAsync(
            async () => await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken) == 1,
            TimeSpan.FromSeconds(2));

        int verifiedAfterFirstRound = _database.QueryVideos(null, null)
            .Count(record => record.ArchiveStatus == VideoArchiveStatus.Verified);
        Assert.Equal(1, verifiedAfterFirstRound);
        Assert.Equal(0, await service.ProcessPendingOnceAsync(CancellationToken.None));

        int completed = 0;
        await WaitUntilAsync(
            async () =>
            {
                completed = await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);
                return completed == 2;
            },
            TimeSpan.FromSeconds(2));
        Assert.Equal(2, completed);
        verifiedAfterFirstRound = _database.QueryVideos(null, null)
            .Count(record => record.ArchiveStatus == VideoArchiveStatus.Verified);
        Assert.Equal(3, verifiedAfterFirstRound);
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"条件在 {timeout.TotalSeconds:F0} 秒内未满足");
    }

    [Fact]
    public async Task RecoveryFirstUpload_UsesThrottleBeforeReachabilityIsRecorded()
    {
        for (int i = 0; i < 3; i++)
            InsertPendingRecord($"throttle-first-{i}.mp4", "throttle-content-" + i);
        var unreachable = new ThrottleAwareProvider();
        using var service = new ArchiveService(
            _database,
            unreachable,
            new ArchiveWorkerOptions
            {
                AutomaticWorkerEnabled = false,
                UnreachableFailureThreshold = 1,
                UnreachableCooldown = TimeSpan.FromMilliseconds(20)
            });

        await service.ProcessPendingOnceAsync(CancellationToken.None);
        unreachable.Unreachable = false;
        await Task.Delay(40);
        Assert.Equal(1, await service.ProcessPendingOnceAsync(CancellationToken.None));
        Assert.True(unreachable.SawActiveThrottle);
    }

    [Fact]
    public async Task RemovedArchiveRoot_ReroutesFailedRecordAndUsesNewPathImmediately()
    {
        long id = InsertPendingRecord("reroute-old-root.mp4", "reroute-content");
        string oldPath = Path.Combine(_directory, "removed-nas", "reroute-old-root.mp4");
        _database.RerouteArchivePath(id, oldPath);
        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Failed,
            error: "找不到网络路径",
            incrementRetry: true);
        var provider = new RecordingProvider();
        using var service = new ArchiveService(
            _database,
            provider,
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false },
            archiveTargetResolver: () =>
                [new StorageLocation { Path = _nasRoot, Priority = 0 }]);

        Assert.Equal(1, await service.ProcessPendingOnceAsync(CancellationToken.None));

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.StartsWith(_nasRoot, record.ArchivePath);
        Assert.Equal(record.ArchivePath, Assert.Single(provider.PublishedPaths));
        Assert.Equal(VideoArchiveStatus.Verified, record.ArchiveStatus);
        Assert.Equal(0, record.ArchiveRetryCount);
        Assert.False(File.Exists(oldPath));
    }

    [Fact]
    public async Task ConfiguredArchiveRoot_IsNotRerouted()
    {
        long id = InsertPendingRecord("keep-current-root.mp4", "keep-content");
        string expectedPath = _database.GetVideoById(id)!.ArchivePath;
        var provider = new RecordingProvider();
        using var service = new ArchiveService(
            _database,
            provider,
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false },
            archiveTargetResolver: () =>
                [new StorageLocation { Path = _nasRoot, Priority = 0 }]);

        Assert.Equal(1, await service.ProcessPendingOnceAsync(CancellationToken.None));

        Assert.Equal(expectedPath, _database.GetVideoById(id)!.ArchivePath);
        Assert.Equal(expectedPath, Assert.Single(provider.PublishedPaths));
    }

    [Fact]
    public async Task WorkerEvents_ReportUploadingQueueChangeAndFinalIdle()
    {
        InsertPendingRecord("worker-events.mp4", "worker-event-content");
        var phases = new List<ArchiveWorkerPhase>();
        int queueChanges = 0;
        using var service = new ArchiveService(
            _database,
            new RecordingProvider(),
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false });
        service.WorkerStateChanged += snapshot => phases.Add(snapshot.Phase);
        service.ArchiveQueueChanged += () => queueChanges++;

        Assert.Equal(
            1,
            await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken));

        Assert.Contains(ArchiveWorkerPhase.Uploading, phases);
        Assert.Equal(ArchiveWorkerPhase.Idle, service.CurrentWorkerSnapshot.Phase);
        Assert.Equal(1, queueChanges);
    }

    [Fact]
    public void ConditionalReroute_RejectsChangedPathAndNonRetryableStates()
    {
        long id = InsertPendingRecord("conditional-reroute.mp4", "conditional-content");
        string original = _database.GetVideoById(id)!.ArchivePath;
        string replacement = Path.Combine(_nasRoot, "replacement.mp4");

        Assert.Equal(
            0,
            _database.TryReroutePendingArchivePath(id, original + ".changed", replacement));
        Assert.Equal(original, _database.GetVideoById(id)!.ArchivePath);

        _database.UpdateArchiveState(id, VideoArchiveStatus.Copying);
        Assert.Equal(0, _database.TryReroutePendingArchivePath(id, original, replacement));
        _database.UpdateArchiveState(id, VideoArchiveStatus.Verified);
        Assert.Equal(0, _database.TryReroutePendingArchivePath(id, original, replacement));

        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Failed,
            error: "旧错误",
            incrementRetry: true,
            nextRetryAt: DateTime.Now.AddHours(1));
        Assert.Equal(1, _database.TryReroutePendingArchivePath(id, original, replacement));
        VideoRecord rerouted = _database.GetVideoById(id)!;
        Assert.Equal(replacement, rerouted.ArchivePath);
        Assert.Equal(VideoArchiveStatus.Pending, rerouted.ArchiveStatus);
        Assert.Equal("", rerouted.ArchiveError);
        Assert.Equal(0, rerouted.ArchiveRetryCount);
        Assert.Null(rerouted.NextRetryAt);
    }

    [Fact]
    public async Task PersistedFailedBacklog_StartsRecoveryAtSingleRecord()
    {
        for (int i = 0; i < 4; i++)
        {
            long id = InsertPendingRecord($"startup-failed-{i}.mp4", "failed-" + i);
            _database.UpdateArchiveState(id, VideoArchiveStatus.Failed, error: "旧路径不可达");
        }
        var provider = new RecordingProvider();
        using var service = new ArchiveService(
            _database,
            provider,
            new ArchiveWorkerOptions
            {
                AutomaticWorkerEnabled = false,
                BatchSize = 20,
                RecoveryBacklogThreshold = 4,
                RecoveryInitialBatchSize = 1,
                RecoveryMaxBatchSize = 4,
                RecoveryInterBatchDelay = TimeSpan.FromMilliseconds(30)
            });
        int queueChanges = 0;
        service.ArchiveQueueChanged += () => queueChanges++;

        Assert.Equal(
            1,
            await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken));
        Assert.Single(provider.PublishedPaths);
        Assert.Equal(1, queueChanges);
        Assert.Equal(
            ArchiveWorkerPhase.WaitingForNextBatch,
            service.CurrentWorkerSnapshot.Phase);
        Assert.Equal(
            0,
            await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(
            2,
            await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RecoveryBatchGreaterThanOne_RemainsStrictlySequential()
    {
        for (int i = 0; i < 4; i++)
        {
            long id = InsertPendingRecord($"sequential-{i}.mp4", "sequential-" + i);
            _database.UpdateArchiveState(id, VideoArchiveStatus.Failed, error: "旧路径不可达");
        }
        var provider = new ConcurrencyTrackingProvider();
        using var service = new ArchiveService(
            _database,
            provider,
            new ArchiveWorkerOptions
            {
                AutomaticWorkerEnabled = false,
                BatchSize = 20,
                RecoveryBacklogThreshold = 4,
                RecoveryInitialBatchSize = 1,
                RecoveryMaxBatchSize = 4,
                RecoveryInterBatchDelay = TimeSpan.FromMilliseconds(20)
            });

        Assert.Equal(
            1,
            await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken));
        await Task.Delay(40, TestContext.Current.CancellationToken);
        Assert.Equal(
            2,
            await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, provider.MaxConcurrentPublishes);
    }

    [Fact]
    public async Task AutomaticRecoveryWorker_UsesRecoveryDelayInsteadOfLongPollInterval()
    {
        for (int i = 0; i < 4; i++)
        {
            long id = InsertPendingRecord($"automatic-recovery-{i}.mp4", "automatic-" + i);
            _database.UpdateArchiveState(id, VideoArchiveStatus.Failed, error: "旧路径不可达");
        }

        using var service = new ArchiveService(
            _database,
            new RecordingProvider(),
            new ArchiveWorkerOptions
            {
                AutomaticWorkerEnabled = true,
                PollInterval = TimeSpan.FromSeconds(30),
                RecoveryBacklogThreshold = 4,
                RecoveryInitialBatchSize = 1,
                RecoveryMaxBatchSize = 4,
                RecoveryInterBatchDelay = TimeSpan.FromMilliseconds(50)
            });

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        int verified;
        do
        {
            verified = _database.QueryVideos(null, null)
                .Count(record => record.ArchiveStatus == VideoArchiveStatus.Verified);
            if (verified >= 3)
                break;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        while (DateTime.UtcNow < deadline);

        Assert.True(
            verified >= 3,
            $"恢复 Worker 应在短恢复间隔内处理后续批次，实际完成 {verified} 个");
    }

    [Fact]
    public void AdaptiveThrottle_RampsToUnlimitedAndDemotesUnderPressure()
    {
        var time = new MutableTimeProvider();
        ArchiveLoadState state = ArchiveLoadState.Healthy;
        var throttle = new ArchiveTransferThrottle(
            () => true,
            () => state,
            time);
        const long mib = 1024L * 1024;

        Assert.Equal(96 * mib, throttle.CurrentBytesPerSecond);
        foreach (long expected in new[] { 192 * mib, 384 * mib, 768 * mib, 0L })
        {
            time.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(expected, throttle.CurrentBytesPerSecond);
        }

        state = ArchiveLoadState.Degraded;
        Assert.Equal(384 * mib, throttle.CurrentBytesPerSecond);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(192 * mib, throttle.CurrentBytesPerSecond);
        state = ArchiveLoadState.Paused;
        _ = throttle.CurrentBytesPerSecond;
        state = ArchiveLoadState.Healthy;
        Assert.Equal(24 * mib, throttle.CurrentBytesPerSecond);
    }

    [Fact]
    public async Task AdaptiveThrottle_PausesCurrentChunkUntilRecordingEnds()
    {
        ArchiveLoadState state = ArchiveLoadState.Paused;
        var throttle = new ArchiveTransferThrottle(
            () => true,
            () => state);

        Task wait = throttle.WaitAsync(1, TestContext.Current.CancellationToken).AsTask();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);

        state = ArchiveLoadState.Healthy;
        await wait.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan interval) => _utcNow += interval;
    }

    [Fact]
    public async Task Backfill_CatchesUpTo2000ThenThrottlesUntilWake()
    {
        const int total = 3000;
        for (int i = 0; i < total; i++)
            InsertBackfillCandidate($"bf-{i:D4}.mp4");

        using var service = new ArchiveService(
            _database,
            new DiskFullProvider(),
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false },
            archiveTargetResolver: () =>
                new List<StorageLocation> { new() { Path = _nasRoot, Priority = 0 } });

        await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2000, CountBackfilled());

        // 距上次不足 5 分钟：不再回填
        await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2000, CountBackfilled());

        // Wake 后立即追赶剩余 1000
        service.Wake();
        await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(total, CountBackfilled());
    }

    [Fact]
    public async Task Backfill_Exactly2000_CompletesInOneRound()
    {
        const int total = 2000;
        for (int i = 0; i < total; i++)
            InsertBackfillCandidate($"bfx-{i:D4}.mp4");

        using var service = new ArchiveService(
            _database,
            new DiskFullProvider(),
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false },
            archiveTargetResolver: () =>
                new List<StorageLocation> { new() { Path = _nasRoot, Priority = 0 } });

        await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(total, CountBackfilled());

        await service.ProcessPendingOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(total, CountBackfilled());
    }

    [Fact]
    public async Task Conflict_NeverAutoRetried()
    {
        long id = InsertPendingRecord("conflict.mp4", "conflict-content");
        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Conflict,
            error: "NAS 已有同名不同内容");
        var provider = new RecordingProvider();
        using var service = new ArchiveService(
            _database,
            provider,
            new ArchiveWorkerOptions { AutomaticWorkerEnabled = false });

        int completed = await service.ProcessPendingOnceAsync(
            TestContext.Current.CancellationToken);

        VideoRecord record = _database.GetVideoById(id)!;
        Assert.Equal(0, completed);
        Assert.Equal(VideoArchiveStatus.Conflict, record.ArchiveStatus);
        Assert.Equal(0, record.ArchiveRetryCount);
        Assert.Empty(provider.PublishedPaths);
        Assert.DoesNotContain(
            _database.GetPendingArchives(20, DateTime.Now),
            candidate => candidate.Id == id);
    }

    private long InsertBackfillCandidate(string fileName)
    {
        string localPath = Path.Combine(_localRoot, fileName);
        File.WriteAllText(localPath, "x");
        DateTime now = DateTime.Now;
        long id = _database.InsertVideoRecord(
            "单号" + fileName,
            "发货",
            "h264",
            "libx264",
            localPath,
            now.AddMinutes(-120),
            archivePath: "");
        _database.UpdateVideoRecordOnStop(id, now.AddMinutes(-60), 10, 1, "手动");
        return id;
    }

    private int CountBackfilled() =>
        _database.QueryVideos(null, null)
            .Count(record => !string.IsNullOrWhiteSpace(record.ArchivePath));
}
