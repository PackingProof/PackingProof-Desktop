using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class RecordingFailureScanRecordTests
{
    [Fact]
    public void RecordingFailureBranch_ClearsActiveScanRecordState()
    {
        string code = LoadRecordingSource();

        int failureIndex = code.IndexOf(
            "MarkCurrentRecordingFailed(\"编码失败\"",
            StringComparison.Ordinal);
        Assert.True(failureIndex >= 0, "找不到编码失败分支");

        string failureBranch = code[failureIndex..];
        int nextMethod = failureBranch.IndexOf(
            "private void MarkCurrentRecordingFailed",
            StringComparison.Ordinal);
        if (nextMethod >= 0)
            failureBranch = failureBranch[..nextMethod];

        Assert.Contains("_currentScanRecord.IsActive = false", failureBranch);
        Assert.Contains("_currentScanRecord.Duration = \"失败\"", failureBranch);
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
        Assert.Contains("_currentScanRecord.Duration = \"失败\"", before);
    }

    private static string LoadRecordingSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "ExpressPackingMonitoring",
                "ViewModels",
                "MainViewModel.Recording.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("找不到 MainViewModel.Recording.cs");
    }
}
