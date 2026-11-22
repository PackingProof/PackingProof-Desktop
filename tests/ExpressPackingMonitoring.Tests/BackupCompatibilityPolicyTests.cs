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
    [InlineData("mobile-backup-v1", 2, 3)]
    [InlineData("mobile-backup-v2", 1, 3)]
    [InlineData("mobile-backup-v2", 2, 2)]
    public void HostWithMismatchedProtocolIsRejected(
        string protocol,
        int enrollmentVersion,
        int authVersion)
    {
        var info = new BackupCompatibilityInfo
        {
            HostVersion = "0.0.55",
            Protocol = protocol,
            EnrollmentVersion = enrollmentVersion,
            AuthVersion = authVersion
        };

        Assert.False(BackupCompatibilityPolicy.HasCompatibleHostProtocol(info));
        Assert.False(BackupCompatibilityPolicy.IsCompatibleHost(info));
        Assert.False(BackupCompatibilityPolicy.IsCompatibleWorkstationHost(
            info,
            "0.0.105"));
    }

    [Theory]
    [InlineData("0.0.54")]
    [InlineData("")]
    public void OlderProtocolCompatibleHostRemainsUsableDuringTransition(
        string hostVersion)
    {
        var info = new BackupCompatibilityInfo
        {
            HostVersion = hostVersion,
            Protocol = "mobile-backup-v2",
            EnrollmentVersion = 2,
            AuthVersion = 3
        };

        Assert.True(BackupCompatibilityPolicy.HasCompatibleHostProtocol(info));
        Assert.True(BackupCompatibilityPolicy.IsCompatibleWorkstationHost(
            info,
            "0.0.105"));
        Assert.False(BackupCompatibilityPolicy.IsCompatibleWorkstationHost(
            info,
            "0.0.105",
            enforceMinimum: true));
    }

    [Fact]
    public void OlderProtocolCompatibleHostStillFailsStrictHostCheck()
    {
        var info = new BackupCompatibilityInfo
        {
            HostVersion = "0.0.54",
            Protocol = "mobile-backup-v2",
            EnrollmentVersion = 2,
            AuthVersion = 3
        };

        Assert.True(BackupCompatibilityPolicy.HasCompatibleHostProtocol(info));
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
    public void PreviouslyReleasedMobileClientIsRejectedBeforeEnrollment()
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
        Assert.Null(BackupCompatibilityPolicy.ValidateClient(workstation));
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

    [Fact]
    public void WorkstationHostCompatibilityUsesPublishedMinimumVersion()
    {
        var host = BackupCompatibilityPolicy.CreateHostInfo();
        host.MinimumWorkstationVersion = "0.0.120";

        Assert.True(BackupCompatibilityPolicy.IsCompatibleWorkstationHost(host, "0.0.120"));
        Assert.True(BackupCompatibilityPolicy.IsCompatibleWorkstationHost(host, "0.0.119"));
        Assert.False(BackupCompatibilityPolicy.IsCompatibleWorkstationHost(
            host,
            "0.0.119",
            enforceMinimum: true));
    }

    [Fact]
    public void TransitionReleaseAcceptsMissingOrExplicitlyOldWorkstationVersion()
    {
        BackupCompatibilityFailure? failure =
            BackupCompatibilityPolicy.ValidateWorkstationClientVersion("");

        Assert.Null(failure);
        var enrollment = new BackupDeviceEnrollmentRequest
        {
            DeviceKind = "pc",
            ClientVersion = "",
            BackupProtocol = "mobile-backup-v2",
            EnrollmentVersion = 2,
            AuthVersion = 3
        };
        Assert.Null(BackupCompatibilityPolicy.ValidateClient(enrollment));

        enrollment.ClientVersion = "0.0.54";
        Assert.Null(BackupCompatibilityPolicy.ValidateWorkstationClientVersion("0.0.54"));
        Assert.Null(BackupCompatibilityPolicy.ValidateClient(enrollment));
    }

    [Fact]
    public void FutureStrictModeRejectsExplicitlyOutdatedWorkstation()
    {
        var enrollment = new BackupDeviceEnrollmentRequest
        {
            DeviceKind = "pc",
            ClientVersion = "0.0.54",
            BackupProtocol = "mobile-backup-v2",
            EnrollmentVersion = 2,
            AuthVersion = 3
        };

        Assert.Equal(
            "recording-workstation",
            BackupCompatibilityPolicy.ValidateWorkstationClientVersion(
                "0.0.54",
                enforceMinimum: true)?.UpdateTarget);
        Assert.Equal(
            "recording-workstation",
            BackupCompatibilityPolicy.ValidateClient(
                enrollment,
                enforceWorkstationMinimum: true)?.UpdateTarget);
    }
}
