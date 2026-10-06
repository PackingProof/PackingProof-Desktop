using System.Text;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 退款状态可能在扫码开始时、打包过程中、或同码停录之后才拿到；
/// 一段录像里同一张面单只能报一次，同一条单号下次再扫仍然要报。
/// </summary>
public sealed class PrintedRefundAlertDedupTests
{
    [Fact]
    public void TryMark_AlertsOncePerSessionAndTrackingNumber()
    {
        var dedup = new PrintedRefundAlertDedup();

        Assert.True(dedup.TryMark("session-1", "SF1234567890"));
        Assert.False(dedup.TryMark("session-1", "SF1234567890"));

        // 下一次扫码（新的录像会话）还要提醒。
        Assert.True(dedup.TryMark("session-2", "SF1234567890"));
        // 同一段录像里的另一张面单互不影响。
        Assert.True(dedup.TryMark("session-1", "SF9999999999"));
    }

    [Fact]
    public void TryMark_NormalizesBareAndPackageCodesToTheSameWaybill()
    {
        var dedup = new PrintedRefundAlertDedup();

        Assert.True(dedup.TryMark("session-1", "JDX058278770023"));
        Assert.False(dedup.TryMark("session-1", "JDX058278770023-1-1-"));
        Assert.False(dedup.TryMark("session-1", " jdx058278770023 "));
    }

    [Fact]
    public void TryMark_AlertsAgainAfterRetentionWindow()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero));
        var dedup = new PrintedRefundAlertDedup(time, TimeSpan.FromMinutes(15));

        Assert.True(dedup.TryMark("session-1", "SF1234567890"));
        time.Advance(TimeSpan.FromMinutes(14));
        Assert.False(dedup.TryMark("session-1", "SF1234567890"));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(dedup.TryMark("session-1", "SF1234567890"));
    }

    [Theory]
    [InlineData("", "SF1234567890")]
    [InlineData("session-1", "")]
    [InlineData(null, null)]
    public void TryMark_WithoutSessionOrTrackingNumberNeverDeduplicates(string? sessionKey, string? trackingNumber)
    {
        var dedup = new PrintedRefundAlertDedup();

        Assert.True(dedup.TryMark(sessionKey, trackingNumber));
        Assert.True(dedup.TryMark(sessionKey, trackingNumber));
    }

    /// <summary>
    /// 同码停录是"这一单打包完成"的检查点，必须补一次退款核验；
    /// 订单信息到达时也要判一次，否则打包期间才发生的退款永远报不出来。
    /// </summary>
    [Fact]
    public void RefundVerification_CoversSameCodeStopAndLateOrderInfo()
    {
        string scanner = ReadProjectFile("ViewModels", "MainViewModel.Scanner.cs");
        string integration = ReadProjectFile("ViewModels", "MainViewModel.Integration.cs");

        Assert.Contains("VerifyRefundOnSameCodeStop(upperResult)", scanner, StringComparison.Ordinal);
        Assert.Contains("PublishExtensionScanTask(trackingNumber)", scanner, StringComparison.Ordinal);
        Assert.Contains("QueuePrintedRefundCheck(trackingNumber, _recordingMode ?? CurrentMode, sessionKey)", scanner, StringComparison.Ordinal);
        Assert.Contains("MaybeAlertPrintedRefundForActiveRecording(activeOrder)", integration, StringComparison.Ordinal);
        Assert.Contains("_printedRefundAlertDedup.TryMark(", scanner, StringComparison.Ordinal);
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

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
