using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 小窗状态灯直接关系到店员能否判断"这一单到底录上没有"，
/// 这里锁定四种状态的判定边界。
/// </summary>
public sealed class FloatingPreviewStatusTests
{
    [Fact]
    public void Recording_ShowsOrderId()
    {
        FloatingPreviewStatus status = FloatingPreviewStatusPolicy.Evaluate(
            isRecording: true, preRecordEnabled: true, preRecordHasFrames: true, orderId: "JD0123456789");

        Assert.Equal(FloatingPreviewIndicator.Recording, status.Indicator);
        Assert.Equal("JD0123456789", status.Text);
        Assert.True(status.TextIsOrderId);
    }

    /// <summary>录制中但没抓到单号时不能只亮红灯不给字。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RecordingWithoutOrderId_FallsBackToRecordingText(string? orderId)
    {
        FloatingPreviewStatus status = FloatingPreviewStatusPolicy.Evaluate(
            isRecording: true, preRecordEnabled: false, preRecordHasFrames: false, orderId: orderId);

        Assert.Equal(FloatingPreviewIndicator.Recording, status.Indicator);
        Assert.Equal(FloatingPreviewStatusPolicy.RecordingWithoutOrderText, status.Text);
        Assert.False(status.TextIsOrderId);
    }

    [Fact]
    public void OrderIdIsTrimmed()
    {
        FloatingPreviewStatus status = FloatingPreviewStatusPolicy.Evaluate(
            isRecording: true, preRecordEnabled: false, preRecordHasFrames: false, orderId: "  JD9 ");

        Assert.Equal("JD9", status.Text);
    }

    [Fact]
    public void PreRecordBuffering_ShowsPreRecordingIndicator()
    {
        FloatingPreviewStatus status = FloatingPreviewStatusPolicy.Evaluate(
            isRecording: false, preRecordEnabled: true, preRecordHasFrames: true, orderId: null);

        Assert.Equal(FloatingPreviewIndicator.PreRecording, status.Indicator);
        Assert.Equal(FloatingPreviewStatusPolicy.PreRecordingText, status.Text);
        Assert.False(status.TextIsOrderId);
    }

    /// <summary>预录制关掉、或开着但缓冲还没画面，都不应假装在预录。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WithoutActivePreRecordBuffer_IsIdle(bool preRecordEnabled, bool preRecordHasFrames)
    {
        FloatingPreviewStatus status = FloatingPreviewStatusPolicy.Evaluate(
            isRecording: false, preRecordEnabled: preRecordEnabled, preRecordHasFrames: preRecordHasFrames, orderId: null);

        Assert.Equal(FloatingPreviewIndicator.Idle, status.Indicator);
        Assert.Equal(FloatingPreviewStatusPolicy.IdleText, status.Text);
    }

    /// <summary>录制状态优先级最高：正在录制时即使缓冲还在滚也必须显示录制中。</summary>
    [Fact]
    public void RecordingTakesPrecedenceOverPreRecording()
    {
        FloatingPreviewStatus status = FloatingPreviewStatusPolicy.Evaluate(
            isRecording: true, preRecordEnabled: true, preRecordHasFrames: true, orderId: null);

        Assert.Equal(FloatingPreviewIndicator.Recording, status.Indicator);
    }

    /// <summary>待机时残留的单号不能让状态灯变红。</summary>
    [Fact]
    public void IdleWithStaleOrderId_StaysIdle()
    {
        FloatingPreviewStatus status = FloatingPreviewStatusPolicy.Evaluate(
            isRecording: false, preRecordEnabled: false, preRecordHasFrames: false, orderId: "JD0123456789");

        Assert.Equal(FloatingPreviewIndicator.Idle, status.Indicator);
        Assert.Equal(FloatingPreviewStatusPolicy.IdleText, status.Text);
        Assert.False(status.TextIsOrderId);
    }

    /// <summary>
    /// 预录制开关必须读 EnableEventRecordingBuffer。
    /// AppConfig 会把 PreRecordSeconds 规范化清零，用它判断会让预录制灯永远不亮。
    /// </summary>
    [Fact]
    public void ViewModel_UsesEventRecordingBufferFlag_NotNormalizedPreRecordSeconds()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.FloatingPreview.cs"));

        int evaluateIndex = source.IndexOf("FloatingPreviewStatusPolicy.Evaluate", StringComparison.Ordinal);
        Assert.True(evaluateIndex >= 0, "未找到小窗状态求值调用");

        string evaluateCall = source[evaluateIndex..];
        int callEnd = evaluateCall.IndexOf(");", StringComparison.Ordinal);
        evaluateCall = callEnd >= 0 ? evaluateCall[..callEnd] : evaluateCall;

        Assert.Contains("EnableEventRecordingBuffer", evaluateCall, StringComparison.Ordinal);
        Assert.DoesNotContain("PreRecordSeconds", evaluateCall, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }
}
