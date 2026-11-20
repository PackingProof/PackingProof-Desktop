using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class BackupDeviceMaintenanceStateStoreTests
{
    [Fact]
    public void LastSuccessfulCheckPersistsAcrossRestart()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"backup-device-maintenance-{Guid.NewGuid():N}");
        string path = Path.Combine(
            directory,
            "device-maintenance",
            "state.json");
        try
        {
            var store = new BackupDeviceMaintenanceStateStore(path);
            DateTimeOffset checkedAt = new(
                2026,
                11,
                18,
                10,
                30,
                0,
                TimeSpan.Zero);

            store.SaveCheckpoint(new BackupDeviceMaintenanceCheckpoint(
                checkedAt,
                NameSeedVersion: 1,
                LegacyRemovalImportVersion: 1));

            BackupDeviceMaintenanceCheckpoint? checkpoint =
                new BackupDeviceMaintenanceStateStore(path).LoadCheckpoint();
            Assert.NotNull(checkpoint);
            Assert.Equal(checkedAt, checkpoint.LastSuccessfulCheckUtc);
            Assert.Equal(1, checkpoint.NameSeedVersion);
            Assert.Equal(1, checkpoint.LegacyRemovalImportVersion);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void VersionTwoCheckpointKeepsNameSeedAndRequestsRemovalImport()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"backup-device-maintenance-{Guid.NewGuid():N}");
        string path = Path.Combine(
            directory,
            "device-maintenance",
            "state.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                """
                {
                  "Version": 2,
                  "LastSuccessfulCheckUtc": "2026-11-19T04:53:04.592159+00:00",
                  "NameSeedVersion": 1
                }
                """);

            BackupDeviceMaintenanceCheckpoint? checkpoint =
                new BackupDeviceMaintenanceStateStore(path).LoadCheckpoint();

            Assert.NotNull(checkpoint);
            Assert.Equal(1, checkpoint.NameSeedVersion);
            Assert.Equal(0, checkpoint.LegacyRemovalImportVersion);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
