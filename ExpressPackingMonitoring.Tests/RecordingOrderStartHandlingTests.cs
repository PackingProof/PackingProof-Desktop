using System.Text;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 手动“开始录制”曾经绕过重复单号提醒和订单信息提示：扫码识别的调用点后面才做这两件事，
/// 而手动开始录制直接走录像启动。现在所有入口统一在 InternalStartRecordingAsync 里处理。
/// </summary>
public sealed class RecordingOrderStartHandlingTests
{
    [Theory]
    [InlineData("435384812936683")]
    [InlineData("79037611278510")]
    [InlineData("JDX058278770023-1-1-")]
    [InlineData(" 79037611278510 ")]
    public void ShouldHandle_AcceptsRealOrderNumbers(string orderNumber) =>
        Assert.True(RecordingStartOrderPolicy.ShouldHandle(orderNumber));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("MAN_163415")]
    [InlineData(" man_163415 ")]
    public void ShouldHandle_SkipsManualPlaceholderAndEmptyInput(string? orderNumber) =>
        Assert.False(RecordingStartOrderPolicy.ShouldHandle(orderNumber));

    /// <summary>单号开始录像后的处理只能挂在录像启动路径上，否则手动开始录制又会漏掉提示。</summary>
    [Fact]
    public void OrderNumberHandling_IsInvokedFromTheRecordingStartPathOnly()
    {
        string recording = ReadProjectFile("ViewModels", "MainViewModel.Recording.cs");
        string scanner = ReadProjectFile("ViewModels", "MainViewModel.Scanner.cs");

        Assert.Contains("RecordingStartOrderPolicy.ShouldHandle(", recording, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(recording, "HandleOrderNumberRecordingStarted("));
        Assert.Contains("private void HandleOrderNumberRecordingStarted(", scanner, StringComparison.Ordinal);
        // 重复单号提醒只有统一处理方法会发，扫码调用点不再自己发一份。
        Assert.Equal(1, CountOccurrences(scanner, "duplicate-order-number:"));
        Assert.Equal(1, CountOccurrences(scanner, "OrderIdExistsRecent("));
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = source.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static string ReadProjectFile(params string[] relativePathParts) =>
        File.ReadAllText(
            Path.Combine([FindRepositoryRoot(), "ExpressPackingMonitoring", .. relativePathParts]),
            Encoding.UTF8);

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
