using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.ViewModels;
using System.Collections.Generic;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ArchiveBackupCardStateTests
{
    [Fact]
    public void BuildArchiveBackupCardState_PrecedenceAndTexts()
    {
        ArchiveBackupCardState uploading = MainViewModel.BuildArchiveBackupCardState(
            pendingCount: 1,
            uploadingCount: 2,
            failedCount: 1,
            pausedCount: 1,
            currentTarget: @"\\nas\share");
        Assert.Equal("备份中", uploading.ShortStatusText);
        Assert.Contains("共 5 个待备份", uploading.DetailText);

        ArchiveBackupCardState failed = MainViewModel.BuildArchiveBackupCardState(
            1,
            0,
            2,
            0,
            @"\\nas\share");
        Assert.Equal("备份失败", failed.ShortStatusText);
        Assert.Contains("等待重试", failed.DetailText);

        ArchiveBackupCardState paused = MainViewModel.BuildArchiveBackupCardState(
            1,
            0,
            0,
            3,
            @"\\nas\share");
        Assert.Equal("备份暂停", paused.ShortStatusText);
        Assert.Contains("等待空间恢复", paused.DetailText);

        ArchiveBackupCardState pending = MainViewModel.BuildArchiveBackupCardState(
            4,
            0,
            0,
            0,
            @"\\nas\share");
        Assert.Equal("待备份", pending.ShortStatusText);
        Assert.Contains("4 个录像等待备份到 NAS", pending.DetailText);

        ArchiveBackupCardState synced = MainViewModel.BuildArchiveBackupCardState(
            0,
            0,
            0,
            0,
            @"\\nas\share\sub\dir");
        Assert.Equal("已同步", synced.ShortStatusText);
        Assert.Contains("全部已备份 · \\\\nas", synced.DetailText);
        Assert.DoesNotContain("当前目标", synced.DetailText);
        Assert.DoesNotContain("sub\\dir", synced.DetailText);
    }

    [Fact]
    public void CompactArchiveTarget_KeepsOnlyServerHost()
    {
        Assert.Equal(
            @"\\192.168.1.100",
            MainViewModel.CompactArchiveTarget(
                @"\\192.168.1.100\NASSim\快递打包视频"));
        Assert.Equal(
            @"\\nas",
            MainViewModel.CompactArchiveTarget(@"\\nas\share"));
        Assert.Equal(
            @"D:\local\path",
            MainViewModel.CompactArchiveTarget(@"D:\local\path"));
    }

    [Fact]
    public void ShouldShowArchiveBackupCard_RequiresNonWorkstationAndNetworkLocation()
    {
        var config = new AppConfig
        {
            StorageLocations = new List<StorageLocation>()
        };

        Assert.False(MainViewModel.ShouldShowArchiveBackupCard(
            config,
            isRecordingWorkstation: false));

        config.StorageLocations.Add(new StorageLocation
        {
            Path = @"\\nas\share",
            Priority = 0
        });
        Assert.True(MainViewModel.ShouldShowArchiveBackupCard(
            config,
            isRecordingWorkstation: false));
        Assert.False(MainViewModel.ShouldShowArchiveBackupCard(
            config,
            isRecordingWorkstation: true));
    }
}
