using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class RecordingFailureScanRecordTests
{
    [Fact]
    public void RecordingFailureBranch_ClearsActiveScanRecordState()
    {
        string code = LoadRecordingSource();

        int failureIndex = code.IndexOf(
            "MarkCurrentRecordingFailed(",
            StringComparison.Ordinal);
        Assert.True(failureIndex >= 0, "找不到录像失败分支");

        string failureBranch = code[failureIndex..];
        int nextMethod = failureBranch.IndexOf(
            "private void MarkCurrentRecordingFailed",
            StringComparison.Ordinal);
        if (nextMethod >= 0)
            failureBranch = failureBranch[..nextMethod];

        Assert.Contains(
            "presentation.IsEncoderFailure ? \"编码失败\" : \"录像写入失败\"",
            failureBranch,
            StringComparison.Ordinal);
        Assert.Contains("_currentScanRecord.IsActive = false", failureBranch);
        Assert.Contains("_currentScanRecord.SaveStatusText =", failureBranch);
        Assert.Contains("AppLanguage.Get(\"失败\")", failureBranch);
        Assert.Contains("_currentScanRecord = null", failureBranch);
    }

    [Fact]
    public void StartPath_DeactivatesLeftoverScanRecordBeforeCreatingNewOne()
    {
        string code = LoadRecordingSource();

        int startIndex = code.IndexOf(
            "_currentScanRecord = new ScanRecord(",
            StringComparison.Ordinal);
        Assert.True(startIndex >= 0, "找不到开始录像创建扫描记录的位置");

        string before = code[..startIndex];
        Assert.Contains("_currentScanRecord.IsActive = false", before);
        Assert.Contains("_currentScanRecord.SaveStatusText =", before);
        Assert.Contains("AppLanguage.Get(\"失败\")", before);
    }

    [Fact]
    public void FinalizeFailure_ReleasesActiveScanRecord()
    {
        var record = new ScanRecord(
            "SF-FINALIZE",
            "8s",
            "09:20:00",
            "发货",
            isActive: true,
            videoRecordId: 18);

        MainViewModel.ApplyFinalizeFailureState(record);

        Assert.False(record.IsActive);
        Assert.Equal(
            AppLanguage.Get("保存失败"),
            record.SaveStatusText);
    }

    [Fact]
    public void FinalizeExceptionPath_PublishesFailureStateOnUiDispatcher()
    {
        string code = LoadRecordingSource();

        int catchIndex = code.IndexOf(
            "WriteAudioDiagnostic($\"Finalize exception: {ex.Message}\"",
            StringComparison.Ordinal);
        Assert.True(catchIndex >= 0, "找不到录像定稿异常分支");

        string failureBranch = code[catchIndex..];
        int finallyIndex = failureBranch.IndexOf(
            "finally",
            StringComparison.Ordinal);
        Assert.True(finallyIndex >= 0);
        failureBranch = failureBranch[..finallyIndex];

        Assert.Contains(
            "Application.Current?.Dispatcher.InvokeAsync",
            failureBranch,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplyFinalizeFailureState(scanRecord)",
            failureBranch,
            StringComparison.Ordinal);
        Assert.Contains(
            "RefreshTodayStats()",
            failureBranch,
            StringComparison.Ordinal);
    }

    private static string LoadRecordingSource()
        => RepositorySource.ReadMainViewModelParts(
            "Recording",
            "Ffmpeg",
            "Audio",
            "Conversion");
}
