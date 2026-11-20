using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class MobileBackupStorageAdmissionTests
{
    private const long GiB = 1024L * 1024L * 1024L;

    [Fact]
    public void CreateBackpressuresNewUploadWhenAggregateReservationsExceedUsableSpace()
    {
        string directory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var admission = new HostUploadStorageAdmissionController(
                _ => StorageWriteMediaKind.HardDisk);
            var service = new MobileBackupService(
                database,
                Path.Combine(directory, "state"),
                () => Path.Combine(directory, "recordings"),
                storageAdmission: admission,
                storageVolumeProbe: _ => new StorageVolumeInfo(
                    @"Z:\",
                    10 * GiB,
                    1700L * 1024 * 1024,
                    "test-volume"));

            service.CreateOrResume(Request("first", 600L * 1024 * 1024), "phone-a");

            Assert.Throws<HostUploadStorageBusyException>(() =>
                service.CreateOrResume(
                    Request("second", 600L * 1024 * 1024),
                    "phone-b"));
            Assert.Equal(600L * 1024 * 1024, admission.GetReservedBytes("test-volume"));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void StorageAvailabilityReportsBackpressureFromOutstandingReservations()
    {
        string directory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var admission = new HostUploadStorageAdmissionController(
                _ => StorageWriteMediaKind.HardDisk);
            var volume = new StorageVolumeInfo(
                @"Z:\",
                10 * GiB,
                4 * GiB,
                "test-volume");
            long reserveBytes = StorageSpacePolicy.CalculateMinimumReserveBytes(volume);
            long reservableBytes = volume.AvailableFreeSpace - reserveBytes;
            var service = new MobileBackupService(
                database,
                Path.Combine(directory, "state"),
                () => Path.Combine(directory, "recordings"),
                storageAdmission: admission,
                storageVolumeProbe: _ => volume);

            Assert.True(service.GetStorageAvailability().IsAvailable);
            service.CreateOrResume(Request("first", reservableBytes), "phone-a");

            HostUploadStorageAvailability status = service.GetStorageAvailability();
            Assert.False(status.IsAvailable);
            Assert.Equal("write_busy", status.State);
            Assert.InRange(status.RetryAfterSeconds, 1, 300);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void CompletedUploadReleasesGlobalReservation()
    {
        string directory = CreateTempDirectory();
        try
        {
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var admission = new HostUploadStorageAdmissionController(
                _ => StorageWriteMediaKind.HardDisk);
            var service = new MobileBackupService(
                database,
                Path.Combine(directory, "state"),
                () => Path.Combine(directory, "recordings"),
                storageAdmission: admission,
                storageVolumeProbe: _ => new StorageVolumeInfo(
                    @"Z:\",
                    10 * GiB,
                    5 * GiB,
                    "test-volume"));
            byte[] content = "storage reservation release"u8.ToArray();
            string sha = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

            service.CreateOrResume(
                new MobileBackupCreateRequest
                {
                    FileSha256 = sha,
                    TotalBytes = content.Length,
                    MimeType = "video/mp4"
                },
                "phone-a");
            service.AppendChunk(
                sha,
                0,
                content.Length - 1,
                content.Length,
                content,
                sha);
            service.Complete(
                sha,
                new MobileBackupCompleteRequest
                {
                    FileSha256 = sha,
                    SourceDeviceId = "phone-a",
                    SourceDeviceName = "手机A",
                    SourceDeviceKind = "mobile",
                    Sessions =
                    [
                        new MobileBackupSessionRequest
                        {
                            Id = "session-a",
                            TrackingNumber = "TRACK-A",
                            StartedAt = DateTimeOffset.Now.AddSeconds(-10),
                            EndedAt = DateTimeOffset.Now,
                            MediaStartMs = 0,
                            MediaEndMs = 10_000,
                            Mode = "发货"
                        }
                    ]
                });

            Assert.Equal(0, admission.GetReservedBytes("test-volume"));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void NewUploadsSpreadAcrossDistinctPhysicalDisksAndPersistTheirRoot()
    {
        string directory = CreateTempDirectory();
        try
        {
            string rootA = Directory.CreateDirectory(Path.Combine(directory, "disk-a")).FullName;
            string rootB = Directory.CreateDirectory(Path.Combine(directory, "disk-b")).FullName;
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var service = new MobileBackupService(
                database,
                Path.Combine(directory, "state"),
                () => rootA,
                storageVolumeProbe: path => VolumeFor(path, rootA, rootB),
                recordingRootsResolver: () => [rootA, rootB]);

            MobileBackupCreateRequest first = Request("first", 8 * 1024 * 1024);
            MobileBackupCreateRequest second = Request("second", 8 * 1024 * 1024);
            service.CreateOrResume(first, "phone-a");
            service.CreateOrResume(second, "phone-b");

            Assert.Equal(rootA, ReadUploadRoot(directory, first.FileSha256));
            Assert.Equal(rootB, ReadUploadRoot(directory, second.FileSha256));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void PartitionsOnOnePhysicalDiskDoNotActLikeAdditionalDisks()
    {
        string directory = CreateTempDirectory();
        try
        {
            string rootC = Directory.CreateDirectory(Path.Combine(directory, "disk-c")).FullName;
            string rootD = Directory.CreateDirectory(Path.Combine(directory, "disk-d")).FullName;
            string rootE = Directory.CreateDirectory(Path.Combine(directory, "disk-e")).FullName;
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var service = new MobileBackupService(
                database,
                Path.Combine(directory, "state"),
                () => rootC,
                storageVolumeProbe: path =>
                {
                    string root = Path.GetFullPath(path);
                    if (string.Equals(root, rootC, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(root, rootD, StringComparison.OrdinalIgnoreCase))
                    {
                        return new StorageVolumeInfo(
                            root,
                            10 * GiB,
                            5 * GiB,
                            $"volume-{Path.GetFileName(root)}",
                            "physical-disk:0");
                    }
                    return new StorageVolumeInfo(
                        root,
                        10 * GiB,
                        5 * GiB,
                        "volume-e",
                        "physical-disk:1");
                },
                recordingRootsResolver: () => [rootC, rootD, rootE]);

            MobileBackupCreateRequest first = Request("first", 8 * 1024 * 1024);
            MobileBackupCreateRequest second = Request("second", 8 * 1024 * 1024);
            service.CreateOrResume(first, "phone-a");
            service.CreateOrResume(second, "phone-b");

            Assert.Equal(rootC, ReadUploadRoot(directory, first.FileSha256));
            Assert.Equal(rootE, ReadUploadRoot(directory, second.FileSha256));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void RestartResumesPartialUploadOnItsOriginalDisk()
    {
        string directory = CreateTempDirectory();
        try
        {
            string rootA = Directory.CreateDirectory(Path.Combine(directory, "disk-a")).FullName;
            string rootB = Directory.CreateDirectory(Path.Combine(directory, "disk-b")).FullName;
            string stateDirectory = Path.Combine(directory, "state");
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            byte[] content = "resume-on-original-disk"u8.ToArray();
            MobileBackupCreateRequest request = new()
            {
                FileSha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant(),
                TotalBytes = content.Length,
                MimeType = "video/mp4"
            };

            var firstService = new MobileBackupService(
                database,
                stateDirectory,
                () => rootA,
                storageVolumeProbe: path => VolumeFor(path, rootA, rootB),
                recordingRootsResolver: () => [rootA, rootB]);
            firstService.CreateOrResume(request, "phone-a");
            firstService.AppendChunk(
                request.FileSha256,
                0,
                content.Length - 1,
                content.Length,
                content,
                request.FileSha256);

            var restarted = new MobileBackupService(
                database,
                stateDirectory,
                () => rootB,
                storageVolumeProbe: path => VolumeFor(path, rootA, rootB),
                recordingRootsResolver: () => [rootB, rootA]);
            MobileBackupCreateResult resumed =
                restarted.CreateOrResume(request, "phone-a");

            Assert.Equal(content.Length, resumed.Offset);
            Assert.Equal(rootA, ReadUploadRoot(directory, request.FileSha256));
            Assert.True(File.Exists(Path.Combine(
                rootA,
                MobileBackupService.StagingDirectoryName,
                $"{request.FileSha256}.part")));
            Assert.False(File.Exists(Path.Combine(
                rootB,
                MobileBackupService.StagingDirectoryName,
                $"{request.FileSha256}.part")));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void ResumeDoesNotMovePartialUploadWhenOriginalDiskIsMissing()
    {
        string directory = CreateTempDirectory();
        try
        {
            string rootA = Directory.CreateDirectory(Path.Combine(directory, "disk-a")).FullName;
            string rootB = Directory.CreateDirectory(Path.Combine(directory, "disk-b")).FullName;
            string stateDirectory = Path.Combine(directory, "state");
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            byte[] content = "do-not-move-partial"u8.ToArray();
            MobileBackupCreateRequest request = new()
            {
                FileSha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant(),
                TotalBytes = content.Length,
                MimeType = "video/mp4"
            };

            var firstService = new MobileBackupService(
                database,
                stateDirectory,
                () => rootA,
                storageVolumeProbe: path => VolumeFor(path, rootA, rootB),
                recordingRootsResolver: () => [rootA, rootB]);
            firstService.CreateOrResume(request, "phone-a");
            firstService.AppendChunk(
                request.FileSha256,
                0,
                content.Length - 1,
                content.Length,
                content,
                request.FileSha256);

            var restartedWithoutOriginalDisk = new MobileBackupService(
                database,
                stateDirectory,
                () => rootB,
                storageVolumeProbe: path => new StorageVolumeInfo(
                    rootB,
                    10 * GiB,
                    5 * GiB,
                    "volume-b",
                    "physical-disk:1"),
                recordingRootsResolver: () => [rootB]);
            Assert.Throws<MobileBackupStorageUnavailableException>(() =>
                restartedWithoutOriginalDisk.CreateOrResume(request, "phone-a"));
            Assert.True(File.Exists(Path.Combine(
                rootA,
                MobileBackupService.StagingDirectoryName,
                $"{request.FileSha256}.part")));
            Assert.False(Directory.Exists(Path.Combine(rootB, MobileBackupService.StagingDirectoryName)));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void ResumeRejectsSamePathWhenItNowPointsAtAnotherPhysicalDisk()
    {
        string directory = CreateTempDirectory();
        try
        {
            string root = Directory.CreateDirectory(Path.Combine(directory, "disk")).FullName;
            string stateDirectory = Path.Combine(directory, "state");
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            byte[] content = "rebound-volume"u8.ToArray();
            MobileBackupCreateRequest request = new()
            {
                FileSha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant(),
                TotalBytes = content.Length,
                MimeType = "video/mp4"
            };

            var firstService = new MobileBackupService(
                database,
                stateDirectory,
                () => root,
                storageVolumeProbe: _ => new StorageVolumeInfo(
                    root,
                    10 * GiB,
                    5 * GiB,
                    "volume-original",
                    "physical-disk:0"),
                recordingRootsResolver: () => [root]);
            firstService.CreateOrResume(request, "phone-a");
            firstService.AppendChunk(
                request.FileSha256,
                0,
                content.Length - 1,
                content.Length,
                content,
                request.FileSha256);

            var restarted = new MobileBackupService(
                database,
                stateDirectory,
                () => root,
                storageVolumeProbe: _ => new StorageVolumeInfo(
                    root,
                    10 * GiB,
                    5 * GiB,
                    "volume-remapped",
                    "physical-disk:1"),
                recordingRootsResolver: () => [root]);
            Assert.Throws<MobileBackupStorageUnavailableException>(() =>
                restarted.CreateOrResume(request, "phone-a"));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void CompletingAStoredSessionRejectsAMissingFile()
    {
        string directory = CreateTempDirectory();
        try
        {
            string root = Directory.CreateDirectory(Path.Combine(directory, "recordings")).FullName;
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            var service = new MobileBackupService(
                database,
                Path.Combine(directory, "state"),
                () => root,
                storageVolumeProbe: path => new StorageVolumeInfo(
                    root,
                    10 * GiB,
                    5 * GiB,
                    "volume-a",
                    "physical-disk:0"),
                recordingRootsResolver: () => [root]);
            byte[] content = "missing-completed-file"u8.ToArray();
            string sha = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
            service.CreateOrResume(
                new MobileBackupCreateRequest
                {
                    FileSha256 = sha,
                    TotalBytes = content.Length,
                    MimeType = "video/mp4"
                },
                "phone-a");
            service.AppendChunk(
                sha,
                0,
                content.Length - 1,
                content.Length,
                content,
                sha);
            var completeRequest = new MobileBackupCompleteRequest
            {
                FileSha256 = sha,
                SourceDeviceId = "phone-a",
                SourceDeviceName = "手机A",
                SourceDeviceKind = "mobile",
                Sessions =
                [
                    new MobileBackupSessionRequest
                    {
                        Id = "missing-file-session",
                        TrackingNumber = "MISSING-FILE",
                        StartedAt = DateTimeOffset.Now.AddSeconds(-10),
                        EndedAt = DateTimeOffset.Now,
                        MediaStartMs = 0,
                        MediaEndMs = 10_000,
                        Mode = "发货"
                    }
                ]
            };
            MobileBackupCompleteResult completed =
                service.Complete(sha, completeRequest);
            VideoRecord record = database.GetVideoBySourceSession(
                "phone-a",
                "missing-file-session")
                ?? throw new InvalidOperationException("complete record missing");
            File.Delete(completed.RecordId == record.Id
                ? record.FilePath
                : throw new InvalidOperationException("record mismatch"));

            MobileBackupValidationException error =
                Assert.Throws<MobileBackupValidationException>(() =>
                    service.Complete(sha, completeRequest));
            Assert.Equal("host_file_missing", error.ErrorCode);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void LegacyStateWithoutDiskIdentityCanResumeDirectly()
    {
        string directory = CreateTempDirectory();
        try
        {
            string root = Directory.CreateDirectory(Path.Combine(directory, "recordings")).FullName;
            string stateDirectory = Path.Combine(directory, "state");
            using var database = new VideoDatabase(Path.Combine(directory, "videos.db"));
            byte[] content = "legacy-direct-append"u8.ToArray();
            string sha = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
            var request = new MobileBackupCreateRequest
            {
                FileSha256 = sha,
                TotalBytes = content.Length,
                MimeType = "video/mp4"
            };
            var first = new MobileBackupService(
                database,
                stateDirectory,
                () => root,
                storageVolumeProbe: _ => new StorageVolumeInfo(
                    root,
                    10 * GiB,
                    5 * GiB,
                    "volume-a",
                    "physical-disk:0"),
                recordingRootsResolver: () => [root]);
            first.CreateOrResume(request, "phone-a");

            string statePath = Path.Combine(stateDirectory, $"{sha}.json");
            JsonObject state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
            state.Remove("uploadVolumeId");
            state.Remove("uploadPhysicalDiskId");
            File.WriteAllText(statePath, state.ToJsonString());
            File.WriteAllBytes(
                Path.Combine(stateDirectory, $"{sha}.part"),
                content[..8]);

            var restarted = new MobileBackupService(
                database,
                stateDirectory,
                () => root,
                storageVolumeProbe: _ => new StorageVolumeInfo(
                    root,
                    10 * GiB,
                    5 * GiB,
                    "volume-a",
                    "physical-disk:0"),
                recordingRootsResolver: () => [root]);
            byte[] remaining = content[8..];
            Assert.Equal(
                content.Length,
                restarted.AppendChunk(
                    sha,
                    8,
                    content.Length - 1,
                    content.Length,
                    remaining,
                    Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(remaining))
                        .ToLowerInvariant()));
            Assert.False(File.Exists(Path.Combine(stateDirectory, $"{sha}.part")));
            Assert.True(File.Exists(Path.Combine(
                root,
                MobileBackupService.StagingDirectoryName,
                $"{sha}.part")));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static StorageVolumeInfo VolumeFor(
        string path,
        string rootA,
        string rootB)
    {
        string root = Path.GetFullPath(path);
        if (string.Equals(root, rootA, StringComparison.OrdinalIgnoreCase))
        {
            return new StorageVolumeInfo(
                rootA,
                10 * GiB,
                5 * GiB,
                "volume-a",
                "physical-disk:0");
        }
        return new StorageVolumeInfo(
            rootB,
            10 * GiB,
            5 * GiB,
            "volume-b",
            "physical-disk:1");
    }

    private static string ReadUploadRoot(string directory, string sha)
    {
        string statePath = Path.Combine(directory, "state", $"{sha}.json");
        using JsonDocument state = JsonDocument.Parse(File.ReadAllText(statePath));
        return state.RootElement.GetProperty("uploadRoot").GetString() ?? "";
    }

    private static MobileBackupCreateRequest Request(string sha, long length) =>
        new()
        {
            FileSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(sha))).ToLowerInvariant(),
            TotalBytes = length,
            MimeType = "video/mp4"
        };

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"MobileBackupStorageAdmissionTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
