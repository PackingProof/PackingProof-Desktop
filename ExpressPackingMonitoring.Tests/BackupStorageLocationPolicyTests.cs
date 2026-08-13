using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class BackupStorageLocationPolicyTests
{
    [Fact]
    public void Network_IsAccepted()
    {
        Assert.Equal(
            BackupStorageDecision.Accept,
            BackupStorageLocationPolicy.Evaluate(
                @"\\nas\share\快递打包视频",
                _ => StorageVolumeInfo.StorageLocationKind.Network));
    }

    [Fact]
    public void VirtualDisk_RequiresConfirmation()
    {
        Assert.Equal(
            BackupStorageDecision.ConfirmVirtualDisk,
            BackupStorageLocationPolicy.Evaluate(
                @"Z:\快递打包视频",
                _ => StorageVolumeInfo.StorageLocationKind.VirtualDisk));
    }

    [Fact]
    public void Unknown_RequiresConfirmation()
    {
        Assert.Equal(
            BackupStorageDecision.ConfirmUnknown,
            BackupStorageLocationPolicy.Evaluate(
                @"Z:\快递打包视频",
                _ => StorageVolumeInfo.StorageLocationKind.Unknown));
    }

    [Fact]
    public void PhysicalLocal_IsRejected()
    {
        Assert.Equal(
            BackupStorageDecision.RejectPhysicalLocal,
            BackupStorageLocationPolicy.Evaluate(
                @"D:\快递打包视频",
                _ => StorageVolumeInfo.StorageLocationKind.Local));
    }
}
