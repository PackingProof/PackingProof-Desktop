using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class HostUploadStorageAdmissionTests
{
    [Fact]
    public void HardDiskStartsWithTwoWritesAndRotatesDevicesAtChunkBoundaries()
    {
        var admission = new HostUploadStorageAdmissionController(
            _ => StorageWriteMediaKind.HardDisk);
        Reserve(admission, "a", "phone-a");
        Reserve(admission, "b", "phone-b");
        Reserve(admission, "c", "phone-c");
        Assert.Equal(2, admission.GetConcurrentWriteLimit("volume"));

        HostUploadWriteLease first = admission.AcquireWrite("a", "phone-a", "volume");
        HostUploadWriteLease second = admission.AcquireWrite("b", "phone-b", "volume");
        Assert.Throws<HostUploadStorageBusyException>(() =>
            admission.AcquireWrite("c", "phone-c", "volume"));
        Assert.Throws<HostUploadStorageBusyException>(() =>
            admission.AcquireWrite("a", "phone-a", "volume"));
        first.Dispose();

        HostUploadWriteLease third = admission.AcquireWrite("c", "phone-c", "volume");
        second.Dispose();
        third.Dispose();
    }

    [Fact]
    public void SolidStateDriveStartsWithFourWritesButNotTwoPerDevice()
    {
        var admission = new HostUploadStorageAdmissionController(
            _ => StorageWriteMediaKind.SolidState);
        Reserve(admission, "a1", "phone-a");
        Reserve(admission, "a2", "phone-a");
        Reserve(admission, "b", "phone-b");
        Reserve(admission, "c", "phone-c");
        Reserve(admission, "d", "phone-d");
        Reserve(admission, "e", "phone-e");
        Assert.Equal(4, admission.GetConcurrentWriteLimit("volume"));

        HostUploadWriteLease first = admission.AcquireWrite("a1", "phone-a", "volume");
        Assert.Throws<HostUploadStorageBusyException>(() =>
            admission.AcquireWrite("a2", "phone-a", "volume"));
        HostUploadWriteLease second = admission.AcquireWrite("b", "phone-b", "volume");
        HostUploadWriteLease third = admission.AcquireWrite("c", "phone-c", "volume");
        HostUploadWriteLease fourth = admission.AcquireWrite("d", "phone-d", "volume");
        Assert.Throws<HostUploadStorageBusyException>(() =>
            admission.AcquireWrite("e", "phone-e", "volume"));

        first.Dispose();
        second.Dispose();
        third.Dispose();
        fourth.Dispose();
    }

    [Fact]
    public void ReservationRejectsAggregateOversellAndTracksCommittedBytes()
    {
        var admission = new HostUploadStorageAdmissionController(
            _ => StorageWriteMediaKind.HardDisk);

        admission.ReserveUpload(
            "a",
            "phone-a",
            "volume",
            totalBytes: 600,
            receivedBytes: 0,
            availableBytes: 1000,
            reserveBytes: 100);
        Assert.Equal(600, admission.GetReservedBytes("volume"));

        Assert.Throws<HostUploadStorageBusyException>(() =>
            admission.ReserveUpload(
                "b",
                "phone-b",
                "volume",
                totalBytes: 500,
                receivedBytes: 0,
                availableBytes: 1000,
                reserveBytes: 100));

        admission.CommitChunk("a", 200);
        Assert.Equal(400, admission.GetReservedBytes("volume"));
        admission.ReserveUpload(
            "b",
            "phone-b",
            "volume",
            totalBytes: 500,
            receivedBytes: 0,
            availableBytes: 1000,
            reserveBytes: 100);
        Assert.Equal(900, admission.GetReservedBytes("volume"));
    }

    [Fact]
    public void UnknownMediaTypeFallsBackToHardDiskCapacity()
    {
        var admission = new HostUploadStorageAdmissionController(
            _ => StorageWriteMediaKind.Unknown);

        Assert.Equal(2, admission.GetConcurrentWriteLimit("volume"));
    }

    [Fact]
    public void ReservationDistinguishesDiskFullFromTemporaryBackpressure()
    {
        var admission = new HostUploadStorageAdmissionController(
            _ => StorageWriteMediaKind.HardDisk);

        Assert.Throws<MobileBackupStorageUnavailableException>(() =>
            admission.ReserveUpload(
                "a",
                "phone-a",
                "volume",
                totalBytes: 1200,
                receivedBytes: 0,
                availableBytes: 1000,
                reserveBytes: 100));
    }

    [Fact]
    public async Task TenClientsProgressWithinTheInitialAdaptiveWriteBudget()
    {
        var admission = new HostUploadStorageAdmissionController(
            _ => StorageWriteMediaKind.HardDisk);
        const int clients = 10;
        for (int index = 0; index < clients; index++)
        {
            Reserve(
                admission,
                $"upload-{index}",
                $"device-{index}",
                bytes: 5000);
        }

        int active = 0;
        int maxActive = 0;
        int completedChunks = 0;
        Task[] workers = Enumerable.Range(0, clients)
            .Select(index => Task.Run(async () =>
            {
                for (int chunk = 0; chunk < 5; chunk++)
                {
                    while (true)
                    {
                        try
                        {
                            using HostUploadWriteLease lease = admission.AcquireWrite(
                                $"upload-{index}",
                                $"device-{index}",
                                "volume");
                            int current = Interlocked.Increment(ref active);
                            InterlockedExtensions.Max(ref maxActive, current);
                            await Task.Delay(2);
                            admission.CommitChunk($"upload-{index}", 100);
                            Interlocked.Decrement(ref active);
                            Interlocked.Increment(ref completedChunks);
                            break;
                        }
                        catch (HostUploadStorageBusyException)
                        {
                            await Task.Delay(1);
                        }
                    }
                }
            }))
            .ToArray();

        await Task.WhenAll(workers);
        Assert.Equal(2, maxActive);
        Assert.Equal(clients * 5, completedChunks);
    }

    [Fact]
    public void PartitionsOnTheSamePhysicalDiskShareOneWriteBudget()
    {
        var admission = new HostUploadStorageAdmissionController(
            _ => StorageWriteMediaKind.HardDisk);
        Reserve(admission, "a", "phone-a", volume: "volume-c", physicalDisk: "disk:0");
        Reserve(admission, "b", "phone-b", volume: "volume-d", physicalDisk: "disk:0");
        Reserve(admission, "c", "phone-c", volume: "volume-e", physicalDisk: "disk:1");
        Reserve(admission, "d", "phone-d", volume: "volume-f", physicalDisk: "disk:0");

        HostUploadWriteLease first = admission.AcquireWrite(
            "a",
            "phone-a",
            "volume-c");
        HostUploadWriteLease second = admission.AcquireWrite(
            "b",
            "phone-b",
            "volume-d");
        Assert.Throws<HostUploadStorageBusyException>(() =>
            admission.AcquireWrite("d", "phone-d", "volume-f"));
        HostUploadWriteLease third = admission.AcquireWrite(
            "c",
            "phone-c",
            "volume-e");

        first.Dispose();
        second.Dispose();
        third.Dispose();
    }

    [Fact]
    public void ReleasingAnUploadRemovesItsStaleQueueEntry()
    {
        var admission = new HostUploadStorageAdmissionController(
            _ => StorageWriteMediaKind.HardDisk);
        Reserve(admission, "a", "phone-a");
        Reserve(admission, "b", "phone-b");
        Reserve(admission, "c", "phone-c");

        HostUploadWriteLease first = admission.AcquireWrite("a", "phone-a", "volume");
        HostUploadWriteLease second = admission.AcquireWrite("b", "phone-b", "volume");
        Assert.Throws<HostUploadStorageBusyException>(() =>
            admission.AcquireWrite("c", "phone-c", "volume"));
        Assert.Equal(1, admission.GetWriteLoad("volume").WaitingWriters);

        admission.ReleaseUpload("c");

        Assert.Equal(0, admission.GetWriteLoad("volume").WaitingWriters);
        first.Dispose();
        second.Dispose();
    }

    private static void Reserve(
        HostUploadStorageAdmissionController admission,
        string uploadId,
        string deviceId,
        long bytes = 1024,
        string volume = "volume",
        string? physicalDisk = null)
    {
        admission.ReserveUpload(
            uploadId,
            deviceId,
            volume,
            bytes,
            receivedBytes: 0,
            availableBytes: 1024L * 1024,
            reserveBytes: 0,
            physicalDiskKey: physicalDisk);
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            int current = Volatile.Read(ref target);
            while (value > current)
            {
                int observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }
}
