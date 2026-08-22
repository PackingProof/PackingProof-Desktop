using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Services;
using System.Collections.Generic;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ArchiveBackupCardStateTests
{
    private static ArchiveQueueSummary Summary(
        int pending = 0,
        int uploading = 0,
        int failed = 0,
        int nasFull = 0,
        int localOnly = 0,
        int conflict = 0,
        int pendingVerification = 0,
        int lost = 0,
        int cleanedUnbacked = 0) =>
        new(pending, uploading, failed, nasFull, localOnly, conflict,
            pendingVerification, lost, cleanedUnbacked);

    [Fact]
    public void BuildArchiveBackupCardState_PrecedenceAndTexts()
    {
        ArchiveBackupCardState uploading = ArchiveBackupCardModel.BuildArchiveBackupCardState(
            Summary(pending: 1, uploading: 2, failed: 1, localOnly: 1),
            currentTarget: @"\\nas\share");
        Assert.Equal("备份中", uploading.ShortStatusText);
        Assert.Contains("剩余 5 个", uploading.DetailText);

        ArchiveBackupCardState failed = ArchiveBackupCardModel.BuildArchiveBackupCardState(
            Summary(pending: 1, failed: 2),
            @"\\nas\share");
        Assert.Equal("等待重试", failed.ShortStatusText);
        Assert.Contains("系统会自动重试", failed.DetailText);

        ArchiveBackupCardState paused = ArchiveBackupCardModel.BuildArchiveBackupCardState(
            Summary(pending: 1, nasFull: 3),
            @"\\nas\share");
        Assert.Equal("备份暂停", paused.ShortStatusText);
        Assert.Contains("等待空间恢复", paused.DetailText);

        ArchiveBackupCardState pending = ArchiveBackupCardModel.BuildArchiveBackupCardState(
            Summary(pending: 4),
            @"\\nas\share");
        Assert.Equal("待备份", pending.ShortStatusText);
        Assert.Contains("4 个录像等待备份到 NAS", pending.DetailText);

        ArchiveBackupCardState synced = ArchiveBackupCardModel.BuildArchiveBackupCardState(
            Summary(),
            @"\\nas\share\sub\dir");
        Assert.Equal("已同步", synced.ShortStatusText);
        Assert.Contains("全部已备份 · \\\\nas", synced.DetailText);
        Assert.DoesNotContain("当前目标", synced.DetailText);
        Assert.DoesNotContain("sub\\dir", synced.DetailText);
    }

    [Fact]
    public void BuildArchiveBackupCardState_FullPriorityMatrix()
    {
        ArchiveBackupCardState lost = ArchiveBackupCardModel.BuildArchiveBackupCardState(
            Summary(lost: 2, uploading: 1),
            @"\\nas\share");
        Assert.Equal("备份丢失", lost.ShortStatusText);
        Assert.Contains("2 个录像本地与 NAS 均无可信副本", lost.DetailText);

        ArchiveBackupCardState conflict = ArchiveBackupCardModel.BuildArchiveBackupCardState(
            Summary(pending: 1, conflict: 2, nasFull: 3),
            @"\\nas\share");
        Assert.Equal("备份异常", conflict.ShortStatusText);
        Assert.Contains("归档冲突", conflict.DetailText);

        ArchiveBackupCardState pendingVerification = ArchiveBackupCardModel.BuildArchiveBackupCardState(
            Summary(localOnly: 1, pendingVerification: 2),
            @"\\nas\share");
        Assert.Equal("待核实", pendingVerification.ShortStatusText);
        Assert.Contains("本地副本缺失", pendingVerification.DetailText);

        ArchiveBackupCardState localOnly = ArchiveBackupCardModel.BuildArchiveBackupCardState(
            Summary(localOnly: 5),
            @"\\nas\share");
        Assert.Equal("待备份", localOnly.ShortStatusText);
        Assert.Contains("5 个录像等待备份到 NAS", localOnly.DetailText);

        // 混合计数时 remaining 为总数
        ArchiveBackupCardState mixed = ArchiveBackupCardModel.BuildArchiveBackupCardState(
            Summary(
                pending: 1,
                uploading: 2,
                failed: 1,
                nasFull: 1,
                localOnly: 1,
                conflict: 1,
                pendingVerification: 1,
                lost: 1),
            @"\\nas\share");
        Assert.Equal("备份丢失", mixed.ShortStatusText);
    }

    [Fact]
    public void BuildArchiveBackupCardState_CleanedUnbackedIsNotSynced()
    {
        ArchiveBackupCardState cleaned = ArchiveBackupCardModel.BuildArchiveBackupCardState(
            Summary(cleanedUnbacked: 3),
            @"\\nas\share");
        Assert.Equal("已清理", cleaned.ShortStatusText);
        Assert.Contains("3 个录像已清理且未备份到 NAS", cleaned.DetailText);

        ArchiveBackupCardState synced = ArchiveBackupCardModel.BuildArchiveBackupCardState(
            Summary(),
            @"\\nas\share");
        Assert.Equal("已同步", synced.ShortStatusText);

        // 有真正剩余时按优先级显示，不因清理记录降到“已清理”
        ArchiveBackupCardState withRemaining = ArchiveBackupCardModel.BuildArchiveBackupCardState(
            Summary(pending: 1, cleanedUnbacked: 3),
            @"\\nas\share");
        Assert.Equal("待备份", withRemaining.ShortStatusText);
    }

    [Fact]
    public void BuildArchiveBackupCardState_UnavailableTarget_TakesPriorityAndShowsPath()
    {
        ArchiveBackupCardState unavailable =
            ArchiveBackupCardModel.BuildArchiveBackupCardState(
                Summary(pending: 1, failed: 500, lost: 2),
                @"\\nas\share",
                targetUnavailable: true,
                unavailableRoot: @"\\CloudDrive-Z-123456789\CloudDrive\百度网盘\快递打包视频");

        Assert.Equal("备份位置不可用", unavailable.ShortStatusText);
        Assert.Contains(
            @"\\CloudDrive-Z-123456789\CloudDrive",
            unavailable.DetailText);
        Assert.Contains("位置恢复后会自动重试", unavailable.DetailText);
    }

    [Fact]
    public void WorkerState_ShowsProgressWaitAndRecordingPriorityAheadOfRetry()
    {
        ArchiveQueueSummary summary = Summary(pending: 3, failed: 9000);
        ArchiveBackupCardState uploading =
            ArchiveBackupCardModel.BuildArchiveBackupCardState(
                summary,
                @"\\nas\share",
                worker: new ArchiveWorkerSnapshot(ArchiveWorkerPhase.Uploading));
        Assert.Equal("备份中", uploading.ShortStatusText);
        Assert.Contains("正在逐个备份", uploading.DetailText);

        DateTime now = new(2026, 8, 22, 3, 0, 0);
        ArchiveBackupCardState waiting =
            ArchiveBackupCardModel.BuildArchiveBackupCardState(
                summary,
                @"\\nas\share",
                worker: new ArchiveWorkerSnapshot(
                    ArchiveWorkerPhase.WaitingForNextBatch,
                    now.AddSeconds(5)),
                now: now);
        Assert.Equal("休息中", waiting.ShortStatusText);
        Assert.Contains("约 5 秒后继续", waiting.DetailText);

        ArchiveBackupCardState paused =
            ArchiveBackupCardModel.BuildArchiveBackupCardState(
                summary,
                @"\\nas\share",
                worker: new ArchiveWorkerSnapshot(
                    ArchiveWorkerPhase.PausedForRecording));
        Assert.Equal("录像优先", paused.ShortStatusText);
        Assert.Contains("停止录像后继续", paused.DetailText);
    }

    [Fact]
    public void ManualArchiveProblems_TakePriorityOverWorkerProgress()
    {
        ArchiveBackupCardState conflict =
            ArchiveBackupCardModel.BuildArchiveBackupCardState(
                Summary(uploading: 1, conflict: 1),
                @"\\nas\share",
                worker: new ArchiveWorkerSnapshot(ArchiveWorkerPhase.Uploading));
        Assert.Equal("备份异常", conflict.ShortStatusText);

        ArchiveBackupCardState full =
            ArchiveBackupCardModel.BuildArchiveBackupCardState(
                Summary(uploading: 1, nasFull: 1),
                @"\\nas\share",
                worker: new ArchiveWorkerSnapshot(ArchiveWorkerPhase.Uploading));
        Assert.Equal("备份暂停", full.ShortStatusText);
    }

    [Fact]
    public void CompactArchiveTarget_KeepsOnlyServerHost()
    {
        Assert.Equal(
            @"\\192.168.1.100",
            ArchiveBackupCardModel.CompactArchiveTarget(
                @"\\192.168.1.100\NASSim\快递打包视频"));
        Assert.Equal(
            @"\\nas",
            ArchiveBackupCardModel.CompactArchiveTarget(@"\\nas\share"));
        Assert.Equal(
            @"D:\local\path",
            ArchiveBackupCardModel.CompactArchiveTarget(@"D:\local\path"));
    }

    [Fact]
    public void ShouldShowArchiveBackupCard_RequiresNonWorkstationAndNetworkLocation()
    {
        var config = new AppConfig
        {
            StorageLocations = new List<StorageLocation>()
        };

        Assert.False(ArchiveBackupCardModel.ShouldShowArchiveBackupCard(
            config,
            isRecordingWorkstation: false));

        config.StorageLocations.Add(new StorageLocation
        {
            Path = @"\\nas\share",
            Priority = 0
        });
        Assert.True(ArchiveBackupCardModel.ShouldShowArchiveBackupCard(
            config,
            isRecordingWorkstation: false));
        Assert.False(ArchiveBackupCardModel.ShouldShowArchiveBackupCard(
            config,
            isRecordingWorkstation: true));
    }
}
