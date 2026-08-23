using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionEnrollmentApprovalWindowTests
{
    [Fact]
    public void BuildAccessDescriptions_ExplainsCapabilitiesWithoutRawExecutionAccess()
    {
        var request = new ExtensionEnrollmentRequest
        {
            RequestedPermissions =
            [
                ExtensionPermissions.ScanTasksRead,
                ExtensionPermissions.ScanResultsWrite,
                ExtensionPermissions.RecordingFieldsWrite,
                ExtensionPermissions.RecordingsActiveRead
            ],
            RequestedCapabilities =
            [
                ExtensionScanCapabilities.OrderLookup,
                ExtensionScanCapabilities.MeasurementCapture
            ]
        };

        IReadOnlyList<string> descriptions =
            ExtensionEnrollmentApprovalWindow.BuildAccessDescriptions(request);

        Assert.Contains(descriptions, value => value.Contains("订单", StringComparison.Ordinal));
        Assert.Contains(descriptions, value => value.Contains("录像水印", StringComparison.Ordinal));
        Assert.Contains(descriptions, value => value.Contains("正在录像", StringComparison.Ordinal));
        Assert.DoesNotContain(descriptions, value => value.Contains("Shell", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(descriptions, value => value.Contains("数据库", StringComparison.Ordinal));
    }

    [Fact]
    public void Countdown_UsesCeilingAndExpiresAtZero()
    {
        Assert.True(ExtensionEnrollmentApprovalWindow.TryGetRemainingDisplaySeconds(
            TimeSpan.FromMilliseconds(1001),
            out int seconds));
        Assert.Equal(2, seconds);
        Assert.False(ExtensionEnrollmentApprovalWindow.TryGetRemainingDisplaySeconds(
            TimeSpan.Zero,
            out seconds));
        Assert.Equal(0, seconds);
    }
}
