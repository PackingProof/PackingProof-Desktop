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
    public void PlaybackWindow_ShowsOneImportEntryAndDialogOwnsModeActions()
    {
        string xamlPath = FindRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "PlaybackWindow.xaml");
        string xaml = File.ReadAllText(xamlPath);
        string dialogXamlPath = FindRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "VideoImportDialog.xaml");
        string dialogXaml = File.ReadAllText(dialogXamlPath);
        string playbackCode = File.ReadAllText(FindRepositoryFile(
            "ExpressPackingMonitoring",
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
                    database.GetCompletedPcVideosForTransfer(DateTime.UtcNow.AddDays(-1)));

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
