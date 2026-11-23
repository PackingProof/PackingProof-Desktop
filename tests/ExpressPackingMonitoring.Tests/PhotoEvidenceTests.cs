using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using Microsoft.Data.Sqlite;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

[Collection("WPF render tests")]
public sealed class PhotoEvidenceTests
{
    [Fact]
    public void DisabledCapabilityDoesNotPrimeArchiveOrInjectPhotoStore()
    {
        string source = RepositorySource.ReadMainViewModelParts(
            "Scanner",
            "Integration",
            "Photo");

        Assert.Contains("if (!Capabilities.CanOperatePhoto)", source, StringComparison.Ordinal);
        Assert.Contains(
            "photoStore: effectiveCapabilities.Features.PhotoCapture",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Capabilities.CanOperatePhoto &&",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("public bool ShowPhotoCaptureButton { get; set; } = true", source);
    }

    [Fact]
    public void StorePersistsSearchesAndSoftDeletesPhotos()
    {
        using var fixture = new TempFixture();
        string photoPath = Path.Combine(fixture.Root, "photo.jpg");
        File.WriteAllBytes(photoPath, [0xFF, 0xD8, 0xFF, 0xD9]);
        var store = new PhotoStore(fixture.DatabasePath);

        PhotoRecord record = store.Add(
            "SF10001",
            "发货",
            photoPath,
            "device-1",
            "电脑1",
            1920,
            1080,
            4,
            DateTimeOffset.UtcNow);

        Assert.Single(store.QueryPage("10001", 0, 20).Items);
        Assert.Empty(store.QueryPage("NO-MATCH", 0, 20).Items);
        Assert.True(store.MarkDeleted(record.Id, "UserRequested"));
        Assert.Null(store.Get(record.Id));
        PhotoRecord deleted = Assert.IsType<PhotoRecord>(
            store.Get(record.Id, includeDeleted: true));
        Assert.True(deleted.IsDeleted);
        Assert.Equal("UserRequested", deleted.DeleteReason);
    }

    [Fact]
    public void QueryPageFiltersAndPagesBeyondLegacyTwoHundredLimit()
    {
        using var fixture = new TempFixture();
        var store = new PhotoStore(fixture.DatabasePath);
        DateTimeOffset now = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
        for (int index = 0; index < 205; index++)
        {
            store.Add(
                $"SF{index:D5}",
                index % 2 == 0 ? "发货" : "退货",
                Path.Combine(fixture.Root, $"photo-{index:D3}.jpg"),
                index % 2 == 0 ? "device-a" : "device-b",
                index % 2 == 0 ? "电脑A" : "电脑B",
                1920,
                1080,
                1024,
                now.AddMinutes(-index));
        }

        PhotoQueryPage firstPage = store.QueryPage("", 0, 50);
        PhotoQueryPage lastPage = store.QueryPage("", 200, 50);
        PhotoQueryPage returnsForDeviceB = store.QueryPage(
            "",
            0,
            20,
            mode: RecordingModeFilter.Return,
            sourceId: "device-b");
        PhotoQueryPage filteredDate = store.QueryPage(
            "",
            0,
            50,
            startInclusive: now.AddMinutes(-20),
            endExclusive: now.AddMinutes(-10));

        Assert.Equal(205, firstPage.Total);
        Assert.Equal(50, firstPage.Items.Count);
        Assert.Equal(5, lastPage.Items.Count);
        Assert.Equal(102, returnsForDeviceB.Total);
        Assert.All(
            returnsForDeviceB.Items,
            record => Assert.Equal("退货", record.Mode));
        Assert.Equal(10, filteredDate.Total);
    }

    [Fact]
    public async Task CaptureWritesLocalAndArchiveFilesAtomically()
    {
        using var fixture = new TempFixture();
        string localRoot = Path.Combine(fixture.Root, "local");
        string archiveRoot = Path.Combine(fixture.Root, "archive");
        var store = new PhotoStore(fixture.DatabasePath);
        var service = new PhotoCaptureService(
            store,
            () => localRoot,
            () => archiveRoot);

        PhotoRecord record = await service.CaptureAsync(
            CreateFrame(),
            "顺丰20002",
            "退货",
            "device-2",
            "电脑2",
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(record.FilePath));
        Assert.EndsWith(".jpg", record.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Path.GetFileName(record.FilePath),
            character => char.IsWhiteSpace(character) || character > 127);
        Assert.Contains("_Return.jpg", record.FilePath);
        PhotoRecord archived = await WaitForArchiveAsync(store, record.Id);
        Assert.Equal("Completed", archived.ArchiveStatus);
        Assert.True(File.Exists(archived.ArchivePath));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(localRoot, "*.partial", SearchOption.AllDirectories),
            path => path.Length > 0);

        await service.DeleteLocalPhotoAsync(
            record.Id,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(record.FilePath));
        Assert.Null(store.Get(record.Id));
        Assert.True(store.Get(record.Id, includeDeleted: true)?.IsDeleted);
        Assert.True(File.Exists(archived.ArchivePath));
    }

    [Fact]
    public async Task FullResolutionCameraFrameIsPreserved()
    {
        using var fixture = new TempFixture();
        var store = new PhotoStore(fixture.DatabasePath);
        var service = new PhotoCaptureService(
            store,
            () => Path.Combine(fixture.Root, "local"));
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.All(96));

