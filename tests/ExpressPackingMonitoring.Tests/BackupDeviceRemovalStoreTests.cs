using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class BackupDeviceRemovalStoreTests
{
    [Fact]
    public void RemovedDevicesPersistAndCanBeRestored()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"backup-device-removals-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "backup-device-removals.json");
        try
        {
            DateTimeOffset removedAt = new(
                2026,
                11,
                19,
                8,
                30,
                0,
                TimeSpan.Zero);
            var store = new BackupDeviceRemovalStore(path, () => removedAt);

            Assert.True(store.MarkRemoved(" phone-removed "));
            Assert.True(store.MarkRemoved("phone-removed"));
            Assert.True(store.MarkRemovedRange(
                ["phone-batch-1", "phone-batch-2"]));

            var reloaded = new BackupDeviceRemovalStore(path);
            Assert.Contains(
                "phone-removed",
                reloaded.GetRemovedDeviceIds());
            Assert.Contains(
                "phone-batch-2",
                reloaded.GetRemovedDeviceIds());

            Assert.True(reloaded.Restore("PHONE-REMOVED"));
            Assert.True(reloaded.Restore("phone-batch-1"));
            Assert.True(reloaded.Restore("phone-batch-2"));
            Assert.Empty(
                new BackupDeviceRemovalStore(path).GetRemovedDeviceIds());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FailedWriteDoesNotLeaveInMemoryRemoval()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"backup-device-removals-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "backup-device-removals.json");
        try
        {
            Directory.CreateDirectory(path);
            var store = new BackupDeviceRemovalStore(path);

            Assert.False(store.MarkRemoved("phone-removed"));
            Assert.False(store.IsRemoved("phone-removed"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
