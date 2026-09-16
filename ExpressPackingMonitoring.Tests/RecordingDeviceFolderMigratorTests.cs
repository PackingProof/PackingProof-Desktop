using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 老设备目录（设备-后六位 / 昵称-后六位）迁移到新命名（完整设备号）。
///
/// 数据库里的 FilePath 与 ArchivePath 是绝对路径，回放、导出、归档校验、删除都按它定位文件，
/// 所以"搬目录"必须和"回写路径"成对完成。这里守的是几条安全性质：
/// 不覆盖同名文件、不制造指向不存在文件的路径、可重复执行、撞号时不猜归属。
/// </summary>
public sealed class RecordingDeviceFolderMigratorTests : IDisposable
{
    private const string DeviceA = "11111111-1111-1111-1111-1111119abcdef";
    private const string DeviceB = "22222222-2222-2222-2222-222222123456";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"device-folder-migration-{Guid.NewGuid():N}");

    private string RecordingRoot => Path.Combine(_root, "recordings");

    private string NasRoot => Path.Combine(_root, "nas");

    [Fact]
    public void MovesLegacyDirectoryAndRewritesDatabasePaths()
    {
        using var database = CreateDatabase();
        string oldFile = CreateUploadFile(RecordingRoot, "手机备份", "设备-ABCDEF", "TRACK-001");
        string oldArchive = CreateUploadFile(NasRoot, "手机备份", "设备-ABCDEF", "TRACK-001");
        long id = InsertExternalRecord(database, DeviceA, oldFile, oldArchive);

        DeviceFolderMigrationSummary summary = CreateMigrator(database).Run();

        string newFile = ReplaceDeviceDirectory(oldFile, DeviceA);
        string newArchive = ReplaceDeviceDirectory(oldArchive, DeviceA);
        Assert.Equal(2, summary.MovedDirectories);
        Assert.Equal(1, summary.UpdatedRecords);
        Assert.True(File.Exists(newFile), "本地文件没搬到新目录");
        Assert.True(File.Exists(newArchive), "NAS 文件没搬到新目录");
        VideoRecord record = database.GetVideoById(id)!;
        Assert.Equal(newFile, record.FilePath);
        Assert.Equal(newArchive, record.ArchivePath);
    }

    /// <summary>更早那代目录名带昵称前缀，同样要按后六位认出来。</summary>
    [Fact]
    public void MovesNicknamePrefixedLegacyDirectory()
    {
        using var database = CreateDatabase();
        string oldFile = CreateUploadFile(RecordingRoot, "手机备份", "一号打包手机-ABCDEF", "TRACK-002");
        long id = InsertExternalRecord(database, DeviceA, oldFile, "");

        CreateMigrator(database).Run();

        string newFile = ReplaceDeviceDirectory(oldFile, DeviceA);
        Assert.True(File.Exists(newFile));
        Assert.Equal(newFile, database.GetVideoById(id)!.FilePath);
    }

    /// <summary>
    /// 重复执行不能出问题：第二轮应该什么都不做，路径也不能被改坏。
    /// 迁移在每次启动时都会跑，这条是底线。
    /// </summary>
    [Fact]
    public void IsIdempotent()
    {
        using var database = CreateDatabase();
        string oldFile = CreateUploadFile(RecordingRoot, "电脑上传", "设备-ABCDEF", "TRACK-003");
        long id = InsertExternalRecord(database, DeviceA, oldFile, "");
        RecordingDeviceFolderMigrator migrator = CreateMigrator(database);

        migrator.Run();
        string afterFirst = database.GetVideoById(id)!.FilePath;
        DeviceFolderMigrationSummary second = migrator.Run();

        Assert.Equal(0, second.MovedDirectories);
        Assert.Equal(0, second.UpdatedRecords);
        Assert.Equal(afterFirst, database.GetVideoById(id)!.FilePath);
        Assert.True(File.Exists(afterFirst));
    }

    /// <summary>
    /// 上次迁移到一半（新目录已存在）时按日期子目录合并，
    /// 同名文件一律跳过 —— 两边可能内容不同，覆盖就是数据丢失。
    /// </summary>
    [Fact]
    public void MergesIntoExistingTargetWithoutOverwriting()
    {
        using var database = CreateDatabase();
        string oldFile = CreateUploadFile(RecordingRoot, "手机备份", "设备-ABCDEF", "TRACK-004", "老内容");
        string existingTarget = CreateUploadFile(
            RecordingRoot,
            "手机备份",
            RecordingDeviceFolderNaming.BuildDirectoryName(DeviceA),
            "TRACK-004",
            "新目录里已有的内容");
        InsertExternalRecord(database, DeviceA, oldFile, "");

        CreateMigrator(database).Run();

        Assert.Equal("新目录里已有的内容", File.ReadAllText(existingTarget));
        Assert.True(File.Exists(oldFile), "同名冲突的源文件必须留在原地，不能被删");
    }

    /// <summary>
    /// 两台设备的后六位撞上时两边都不动：猜错归属会把两台设备的录像混进同一个目录。
    /// </summary>
    [Fact]
    public void SkipsDirectoriesWhenTwoDevicesShareTheSameShortId()
    {
        const string collidingDevice = "33333333-3333-3333-3333-3333339abcdef";
        using var database = CreateDatabase();
        string fileA = CreateUploadFile(RecordingRoot, "手机备份", "设备-ABCDEF", "TRACK-005");
        long idA = InsertExternalRecord(database, DeviceA, fileA, "");
        long idB = InsertExternalRecord(
            database,
            collidingDevice,
            CreateUploadFile(RecordingRoot, "手机备份", "另一台-ABCDEF", "TRACK-006"),
            "");

        DeviceFolderMigrationSummary summary = CreateMigrator(database).Run();

        Assert.Equal(0, summary.MovedDirectories);
        Assert.Equal(0, summary.UpdatedRecords);
        Assert.True(summary.SkippedDirectories > 0);
        Assert.True(File.Exists(fileA), "撞号时源文件必须留在原地");
        Assert.Equal(fileA, database.GetVideoById(idA)!.FilePath);
        Assert.Contains("ABCDEF", database.GetVideoById(idB)!.FilePath);
    }

    /// <summary>
    /// NAS 不可达时归档那一侧原样保留、本地照常迁移，
    /// 而且绝不能把 ArchivePath 改成一个不存在的路径。下次启动会补。
    /// </summary>
    [Fact]
    public void KeepsArchivePathWhenNasIsUnreachable()
    {
        using var database = CreateDatabase();
        string oldFile = CreateUploadFile(RecordingRoot, "手机备份", "设备-ABCDEF", "TRACK-007");
        string unreachableArchive = Path.Combine(
            @"\\unreachable-nas\share",
            "手机备份",
            "设备-ABCDEF",
            "2026-08-11",
            "TRACK-007.mp4");
        long id = InsertExternalRecord(database, DeviceA, oldFile, unreachableArchive);

        CreateMigrator(database).Run();

        VideoRecord record = database.GetVideoById(id)!;
        Assert.Equal(ReplaceDeviceDirectory(oldFile, DeviceA), record.FilePath);
        Assert.Equal(unreachableArchive, record.ArchivePath);
    }

    /// <summary>
    /// 目录已经搬了、数据库还没回写时断电：下一轮只做回写那半步。
    /// 这是整个迁移的自愈保证，不依赖额外的日志文件。
    /// </summary>
    [Fact]
    public void HealsDatabaseWhenDirectoryWasAlreadyMoved()
    {
        using var database = CreateDatabase();
        string movedFile = CreateUploadFile(
            RecordingRoot,
            "手机备份",
            RecordingDeviceFolderNaming.BuildDirectoryName(DeviceA),
            "TRACK-008");
        // 库里仍然是老路径，磁盘上文件已经在新目录 —— 断电现场就是这个样子。
        string stalePath = ReplaceDeviceDirectory(movedFile, DeviceA, "设备-ABCDEF");
        long id = InsertExternalRecord(database, DeviceA, stalePath, "");

        DeviceFolderMigrationSummary summary = CreateMigrator(database).Run();

        Assert.Equal(0, summary.MovedDirectories);
        Assert.Equal(1, summary.UpdatedRecords);
        Assert.Equal(movedFile, database.GetVideoById(id)!.FilePath);
    }

    /// <summary>
    /// 文件不在新位置（目录没搬成功）时绝不回写数据库：
    /// 那会把记录指向一个不存在的文件，回放和导出直接报"文件丢失"。
    /// </summary>
    [Fact]
    public void NeverWritesPathsThatDoNotExistYet()
    {
        Assert.Null(RecordingDeviceFolderMigrator.ResolveMigratedPath(
            Path.Combine(_root, "recordings", "手机备份", "设备-ABCDEF", "2026-08-11", "缺失.mp4"),
            DeviceA));
    }

    /// <summary>库里不认识的目录留在原地，不猜、不动。</summary>
    [Fact]
    public void LeavesUnknownDirectoriesAlone()
    {
        using var database = CreateDatabase();
        string strayFile = CreateUploadFile(RecordingRoot, "手机备份", "设备-999999", "TRACK-009");
        InsertExternalRecord(
            database,
            DeviceB,
            CreateUploadFile(RecordingRoot, "手机备份", "设备-123456", "TRACK-010"),
            "");

        CreateMigrator(database).Run();

        Assert.True(File.Exists(strayFile), "库里不认识的目录必须原样保留");
    }

    private RecordingDeviceFolderMigrator CreateMigrator(VideoDatabase database) =>
        new(database, () => [RecordingRoot, NasRoot]);

    private VideoDatabase CreateDatabase()
    {
        Directory.CreateDirectory(_root);
        return new VideoDatabase(Path.Combine(_root, "videos.db"));
    }

    private static long InsertExternalRecord(
        VideoDatabase database,
        string deviceId,
        string filePath,
        string archivePath)
    {
        // 自愈场景下库里记的是已经不存在的老路径，所以长度要容错取。
        long fileSizeBytes = File.Exists(filePath) ? new FileInfo(filePath).Length : 1;
        return database.InsertMobileBackupRecord(
            Path.GetFileNameWithoutExtension(filePath),
            filePath,
            fileSizeBytes,
            new DateTime(2026, 8, 11, 10, 30, 0),
            durationSeconds: 5,
            sourceDeviceId: deviceId,
            sourceDeviceName: "打包手机",
            sourceSessionId: $"session-{Guid.NewGuid():N}",
            contentSha256: Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            archivePath: archivePath);
    }

    private static string CreateUploadFile(
        string root,
        string category,
        string deviceDirectory,
        string trackingNumber,
        string content = "video")
    {
        string directory = Path.Combine(root, category, deviceDirectory, "2026-08-11");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{trackingNumber}.mp4");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>把路径里的设备目录换成指定名字（默认换成新格式）。</summary>
    private static string ReplaceDeviceDirectory(string path, string deviceId, string? directoryName = null)
    {
        string dateDirectory = Path.GetDirectoryName(path)!;
        string deviceDirectory = Path.GetDirectoryName(dateDirectory)!;
        return Path.Combine(
            Path.GetDirectoryName(deviceDirectory)!,
            directoryName ?? RecordingDeviceFolderNaming.BuildDirectoryName(deviceId),
            Path.GetFileName(dateDirectory),
            Path.GetFileName(path));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch
        {
            // 临时目录清理失败不影响断言结果。
        }
    }
}
