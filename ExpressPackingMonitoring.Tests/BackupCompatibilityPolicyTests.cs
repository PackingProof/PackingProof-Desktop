using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class BackupCompatibilityPolicyTests
{
    [Fact]
    public void HostCompatibilityPublishesCurrentBackupContract()
    {
        BackupCompatibilityInfo info = BackupCompatibilityPolicy.CreateHostInfo();

        Assert.Equal("mobile-backup-v2", info.Protocol);
        Assert.Equal(2, info.EnrollmentVersion);
        Assert.Equal(3, info.AuthVersion);
        Assert.Equal("0.5.10", info.MinimumMobileVersion);
        Assert.Equal(11010, info.MinimumMobileBuildNumber);
        Assert.Equal("0.0.32", info.MinimumWorkstationVersion);
        Assert.True(BackupCompatibilityPolicy.IsCompatibleHost(info));
    }

    [Theory]
    [InlineData("0.0.31", "mobile-backup-v2", 2, 3)]
    [InlineData("0.0.32", "mobile-backup-v1", 2, 3)]
    [InlineData("0.0.32", "mobile-backup-v2", 1, 3)]
    [InlineData("0.0.32", "mobile-backup-v2", 2, 2)]
    public void OlderOrMismatchedHostIsRejected(
        string hostVersion,
        string protocol,
        int enrollmentVersion,
        int authVersion)
    {
        var info = new BackupCompatibilityInfo
        {
            HostVersion = hostVersion,
            Protocol = protocol,
            EnrollmentVersion = enrollmentVersion,
            AuthVersion = authVersion
        };

        Assert.False(BackupCompatibilityPolicy.IsCompatibleHost(info));
    }

    [Fact]
    public void NewerCompatibleClientsAreAccepted()
    {
        var mobile = new BackupDeviceEnrollmentRequest
        {
            DeviceKind = "mobile",
            ClientVersion = "0.5.11",
            ClientBuildNumber = 11011,
            BackupProtocol = "mobile-backup-v2",
            EnrollmentVersion = 2,
            AuthVersion = 3
        };
        var workstation = new BackupDeviceEnrollmentRequest
        {
            DeviceKind = "pc",
            ClientVersion = "0.0.33",
            ClientBuildNumber = 0,
            BackupProtocol = "mobile-backup-v2",
            EnrollmentVersion = 2,
            AuthVersion = 3
        };

        Assert.Null(BackupCompatibilityPolicy.ValidateClient(mobile));
        Assert.Null(BackupCompatibilityPolicy.ValidateClient(workstation));
    }
}
