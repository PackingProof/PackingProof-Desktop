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
        Assert.Equal("0.5.23", info.MinimumMobileVersion);
        Assert.Equal(11036, info.MinimumMobileBuildNumber);
        Assert.Equal("0.0.55", info.MinimumWorkstationVersion);
        Assert.True(BackupCompatibilityPolicy.IsCompatibleHost(info));
    }

    [Theory]
    [InlineData("0.0.54", "mobile-backup-v2", 2, 3)]
    [InlineData("0.0.55", "mobile-backup-v1", 2, 3)]
    [InlineData("0.0.55", "mobile-backup-v2", 1, 3)]
    [InlineData("0.0.55", "mobile-backup-v2", 2, 2)]
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
            ClientVersion = "0.5.24",
            ClientBuildNumber = 11037,
            BackupProtocol = "mobile-backup-v2",
            EnrollmentVersion = 2,
            AuthVersion = 3
        };
        var workstation = new BackupDeviceEnrollmentRequest
        {
            DeviceKind = "pc",
            ClientVersion = "0.0.56",
            ClientBuildNumber = 0,
            BackupProtocol = "mobile-backup-v2",
            EnrollmentVersion = 2,
            AuthVersion = 3
        };

        Assert.Null(BackupCompatibilityPolicy.ValidateClient(mobile));
        Assert.Null(BackupCompatibilityPolicy.ValidateClient(workstation));
    }

    [Fact]
    public void PreviouslyReleasedV2ClientsAreRejectedBeforeEnrollment()
    {
        var mobile = new BackupDeviceEnrollmentRequest
        {
            DeviceKind = "mobile",
            ClientVersion = "0.5.23",
            ClientBuildNumber = 11035,
            BackupProtocol = "mobile-backup-v2",
            EnrollmentVersion = 2,
            AuthVersion = 3
        };
        var workstation = new BackupDeviceEnrollmentRequest
        {
            DeviceKind = "pc",
            ClientVersion = "0.0.54",
            BackupProtocol = "mobile-backup-v2",
            EnrollmentVersion = 2,
            AuthVersion = 3
        };

        Assert.Equal("mobile", BackupCompatibilityPolicy.ValidateClient(mobile)?.UpdateTarget);
        Assert.Equal("recording-workstation", BackupCompatibilityPolicy.ValidateClient(workstation)?.UpdateTarget);
    }

    [Fact]
    public void ViewerClientUsesItsOwnProtocolFloor()
    {
        var valid = new BackupDeviceEnrollmentRequest
        {
            DeviceKind = "viewer",
            ClientVersion = BackupCompatibilityPolicy.MinimumViewerVersion,
            ClientBuildNumber = 0,
            BackupProtocol = "mobile-backup-v2",
            EnrollmentVersion = 2,
            AuthVersion = 3
        };
        var outdated = new BackupDeviceEnrollmentRequest
        {
            DeviceKind = "viewer",
            ClientVersion = "0.0.48",
            ClientBuildNumber = 0,
            BackupProtocol = "mobile-backup-v2",
            EnrollmentVersion = 2,
            AuthVersion = 3
        };

        Assert.Null(BackupCompatibilityPolicy.ValidateClient(valid));
        BackupCompatibilityFailure? failure = BackupCompatibilityPolicy.ValidateClient(outdated);
        Assert.NotNull(failure);
        Assert.Equal("viewer", failure.UpdateTarget);
    }
}
