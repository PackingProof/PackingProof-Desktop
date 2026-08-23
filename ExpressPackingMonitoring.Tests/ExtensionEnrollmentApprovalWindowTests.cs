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

        Assert.Contains(descriptions, value => ContainsLocalized(value, "订单", "order"));
        Assert.Contains(descriptions, value => ContainsLocalized(value, "录像水印", "watermark"));
        Assert.Contains(descriptions, value => ContainsLocalized(value, "正在录像", "active recording"));
        Assert.DoesNotContain(descriptions, value => value.Contains("Shell", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(descriptions, value => value.Contains("数据库", StringComparison.Ordinal));
        Assert.DoesNotContain(descriptions, value => value.Contains("database", StringComparison.OrdinalIgnoreCase));
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

    private static bool ContainsLocalized(string value, string chinese, string english) =>
        value.Contains(chinese, StringComparison.Ordinal)
        || value.Contains(english, StringComparison.OrdinalIgnoreCase);
}
