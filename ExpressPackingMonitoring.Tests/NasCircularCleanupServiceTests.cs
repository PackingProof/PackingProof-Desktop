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

public sealed class NasCircularCleanupServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "epm-nas-cleanup-" + Guid.NewGuid().ToString("N"));
    private readonly string _nasRoot;
    private readonly VideoDatabase _database;

    public NasCircularCleanupServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _nasRoot = Path.Combine(_directory, "nas");
        Directory.CreateDirectory(_nasRoot);
        _database = new VideoDatabase(Path.Combine(_directory, "videos.db"));
    }

    public void Dispose()
    {
        _database.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private long InsertVerified(
        string name,
        DateTime start,
        DateTime end,
        bool createLocal,
        bool createNas,
        string? archivePathOverride = null)
    {
        string localPath = Path.Combine(_directory, "local-" + name);
        string archivePath = archivePathOverride
            ?? Path.Combine(_nasRoot, "2026-08-11", name);
        if (createLocal)
            File.WriteAllText(localPath, "12345678901");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        if (createNas)
            File.WriteAllText(archivePath, "12345678901");
        long id = _database.InsertVideoRecord(
            "单号" + name,
            "发货",
            "h264",
            "libx264",
            localPath,
            start,
            archivePath: archivePath);
        _database.UpdateVideoRecordOnStop(id, end, 10, 11, "手动");
        _database.UpdateArchiveState(
            id,
            VideoArchiveStatus.Verified,
            contentSha256: "h",
            completedAt: end);
        return id;
    }

    private static StorageVolumeInfo Volume(long available) =>
        new("C:\\", 100, available, "");

    private static Func<string, StorageVolumeInfo?> Sequence(
        params StorageVolumeInfo?[] values)
    {
        int index = 0;
        return _ => index < values.Length
            ? values[index++]
            : values[^1];
    }

    [Fact]
    public void RunForRoot_StopsWhenVolumeRecoversAndDoesNotOverDelete()
    {
        DateTime now = DateTime.Now;
        long older = InsertVerified(
            "a.mp4",
            now.AddHours(-3),
            now.AddHours(-2),
            createLocal: true,
            createNas: true);
        long newer = InsertVerified(
            "b.mp4",
            now.AddHours(-2),
            now.AddHours(-1),
            createLocal: true,
            createNas: true);
        var service = new NasCircularCleanupService(
            _database,
            volumeReader: Sequence(Volume(8), Volume(13), Volume(13)),
            providerFactory: () => new NasArchiveProvider());

        bool deletedAny = service.RunForRoot(_nasRoot, reserveBytes: 10);

        Assert.True(deletedAny);
        Assert.Equal(
            VideoArchiveStatus.NasDeleted,
            _database.GetVideoById(older)!.ArchiveStatus);
        Assert.Equal(
            VideoArchiveStatus.Verified,
            _database.GetVideoById(newer)!.ArchiveStatus);
        Assert.False(File.Exists(_database.GetVideoById(older)!.ArchivePath));
        Assert.True(File.Exists(_database.GetVideoById(newer)!.ArchivePath));

        // 第二轮空间已恢复：不再删除任何文件
        var recoveredService = new NasCircularCleanupService(
            _database,
            volumeReader: _ => Volume(13),
            providerFactory: () => new NasArchiveProvider());
        Assert.False(recoveredService.RunForRoot(_nasRoot, reserveBytes: 10));
        Assert.Equal(
            VideoArchiveStatus.Verified,
            _database.GetVideoById(newer)!.ArchiveStatus);
    }

    [Fact]
    public void RunForRoot_VolumeReadFailure_StopsRound()
    {
        DateTime now = DateTime.Now;
        long first = InsertVerified(
            "c.mp4",
            now.AddHours(-3),
            now.AddHours(-2),
            createLocal: true,
            createNas: true);
        long second = InsertVerified(
            "d.mp4",
            now.AddHours(-2),
            now.AddHours(-1),
            createLocal: true,
            createNas: true);
        var service = new NasCircularCleanupService(
            _database,
            volumeReader: Sequence(Volume(8), null),
            providerFactory: () => new NasArchiveProvider());

        bool deletedAny = service.RunForRoot(_nasRoot, reserveBytes: 10);

        Assert.True(deletedAny);
        Assert.Equal(
            VideoArchiveStatus.NasDeleted,
            _database.GetVideoById(first)!.ArchiveStatus);
        Assert.Equal(
            VideoArchiveStatus.Verified,
            _database.GetVideoById(second)!.ArchiveStatus);
        Assert.True(File.Exists(_database.GetVideoById(second)!.ArchivePath));
    }

    [Fact]
    public void RunForRoot_ConfirmedMissing_ReconcilesWithoutDelete()
    {
        DateTime now = DateTime.Now;
        long id = InsertVerified(
            "e.mp4",
            now.AddHours(-3),
            now.AddHours(-2),
            createLocal: true,
            createNas: false);
        var service = new NasCircularCleanupService(
            _database,
            volumeReader: _ => Volume(8),
            providerFactory: () => new NasArchiveProvider(),
            probe: (_, _, _) => RemoteFileProbe.FileProbeState.ConfirmedMissing);

        bool deletedAny = service.RunForRoot(_nasRoot, reserveBytes: 10);

        Assert.False(deletedAny); // 只对账、未删除任何文件
        Assert.Equal(
            VideoArchiveStatus.NasDeleted,
            _database.GetVideoById(id)!.ArchiveStatus);
        Assert.False(_database.GetVideoById(id)!.IsDeleted);
        Assert.DoesNotContain(
            _database.GetPendingArchives(20, DateTime.Now),
            record => record.Id == id);
    }

    [Fact]
    public void RunForRoot_Unavailable_KeepsStateAndSkips()
    {
        DateTime now = DateTime.Now;
        string archiveFile = Path.Combine(_nasRoot, "target.mp4");
        File.WriteAllText(archiveFile, "12345678901");
        long id = InsertVerified(
            "f.mp4",
            now.AddHours(-3),
            now.AddHours(-2),
            createLocal: true,
            createNas: false,
            archivePathOverride: archiveFile);
        var service = new NasCircularCleanupService(
            _database,
            volumeReader: _ => Volume(8),
            providerFactory: () => new NasArchiveProvider(),
            probe: (_, _, _) => RemoteFileProbe.FileProbeState.Unavailable);

        bool deletedAny = service.RunForRoot(_nasRoot, reserveBytes: 10);

        Assert.False(deletedAny);
        Assert.Equal(
            VideoArchiveStatus.Verified,
            _database.GetVideoById(id)!.ArchiveStatus);
        Assert.True(File.Exists(archiveFile));
    }

    [Fact]
    public void RunForRoot_AboveReserve_DoesNotProbeOrDelete()
    {
        DateTime now = DateTime.Now;
        long id = InsertVerified(
            "g.mp4",
            now.AddHours(-3),
            now.AddHours(-2),
            createLocal: true,
            createNas: true);
        int providerCalls = 0;
        var service = new NasCircularCleanupService(
            _database,
            volumeReader: _ => Volume(20),
            providerFactory: () =>
            {
                providerCalls++;
                return new NasArchiveProvider();
            });

        Assert.False(service.RunForRoot(_nasRoot, reserveBytes: 10));
        Assert.Equal(0, providerCalls);
        Assert.Equal(
            VideoArchiveStatus.Verified,
            _database.GetVideoById(id)!.ArchiveStatus);
        Assert.True(File.Exists(_database.GetVideoById(id)!.ArchivePath));
    }

    [Fact]
    public async Task RunForRoot_SameRootSecondCallReturnsImmediately()
    {
        DateTime now = DateTime.Now;
        long id = InsertVerified(
            "h.mp4",
            now.AddHours(-3),
            now.AddHours(-2),
            createLocal: true,
            createNas: true);
        var gated = new GatedDeleteProvider();
        var service = new NasCircularCleanupService(
            _database,
            volumeReader: _ => Volume(8),
            providerFactory: () => gated);

        Task<bool> firstRun = Task.Run(() =>
            service.RunForRoot(_nasRoot, reserveBytes: 10));
        await gated.DeleteEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        bool secondRun = service.RunForRoot(_nasRoot, reserveBytes: 10);
        Assert.False(secondRun);

        gated.AllowDelete.TrySetResult(true);
        Assert.True(await firstRun);
        Assert.Equal(
            VideoArchiveStatus.NasDeleted,
            _database.GetVideoById(id)!.ArchiveStatus);
    }

    [Fact]
    public void RunReconcileBatch_OnlyConfirmedMissingRepaired()
    {
        DateTime now = DateTime.Now;
        long exists = InsertVerified(
            "i.mp4",
            now.AddHours(-4),
            now.AddHours(-3),
            createLocal: true,
            createNas: true);
        long missing = InsertVerified(
            "j.mp4",
            now.AddHours(-5),
            now.AddHours(-4),
            createLocal: true,
            createNas: false);
        long unavailable = InsertVerified(
            "k.mp4",
            now.AddHours(-6),
            now.AddHours(-5),
            createLocal: true,
            createNas: true);

        var service = new NasCircularCleanupService(
            _database,
            providerFactory: () => new NasArchiveProvider(),
            probe: (path, _, _) =>
                path.EndsWith("i.mp4", StringComparison.Ordinal)
                    ? RemoteFileProbe.FileProbeState.Exists
                    : path.EndsWith("j.mp4", StringComparison.Ordinal)
                        ? RemoteFileProbe.FileProbeState.ConfirmedMissing
                        : RemoteFileProbe.FileProbeState.Unavailable);

        int repaired = service.RunReconcileBatch(100);

        Assert.Equal(1, repaired);
        Assert.NotNull(_database.GetVideoById(exists)!.LastArchiveProbeAt);
        Assert.Equal(
            VideoArchiveStatus.NasDeleted,
            _database.GetVideoById(missing)!.ArchiveStatus);
        Assert.Equal(
            VideoArchiveStatus.Verified,
            _database.GetVideoById(unavailable)!.ArchiveStatus);
    }

    [Fact]
    public void DisasterChain_CleanupReconcileAndLocalGcKeepSystemRunning()
    {
        DateTime now = DateTime.Now;
        // 老 Verified（本地+NAS 都在）→ 应被容量清理为 NasDeleted
        long verified = InsertVerified(
            "v.mp4",
            now.AddHours(-8),
            now.AddHours(-7),
            createLocal: true,
            createNas: true);
        // 本地已清理、NAS 仍在 → LocalDeleted 候选，删除 NAS 后记录终态
        long localDeleted = InsertVerified(
            "l.mp4",
            now.AddHours(-7),
            now.AddHours(-6),
            createLocal: false,
            createNas: true);
        _database.MarkLocalCopyDeleted(localDeleted, "容量清理");
        // 管理员手动删除了 NAS 文件（本地仍在）→ 对账为 NasDeleted
        long adminDeleted = InsertVerified(
            "m.mp4",
            now.AddHours(-6),
            now.AddHours(-5),
            createLocal: true,
            createNas: false);
        // 正在归档的记录不受影响
        long copying = InsertVerified(
            "c2.mp4",
            now.AddHours(-2),
            now.AddHours(-1),
            createLocal: true,
            createNas: true);
        _database.UpdateArchiveState(
            copying,
            VideoArchiveStatus.Copying,
            attemptedAt: now);
        // 孤立文件（无 DB 记录）绝不能被删除
        string orphan = Path.Combine(_nasRoot, "2026-08-11", "orphan.mp4");
        File.WriteAllText(orphan, "12345678901");

        var service = new NasCircularCleanupService(
            _database,
            volumeReader: Sequence(Volume(8), Volume(9), Volume(10), Volume(10)),
            providerFactory: () => new NasArchiveProvider());

        bool deletedAny = service.RunForRoot(_nasRoot, reserveBytes: 10);

        Assert.True(deletedAny);
        Assert.Equal(
            VideoArchiveStatus.NasDeleted,
            _database.GetVideoById(verified)!.ArchiveStatus);
        Assert.True(
            _database.QueryVideos(null, null)
                .Single(record => record.Id == localDeleted)
                .IsDeleted);
        Assert.Equal(
            VideoArchiveStatus.NasDeleted,
            _database.GetVideoById(adminDeleted)!.ArchiveStatus);
        Assert.Equal(
            VideoArchiveStatus.Copying,
            _database.GetVideoById(copying)!.ArchiveStatus);
        Assert.True(File.Exists(orphan));
        Assert.True(File.Exists(_database.GetVideoById(copying)!.ArchivePath));
        Assert.DoesNotContain(
            _database.GetPendingArchives(20, DateTime.Now),
            record => record.Id == verified);
    }

    private sealed class GatedDeleteProvider : IArchiveProvider
    {
        private readonly NasArchiveProvider _inner = new();
        public TaskCompletionSource<bool> DeleteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AllowDelete { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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

        public Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken) =>
            _inner.ComputeSha256Async(path, cancellationToken);

        public async Task<IArchiveProvider.DeleteOutcome> DeleteAsync(
            string path,
            IReadOnlyList<string> allowedRoots,
            CancellationToken cancellationToken)
        {
            DeleteEntered.TrySetResult(true);
            await AllowDelete.Task.WaitAsync(cancellationToken);
            return await _inner.DeleteAsync(path, allowedRoots, cancellationToken);
        }

        public Task RenameAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            _inner.RenameAsync(sourcePath, destinationPath, cancellationToken);
    }
}
