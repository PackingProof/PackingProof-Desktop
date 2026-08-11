using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class StoragePathSelectionDialogTests
{
    [Theory]
    [InlineData(@"D:\录像", @"D:\录像")]
    [InlineData(@"\\192.168.1.100\共享\录像", @"\\192.168.1.100\共享\录像")]
    [InlineData(@"""D:\录像""", @"D:\录像")]
    [InlineData(@"D:\录像\", @"D:\录像")]
    public void TryNormalizePath_AcceptsQualifiedPaths(string input, string expected)
    {
        Assert.True(StoragePathSelectionDialog.TryNormalizePath(
            input,
            out string normalized,
            out _));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative\\path")]
    public void TryNormalizePath_RejectsEmptyOrRelativePaths(string input)
    {
        Assert.False(StoragePathSelectionDialog.TryNormalizePath(
            input,
            out _,
            out _));
    }
}
