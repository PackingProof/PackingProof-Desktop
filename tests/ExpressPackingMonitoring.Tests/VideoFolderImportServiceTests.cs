using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VideoFolderImportServiceTests
{
    [Fact]
    public async Task ImportAsync_RecursesAndSkipsPathAndContentDuplicates()
    {
        string root = CreateTempDirectory();
        string databasePath = Path.Combine(root, "videos.db");
        string nested = Directory.CreateDirectory(Path.Combine(root, "2026-08-01", "old")).FullName;
        string first = Path.Combine(nested, "ORDER-1.mp4");
        string renamedDuplicate = Path.Combine(nested, "ORDER-1-copy.MP4");
        string second = Path.Combine(nested, "ORDER-2.mp4");
        File.WriteAllBytes(first, [1, 2, 3, 4]);
        File.WriteAllBytes(renamedDuplicate, [1, 2, 3, 4]);
        File.WriteAllBytes(second, [5, 6, 7]);
        File.WriteAllText(Path.Combine(nested, "ignore.mov"), "ignored");

        try
        {
            using (var database = new VideoDatabase(databasePath))
            {
                var service = CreateService(database, root);
                CancellationToken cancellationToken = TestContext.Current.CancellationToken;

                VideoImportResult firstRun = await service.ImportAsync(
                    root,
                    "发货",
                    progress: null,
                    cancellationToken);

                Assert.Equal(2, firstRun.Imported);
                Assert.Equal(1, firstRun.Skipped);
                Assert.Equal(0, firstRun.Failed);
                Assert.All(database.QueryVideos(null, null), record => Assert.Equal("发货", record.Mode));

                string newFile = Path.Combine(nested, "RETURN-1.mp4");
                File.WriteAllBytes(newFile, [8, 9, 10]);
                VideoImportResult secondRun = await service.ImportAsync(
                    root,
                    "退货",
                    progress: null,
                    cancellationToken);

                Assert.Equal(1, secondRun.Imported);
                Assert.Equal(3, secondRun.Skipped);
                VideoRecord importedReturn = Assert.Single(
                    database.QueryVideos(null, null),
                    record => record.OrderId == "RETURN-1");
                Assert.Equal("退货", importedReturn.Mode);
            Assert.Equal("导入", importedReturn.StopReason);
            Assert.False(string.IsNullOrWhiteSpace(importedReturn.ContentSha256));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(first));
            }
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(root);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_RelinksMissingRecordInsteadOfSkipping()
    {
        string root = CreateTempDirectory();
        string databasePath = Path.Combine(root, "videos.db");
        // 旧盘已经拔掉：这条记录还在，但它记的路径不存在了。
        string missingPath = Path.Combine(root, "old-disk", "ORDER-9.mp4");
        // 用户把原录像拷到新盘，并用新目录再次导入。
        string newDiskFolder = Directory.CreateDirectory(Path.Combine(root, "new-disk")).FullName;
        string movedFile = Path.Combine(newDiskFolder, "ORDER-9.mp4");
        File.WriteAllBytes(movedFile, [1, 2, 3, 4]);

        try
        {
            using var database = new VideoDatabase(databasePath);
            long recordId = database.InsertVideoRecord(
                "ORDER-9", "发货", "", "", missingPath, DateTime.Now.AddDays(-1));
            database.UpdateVideoRecordOnStop(
                recordId, DateTime.Now.AddDays(-1).AddMinutes(1), 30, 4, "手动");

            var service = CreateService(database, root);
            VideoImportResult result = await service.ImportAsync(
                newDiskFolder,
                "发货",
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, result.Imported);
            Assert.Equal(0, result.Skipped);
            Assert.Equal(0, result.Failed);
            // 没有插出重复记录：原来的那条被指到了新路径。
            VideoRecord relinked = Assert.Single(database.QueryVideos(null, null));
            Assert.Equal(recordId, relinked.Id);
            Assert.Equal(movedFile, relinked.FilePath);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(root);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_RelinksMissingRecordByContentHash()
    {
        string root = CreateTempDirectory();
        string databasePath = Path.Combine(root, "videos.db");
        string missingPath = Path.Combine(root, "old-disk", "ORDER-11.mp4");
        string newDiskFolder = Directory.CreateDirectory(Path.Combine(root, "new-disk")).FullName;
        string movedFile = Path.Combine(newDiskFolder, "ORDER-11.mp4");
        File.WriteAllBytes(movedFile, [21, 22, 23]);
        string sha256 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(movedFile))).ToLowerInvariant();

        try
        {
            using var database = new VideoDatabase(databasePath);
            // 记录带内容哈希、指向已经拔掉的旧盘：现场这次"全是跳过"最可能就是这条分支。
            Assert.True(database.TryInsertImportedVideoRecord(
                "ORDER-11",
                "发货",
                missingPath,
                3,
                DateTime.Now.AddDays(-3),
                30,
                sha256,
                "device-old",
                "旧电脑"));

            var service = CreateService(database, root);
            VideoImportResult result = await service.ImportAsync(
                newDiskFolder,
                "发货",
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, result.Imported);
            Assert.Equal(0, result.Skipped);
            VideoRecord relinked = Assert.Single(database.QueryVideos(null, null));
            Assert.Equal(movedFile, relinked.FilePath);
            Assert.Equal(sha256, relinked.ContentSha256);
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(root);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_RelinksMissingRecordBySameNameAndSize()
    {
        string root = CreateTempDirectory();
        string databasePath = Path.Combine(root, "videos.db");
        string missingPath = Path.Combine(root, "old-disk", "ORDER-10.mp4");
        string newDiskFolder = Directory.CreateDirectory(Path.Combine(root, "new-disk")).FullName;
        string movedFile = Path.Combine(newDiskFolder, "ORDER-10.mp4");
        File.WriteAllBytes(movedFile, [7, 8, 9, 10, 11]);

        try
        {
            using var database = new VideoDatabase(databasePath);
            // 早期录像没有内容哈希：这条兜底必须靠"同名同大小 + 原路径不可用"认出来。
            long recordId = database.InsertVideoRecord(
                "ORDER-10", "发货", "", "", missingPath, DateTime.Now.AddDays(-2));
            database.UpdateVideoRecordOnStop(
                recordId, DateTime.Now.AddDays(-2).AddMinutes(1), 30, 5, "手动");
            Assert.True(string.IsNullOrEmpty(database.GetVideoById(recordId)?.ContentSha256));

            var service = CreateService(database, root);
            VideoImportResult result = await service.ImportAsync(
                newDiskFolder,
                "发货",
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, result.Imported);
            Assert.Equal(0, result.Skipped);
            VideoRecord relinked = Assert.Single(database.QueryVideos(null, null));
            Assert.Equal(recordId, relinked.Id);
            Assert.Equal(movedFile, relinked.FilePath);
            // 找回时把缺失的内容哈希补上，之后再遇到同内容副本不必退到"同名同大小"。
            Assert.False(string.IsNullOrWhiteSpace(relinked.ContentSha256));
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(root);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 同名同大小的兜底等于"把记录指到别处"，所以只在唯一命中时才敢用：剩两条同名同大小的
    /// 缺失记录时，宁可按新录像插一条，也不能随手挑一条把它的路径改到这次导入的文件上。
    /// </summary>
    [Fact]
    public async Task ImportAsync_DoesNotRelinkWhenSameNameAndSizeIsAmbiguous()
    {
        string root = CreateTempDirectory();
        string databasePath = Path.Combine(root, "videos.db");
        string firstMissingPath = Path.Combine(root, "old-disk-a", "ORDER-11.mp4");
        string secondMissingPath = Path.Combine(root, "old-disk-b", "ORDER-11.mp4");
        string newDiskFolder = Directory.CreateDirectory(Path.Combine(root, "new-disk")).FullName;
        string movedFile = Path.Combine(newDiskFolder, "ORDER-11.mp4");
        File.WriteAllBytes(movedFile, [7, 8, 9, 10, 11]);

        try
        {
            using var database = new VideoDatabase(databasePath);
            foreach (string missingPath in new[] { firstMissingPath, secondMissingPath })
            {
                long recordId = database.InsertVideoRecord(
                    "ORDER-11", "发货", "", "", missingPath, DateTime.Now.AddDays(-2));
                database.UpdateVideoRecordOnStop(
                    recordId, DateTime.Now.AddDays(-2).AddMinutes(1), 30, 5, "手动");
            }

            var service = CreateService(database, root);
            VideoImportResult result = await service.ImportAsync(
                newDiskFolder,
                "发货",
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, result.Imported);
            Assert.Equal(0, result.Skipped);
            List<VideoRecord> records = database.QueryVideos(null, null);
            // 两条老记录都保持原样（谁都没被改到新盘上），新盘那份按新录像入账。
            Assert.Contains(records, record => record.FilePath == firstMissingPath);
            Assert.Contains(records, record => record.FilePath == secondMissingPath);
            Assert.Contains(
                records,
                record => string.Equals(record.FilePath, movedFile, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(root);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PlaybackWindow_ShowsOneImportEntryAndDialogOwnsModeActions()
    {
        string xamlPath = FindRepositoryFile(
            "src", "ExpressPackingMonitoring",
            "UI",
            "PlaybackWindow.xaml");
        string xaml = File.ReadAllText(xamlPath);
        string dialogXamlPath = FindRepositoryFile(
            "src", "ExpressPackingMonitoring",
            "UI",
            "VideoImportDialog.xaml");
        string dialogXaml = File.ReadAllText(dialogXamlPath);
        string playbackCode = File.ReadAllText(FindRepositoryFile(
            "src", "ExpressPackingMonitoring",
            "UI",
            "PlaybackWindow.xaml.cs"));

        Assert.Contains("x:Name=\"BtnImportVideos\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"导入录像\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("导入发货", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("导入退货", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("VideoImportProgressPanel", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("VideoImportStatusText", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"导入发货\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"导入退货\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"取消\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("仅支持 MP4", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Row=\"2\"", dialogXaml, StringComparison.Ordinal);
        Assert.Contains("AppDialog.ShowMessage(", playbackCode, StringComparison.Ordinal);
        Assert.Contains("result.Cancelled ? \"导入已停止\" : \"导入完成\"", playbackCode, StringComparison.Ordinal);
        Assert.DoesNotContain("开始导入", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("开始导入", dialogXaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAsync_RejectsFolderOutsideManagedRoot()
    {
        string parent = CreateTempDirectory();
        string root = Directory.CreateDirectory(Path.Combine(parent, "videos")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(parent, "videos-other")).FullName;
        try
        {
            using (var database = new VideoDatabase(Path.Combine(parent, "videos.db")))
            {
                var service = CreateService(database, root);

                InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => service.ImportAsync(
                        outside,
                        "发货",
                        null,
                        TestContext.Current.CancellationToken));

                Assert.Contains("不在程序管理", error.Message, StringComparison.Ordinal);
                Assert.False(VideoFolderImportService.IsPathWithinRoot(root, outside));
                Assert.True(VideoFolderImportService.IsPathWithinRoot(root, root));
            }
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(parent);
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void ImportedVideoBeforeActivation_IsStillEligibleForWorkstationTransfer()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "old.mp4");
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            using (var database = new VideoDatabase(Path.Combine(root, "videos.db")))
            {
                Assert.True(database.TryInsertImportedVideoRecord(
                    "OLD",
                    "发货",
                    path,
                    3,
                    DateTime.Now.AddYears(-1),
                    10,
                    new string('a', 64),
                    "device-1",
                    "电脑1"));

                VideoRecord record = Assert.Single(
                    database.GetCompletedPcVideosForTransfer());

                Assert.Equal("导入", record.StopReason);
            }
        }
        finally
        {
            SqliteTestPool.ClearPoolFor(root);
            Directory.Delete(root, recursive: true);
        }
    }

    private static VideoFolderImportService CreateService(VideoDatabase database, string root) =>
        new(
            database,
            [root],
            "device-1",
            "电脑1",
            _ => new ImportedVideoMetadata(12));

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"epm-video-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            string candidate = Path.Combine([current.FullName, .. relativeParts]);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, relativeParts));
    }
}
