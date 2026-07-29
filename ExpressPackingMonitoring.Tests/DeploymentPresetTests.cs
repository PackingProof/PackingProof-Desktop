using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class DeploymentPresetTests
{
    [Fact]
    public void CameraMonitorMigratesToRecordingHostWithoutChangingUserData()
    {
        const string databaseMarker = @"D:\PackingProof\videos.db";
        const string recordingPath = @"E:\录像";
        const string accessKey = "0123456789abcdef0123456789abcdef";
        var config = new AppConfig
        {
            WorkstationRole = "CameraMonitor",
            AppRootDirectory = databaseMarker,
            StorageLocations = [new StorageLocation { Path = recordingPath, Priority = 0 }],
            WebAccessKey = accessKey
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(DeploymentPresets.RecordingHost, config.DeploymentPreset);
        Assert.Equal(DeploymentPresets.CurrentSchemaVersion, config.DeploymentSchemaVersion);
        Assert.Equal(databaseMarker, config.AppRootDirectory);
        Assert.Equal(recordingPath, Assert.Single(config.StorageLocations).Path);
        Assert.Equal(accessKey, config.WebAccessKey);
    }

    [Fact]
    public void PrintStationMigratesToMobileBackupHostAndReusesPairingIdentity()
    {
        string existingComputerId = Guid.NewGuid().ToString("D");
        var config = new AppConfig
        {
            WorkstationRole = "PrintStation",
            MobileBackupComputerId = existingComputerId
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(DeploymentPresets.MobileBackupHost, config.DeploymentPreset);
        Assert.Equal(existingComputerId, config.MobileBackupComputerId);
        Assert.Equal(existingComputerId, config.NodeId);
    }

    [Fact]
    public void UnknownLegacyRoleReturnsToUnconfiguredState()
    {
        var config = new AppConfig
        {
            WorkstationRole = "UnknownRole",
            FirstUseWizardCompleted = true
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal("", config.DeploymentPreset);
        Assert.Equal(0, config.DeploymentSchemaVersion);
        Assert.False(config.FirstUseWizardCompleted);
    }

    [Fact]
    public void ViewerClientNeverEnablesWebServerDuringNormalization()
    {
        var config = new AppConfig
        {
            DeploymentPreset = DeploymentPresets.ViewerClient,
            DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion,
            EnableWebServer = true
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.False(config.EnableWebServer);
    }

    [Theory]
    [InlineData(DeploymentPresets.RecordingHost)]
    [InlineData(DeploymentPresets.RecordingWorkstation)]
    [InlineData(DeploymentPresets.ViewerClient)]
    [InlineData(DeploymentPresets.MobileBackupHost)]
    public void ExistingDeploymentPresetsRequireTheCurrentSetupOnce(string preset)
    {
        var config = new AppConfig
        {
            DeploymentPreset = preset,
            DeploymentSchemaVersion = DeploymentPresets.CurrentSchemaVersion,
            DeploymentSetupVersion = 0,
            FirstUseWizardCompleted = true
        };

        Assert.True(AppConfig.ShouldRunDeploymentSetup(config));

        AppConfig.MarkDeploymentSetupCompleted(config);

        Assert.False(AppConfig.ShouldRunDeploymentSetup(config));
        Assert.Equal(AppConfig.CurrentDeploymentSetupVersion, config.DeploymentSetupVersion);
        Assert.True(config.FirstUseWizardCompleted);
    }

    [Fact]
    public void RecordingSetupPreservesCameraBarcodeChoiceAndUserData()
    {
        const string databaseMarker = @"D:\PackingProof\videos.db";
        const string recordingPath = @"E:\录像";
        var config = new AppConfig
        {
            AppRootDirectory = databaseMarker,
            StorageLocations = [new StorageLocation { Path = recordingPath, Priority = 0 }],
            EnableCameraBarcodeRecognition = false,
            EnableGlobalKeyboard = false,
            EnableScannerAutoSubmit = true
        };

        AppConfig.ApplyFirstUseDefaults(config);

        Assert.False(config.EnableCameraBarcodeRecognition);
        Assert.False(config.EnableGlobalKeyboard);
        Assert.True(config.EnableScannerAutoSubmit);
        Assert.Equal(databaseMarker, config.AppRootDirectory);
        Assert.Equal(recordingPath, Assert.Single(config.StorageLocations).Path);
        Assert.Equal(AppConfig.CurrentDeploymentSetupVersion, config.DeploymentSetupVersion);
        Assert.Equal(AppConfig.CurrentRecordingSetupVersion, config.RecordingSetupVersion);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void RecordingSetupRunsForNewAndExistingHostsUntilExplicitCompletion(
        int recordingSetupVersion,
        bool firstUseWizardCompleted)
    {
        var config = new AppConfig
        {
            DeploymentPreset = DeploymentPresets.RecordingHost,
            DeploymentSetupVersion = AppConfig.CurrentDeploymentSetupVersion,
            FirstUseWizardCompleted = firstUseWizardCompleted,
            RecordingSetupVersion = recordingSetupVersion,
            CameraMonikerString = "camera",
            AudioDeviceName = "microphone"
        };

        Assert.Equal(2, AppConfig.CurrentRecordingSetupVersion);
        Assert.True(AppConfig.ShouldRunRecordingSetup(config));

        AppConfig.NormalizeAfterLoad(config);
        Assert.Equal(recordingSetupVersion, config.RecordingSetupVersion);
        Assert.True(AppConfig.ShouldRunRecordingSetup(config));

        AppConfig.ApplyFirstUseDefaults(config);
        Assert.False(AppConfig.ShouldRunRecordingSetup(config));
    }

    [Fact]
    public void DeploymentCapabilitiesMatchTheFourRuntimeBoundaries()
    {
        DeploymentCapabilities recording = DeploymentCapabilities.ForPreset(DeploymentPresets.RecordingHost);
        DeploymentCapabilities workstation = DeploymentCapabilities.ForPreset(DeploymentPresets.RecordingWorkstation);
        DeploymentCapabilities viewer = DeploymentCapabilities.ForPreset(DeploymentPresets.ViewerClient);
        DeploymentCapabilities mobileBackup = DeploymentCapabilities.ForPreset(DeploymentPresets.MobileBackupHost);

        Assert.True(recording.IsHost);
        Assert.True(recording.IsRecordingDevice);
        Assert.True(recording.CanUseCamera);
        Assert.True(recording.CanRecordAudio);
        Assert.True(recording.CanUseCameraBarcode);
        Assert.True(recording.CanUseScanner);
        Assert.True(recording.CanRecordPcVideo);
        Assert.True(recording.CanReceiveMobileBackup);

        Assert.False(workstation.IsHost);
        Assert.True(workstation.IsRecordingDevice);
        Assert.True(workstation.CanUseCamera);
        Assert.True(workstation.CanRecordAudio);
        Assert.True(workstation.CanUseCameraBarcode);
        Assert.True(workstation.CanUseScanner);
        Assert.True(workstation.CanRecordPcVideo);
        Assert.True(workstation.CanConnectHost);
        Assert.False(workstation.CanRunWebServer);
        Assert.False(workstation.CanReceiveMobileBackup);
        Assert.False(workstation.CanManageRecordingDevices);

        Assert.False(viewer.IsHost);
        Assert.False(viewer.IsRecordingDevice);
        Assert.True(viewer.CanConnectHost);
        Assert.True(viewer.CanGenerateUserscript);
        Assert.False(viewer.CanConfigureStorage);
        Assert.False(viewer.CanRunWebServer);

        Assert.True(mobileBackup.IsHost);
        Assert.False(mobileBackup.IsRecordingDevice);
        Assert.True(mobileBackup.CanConfigureStorage);
        Assert.True(mobileBackup.CanRunWebServer);
        Assert.True(mobileBackup.CanReceiveMobileBackup);
        Assert.False(mobileBackup.CanUseCamera);
        Assert.False(mobileBackup.CanRecordAudio);
        Assert.False(mobileBackup.CanRecordPcVideo);
    }

    [Theory]
    [InlineData(true, true, DeploymentPresets.RecordingHost)]
    [InlineData(true, false, DeploymentPresets.RecordingWorkstation)]
    [InlineData(false, true, DeploymentPresets.MobileBackupHost)]
    [InlineData(false, false, DeploymentPresets.ViewerClient)]
    public void TwoStepPurposeAnswersMapToTheFourStablePresets(
        bool recordOnThisComputer,
        bool useAsStorageHost,
        string expectedPreset)
    {
        Assert.Equal(
            expectedPreset,
            WorkstationSelectionWindow.ResolvePreset(
                recordOnThisComputer,
                useAsStorageHost));
    }

    [Fact]
    public void SettingsCapabilitiesExposeIndependentFeatureFlags()
    {
        SettingsCapabilities recording = SettingsCapabilities.ForPreset(DeploymentPresets.RecordingHost);
        SettingsCapabilities workstation = SettingsCapabilities.ForPreset(DeploymentPresets.RecordingWorkstation);
        SettingsCapabilities viewer = SettingsCapabilities.ForPreset(DeploymentPresets.ViewerClient);
        SettingsCapabilities mobileBackup = SettingsCapabilities.ForPreset(DeploymentPresets.MobileBackupHost);

        Assert.True(recording.CanUseCamera);
        Assert.True(recording.CanRecordAudio);
        Assert.True(recording.CanUseScanner);
        Assert.True(workstation.CanRecordPcVideo);
        Assert.True(workstation.CanConnectHost);
        Assert.False(workstation.CanRunWebServer);
        Assert.False(workstation.CanReceiveMobileBackup);
        Assert.False(viewer.CanUseCamera);
        Assert.False(viewer.CanConfigureStorage);
        Assert.True(viewer.CanConnectHost);
        Assert.False(mobileBackup.CanRecordPcVideo);
        Assert.True(mobileBackup.CanConfigureStorage);
    }
}