        PhotoRecord record = await service.CaptureAsync(
            frame,
            "SF40004",
            "发货",
            "device-4",
            "电脑4",
            TestContext.Current.CancellationToken);

        Assert.Equal(1920, record.Width);
        Assert.Equal(1080, record.Height);
        byte[] bytes = await File.ReadAllBytesAsync(
            record.FilePath,
            TestContext.Current.CancellationToken);
        using var stream = new MemoryStream(bytes);
        var decoder = new JpegBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        Assert.Equal(1920, decoder.Frames[0].PixelWidth);
        Assert.Equal(1080, decoder.Frames[0].PixelHeight);
    }

    [Fact]
    public async Task ArchiveFailureRemainsVisibleAndRetriable()
    {
        using var fixture = new TempFixture();
        string archiveRoot = Path.Combine(fixture.Root, "archive-root-is-a-file");
        await File.WriteAllTextAsync(
            archiveRoot,
            "not a directory",
            TestContext.Current.CancellationToken);
        var store = new PhotoStore(fixture.DatabasePath);
        var service = new PhotoCaptureService(
            store,
            () => Path.Combine(fixture.Root, "local"),
            () => archiveRoot);

        PhotoRecord record = await service.CaptureAsync(
            CreateFrame(),
            "SF50005",
            "发货",
            "device-5",
            "电脑5",
            TestContext.Current.CancellationToken);
        PhotoRecord failed = await WaitForArchiveAsync(
            store,
            record.Id,
            "Failed",
            TimeSpan.FromSeconds(8));

        Assert.True(failed.ArchiveAttempts >= 3);
        Assert.False(string.IsNullOrWhiteSpace(failed.ArchiveError));
        Assert.True(File.Exists(record.FilePath));

        File.Delete(archiveRoot);
        Directory.CreateDirectory(archiveRoot);
        await service.RetryArchiveAsync(record.Id);
        PhotoRecord completed = Assert.IsType<PhotoRecord>(
            store.Get(record.Id));
        Assert.Equal("Completed", completed.ArchiveStatus);
        Assert.True(File.Exists(completed.ArchivePath));
    }

    [Fact]
    public async Task LocalOnlyResumeArchivesPendingPhoto()
    {
        using var fixture = new TempFixture();
        string localPath = Path.Combine(fixture.Root, "pending.jpg");
        await File.WriteAllBytesAsync(
            localPath,
            [0xFF, 0xD8, 0xFF, 0xD9],
            TestContext.Current.CancellationToken);
        var store = new PhotoStore(fixture.DatabasePath);
        PhotoRecord pending = store.Add(
            "SF60006",
            "发货",
            localPath,
            "device-6",
            "电脑6",
            2,
            2,
            4,
            DateTimeOffset.UtcNow,
            "",
            "Pending");
        var service = new PhotoCaptureService(
            store,
            () => Path.Combine(fixture.Root, "capture"),
            () => Path.Combine(fixture.Root, "archive"));

        await service.ResumePendingArchivesAsync();

        PhotoRecord completed = Assert.IsType<PhotoRecord>(
            store.Get(pending.Id));
        Assert.Equal("Completed", completed.ArchiveStatus);
    }

    [Fact]
    public async Task CaptureCleansPublishedFileWhenDatabaseWriteFails()
    {
        using var fixture = new TempFixture();
        string localRoot = Path.Combine(fixture.Root, "local");
        var store = new PhotoStore(fixture.DatabasePath);
        SqliteConnection.ClearAllPools();
        File.Delete(fixture.DatabasePath);
        Directory.CreateDirectory(fixture.DatabasePath);
        var service = new PhotoCaptureService(store, () => localRoot);

        await Assert.ThrowsAnyAsync<Exception>(() => service.CaptureAsync(
            CreateFrame(),
            "SF30003",
            "发货",
            "device-3",
            "电脑3",
            TestContext.Current.CancellationToken));

        Assert.Empty(Directory.Exists(localRoot)
            ? Directory.EnumerateFiles(localRoot, "*.jpg", SearchOption.AllDirectories)
            : []);
    }

    private static BitmapSource CreateFrame()
    {
        int stride = 2 * 4;
        byte[] pixels =
        [
            0x20, 0x60, 0xF0, 0xFF, 0x30, 0x70, 0xE0, 0xFF,
            0x40, 0x80, 0xD0, 0xFF, 0x50, 0x90, 0xC0, 0xFF
        ];
        BitmapSource frame = BitmapSource.Create(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);
        frame.Freeze();
        return frame;
    }

    private static async Task<PhotoRecord> WaitForArchiveAsync(
        PhotoStore store,
        long id,
        string expectedStatus = "Completed",
        TimeSpan? timeout = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow +
            (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTimeOffset.UtcNow < deadline)
        {
            PhotoRecord? record = store.Get(id);
            if (record?.ArchiveStatus == expectedStatus)
                return record;
            await Task.Delay(
                50,
                TestContext.Current.CancellationToken);
        }
        throw new TimeoutException(
            $"Photo archive did not reach {expectedStatus}");
    }

    private sealed class TempFixture : IDisposable
    {
        internal TempFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "packingproof-photo-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            DatabasePath = Path.Combine(Root, "videos.db");
        }

        internal string Root { get; }
        internal string DatabasePath { get; }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
