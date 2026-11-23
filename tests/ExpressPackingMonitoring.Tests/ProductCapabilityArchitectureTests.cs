using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Security.Cryptography;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ProductCapabilityArchitectureTests
{
    private const string MobileReleaseUrl =
        "https://gitee.com/PackingProof/PackingProof-Mobile/releases/latest";

    private static readonly ProductFeatures FullFeatures = new(
        VideoRecording: true,
        PhotoCapture: false,
        MediaLibrary: true,
        RemoteMediaAccess: true,
        LanCollaboration: true,
        ComputerBackupReceive: true,
        MobileBackupReceive: true,
        MobileApp: true,
        NasArchive: true,
        Clip: true,
        Compress: true,
        ExtensionApi: true,
        ExtensionMarket: true,
        Userscript: true,
        OrderReceiver: true,
        Tts: true,
        Inputs: new InputFeatures(true, true, true, true, true));

    [Fact]
    public void PackingProofFeatureDocumentIsStrictAndEmbedded()
    {
        ProductFeatures features = BrandMetadata.ProductFeatures;

        Assert.Equal(ProductCapabilitySchemaVersion.Current,
            BrandMetadata.ProductCapabilitySchemaVersion);
        Assert.Matches("^[0-9a-f]{64}$", BrandMetadata.ProductFeaturesSha256);
        Assert.True(features.VideoRecording);
        Assert.True(features.MediaLibrary);
        Assert.True(features.RemoteMediaAccess);
        Assert.True(features.MobileBackupReceive);
        Assert.True(features.ExtensionApi);
        Assert.True(features.Inputs.Camera);
    }

    [Theory]
    [InlineData("packingproof")]
    [InlineData("software-template")]
    public void CheckedInProfilesUseTheCurrentSchemaAndValidDependencies(string profile)
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "branding",
            "profiles",
            profile,
            "Features.json");
        string json = File.ReadAllText(path, System.Text.Encoding.UTF8);

        ProductFeatures features = ProductFeaturesJson.Parse(json);
        Assert.Equal(
            ProductCapabilitySchemaVersion.Current,
            ReadSchemaVersion(json));
        Assert.Empty(ProductFeatureValidator.Validate(
            features,
            new ProductFeatureValidationContext(MobileReleaseUrl)));
    }

    [Fact]
    public void ParserRejectsUnknownMissingAndDuplicateFields()
    {
        string valid = ProductFeaturesJsonFixture(FullFeatures);
        string unknown = valid.Replace(
            "\"videoRecording\": true,",
            "\"videoRecording\": true,\n    \"videoRecordng\": false,",
            StringComparison.Ordinal);
        string missing = valid.Replace(
            "\"mobileApp\": true,",
            "",
            StringComparison.Ordinal);
        string duplicate = valid.Replace(
            "\"mediaLibrary\": true,",
            "\"mediaLibrary\": true,\n    \"mediaLibrary\": false,",
            StringComparison.Ordinal);
        string wrongType = valid.Replace(
            "\"videoRecording\": true,",
            "\"videoRecording\": 1,",
            StringComparison.Ordinal);

        Assert.False(ProductFeaturesJson.TryParse(
            unknown,
            out _,
            out IReadOnlyList<string> unknownErrors));
        Assert.Contains(unknownErrors, error => error.Contains("未知字段", StringComparison.Ordinal));

        Assert.False(ProductFeaturesJson.TryParse(
            missing,
            out _,
            out IReadOnlyList<string> missingErrors));
        Assert.Contains(missingErrors, error => error.Contains("缺少字段", StringComparison.Ordinal));

        Assert.False(ProductFeaturesJson.TryParse(
            duplicate,
            out _,
            out IReadOnlyList<string> duplicateErrors));
        Assert.Contains(duplicateErrors, error => error.Contains("重复字段", StringComparison.Ordinal));

        Assert.False(ProductFeaturesJson.TryParse(
            wrongType,
            out _,
            out IReadOnlyList<string> wrongTypeErrors));
        Assert.Contains(wrongTypeErrors, error => error.Contains("true 或 false", StringComparison.Ordinal));
    }

    [Fact]
    public void ParserRejectsUnsupportedSchemaBeforeReadingCapabilities()
    {
        string json = ProductFeaturesJsonFixture(FullFeatures)
            .Replace(
                $"\"schemaVersion\": {ProductCapabilitySchemaVersion.Current}",
                "\"schemaVersion\": 999",
                StringComparison.Ordinal);

        bool parsed = ProductFeaturesJson.TryParse(
            json,
            out _,
            out IReadOnlyList<string> errors);

        Assert.False(parsed);
        Assert.Contains(errors, error => error.Contains("schemaVersion", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("photoCapture", "mediaLibrary")]
    [InlineData("videoRecording", "mediaLibrary")]
    [InlineData("videoRecording", "inputs.camera")]
    [InlineData("inputs.cameraBarcode", "inputs.camera")]
    [InlineData("inputs.visionObb", "inputs.camera")]
    [InlineData("inputs.visionObb", "videoRecording")]
    [InlineData("inputs.microphone", "videoRecording")]
    [InlineData("remoteMediaAccess", "mediaLibrary")]
    [InlineData("mobileBackupReceive", "mediaLibrary")]
    [InlineData("computerBackupReceive", "mediaLibrary")]
    [InlineData("nasArchive", "mediaLibrary")]
    [InlineData("clip", "mediaLibrary")]
    [InlineData("compress", "mediaLibrary")]
    [InlineData("extensionMarket", "extensionApi")]
    public void StaticDependenciesFailClosed(string enabledCapability, string dependency)
    {
        ProductFeatures features = FeatureWith(
            FullFeatures,
            enabledCapability,
            true,
            dependency,
            false);

        IReadOnlyList<string> errors = ProductFeatureValidator.Validate(
            features,
            new ProductFeatureValidationContext(MobileReleaseUrl));

        Assert.Contains(errors, error => error.Contains(
            enabledCapability,
            StringComparison.Ordinal));
    }

    [Fact]
    public void MobileAppRequiresConfiguredReleaseUrl()
    {
        IReadOnlyList<string> errors = ProductFeatureValidator.Validate(
            FullFeatures,
            new ProductFeatureValidationContext(""));

        Assert.Contains(errors, error => error.Contains("手机发布地址", StringComparison.Ordinal));
    }

    [Fact]
    public void PhotoOnlyProductIsValidWithoutVideoOrMicrophone()
    {
        var photoOnly = new ProductFeatures(
            VideoRecording: false,
            PhotoCapture: true,
            MediaLibrary: true,
            RemoteMediaAccess: false,
            LanCollaboration: false,
            ComputerBackupReceive: false,
            MobileBackupReceive: false,
            MobileApp: false,
            NasArchive: false,
            Clip: false,
            Compress: false,
            ExtensionApi: false,
            ExtensionMarket: false,
            Userscript: false,
            OrderReceiver: false,
            Tts: false,
            Inputs: new InputFeatures(
                Camera: true,
                Microphone: false,
                Scanner: false,
                CameraBarcode: false,
                VisionObb: false));

        Assert.Empty(ProductFeatureValidator.Validate(
            photoOnly,
            new ProductFeatureValidationContext("")));

        EffectiveProductCapabilities effective = EffectiveProductCapabilities.Compose(
            photoOnly,
            ProductRoleCapabilityMapper.Get(ProductRoleCapabilityMapper.RecordingHost),
            RuntimeEnvironment.FullyAvailable,
            BrandMetadata.CapabilityIdentity);

        Assert.True(effective.Features.PhotoCapture);
        Assert.False(effective.Features.VideoRecording);
        Assert.False(effective.Features.Inputs.Microphone);
        Assert.False(effective.WebHostEnabled);
    }

    [Fact]
    public void DiagnosticSnapshotUsesTheSameEffectiveCapabilities()
    {
        EffectiveProductCapabilities effective = EffectiveProductCapabilities.Compose(
            FullFeatures,
            ProductRoleCapabilityMapper.Get(
                ProductRoleCapabilityMapper.RecordingWorkstation),
            RuntimeEnvironment.FullyAvailable,
            BrandMetadata.CapabilityIdentity);

        CapabilityDiagnosticSnapshot snapshot = effective.CreateDiagnosticSnapshot();

        Assert.Equal(BrandMetadata.ProfileId, snapshot.ProfileId);
        Assert.Equal(
            ProductCapabilitySchemaVersion.Current,
            snapshot.FeaturesSchemaVersion);
        Assert.Equal(
            ProductRoleCapabilityMapper.RecordingWorkstation,
            snapshot.SelectedDeploymentPreset);
        Assert.Same(effective.Features, snapshot.EffectiveCapabilities);
        Assert.True(snapshot.WebHostEnabled);
        Assert.Contains("orders-v0", snapshot.WebRouteGroups);
    }

    [Fact]
    public void EveryRoleHasAnExplicitCanonicalCapabilityMask()
    {
        Assert.Equal(4, ProductRoleCapabilityMapper.AllRoles.Count);
        var all = Enum.GetValues<ProductCapabilityId>().ToHashSet();
        var covered = new HashSet<ProductCapabilityId>(
            ProductRoleCapabilityMapper.BrandFixedCapabilities);
        foreach (RoleCapabilities role in ProductRoleCapabilityMapper.AllRoles)
        {
            Assert.NotEmpty(role.Allowed);
            Assert.Empty(role.Allowed.Intersect(
                ProductRoleCapabilityMapper.BrandFixedCapabilities));
            covered.UnionWith(role.Allowed);
        }

        Assert.Equal(all, covered);
    }

    [Fact]
    public void RoleCapabilityMasksMatchTheirAndOnlyTheirDeploymentBoundary()
    {
        Assert.True(ProductRoleCapabilityMapper.Get(
            ProductRoleCapabilityMapper.RecordingHost).CanStartWebHost);
        Assert.True(ProductRoleCapabilityMapper.Get(
            ProductRoleCapabilityMapper.RecordingWorkstation).CanStartWebHost);
        Assert.False(ProductRoleCapabilityMapper.Get(
            ProductRoleCapabilityMapper.ViewerClient).CanStartWebHost);
        Assert.True(ProductRoleCapabilityMapper.Get(
            ProductRoleCapabilityMapper.MobileBackupHost).CanStartWebHost);
        Assert.Equal(
            Expect(
                ProductCapabilityId.VideoRecording,
                ProductCapabilityId.PhotoCapture,
                ProductCapabilityId.MediaLibrary,
                ProductCapabilityId.RemoteMediaAccess,
                ProductCapabilityId.LanCollaboration,
                ProductCapabilityId.OrderReceiver,
                ProductCapabilityId.ComputerBackupReceive,
                ProductCapabilityId.MobileBackupReceive,
                ProductCapabilityId.NasArchive,
                ProductCapabilityId.OrderReceiver,
                ProductCapabilityId.Clip,
                ProductCapabilityId.Compress,
                ProductCapabilityId.Userscript,
                ProductCapabilityId.InputCamera,
                ProductCapabilityId.InputMicrophone,
                ProductCapabilityId.InputScanner,
                ProductCapabilityId.InputCameraBarcode,
                ProductCapabilityId.InputVisionObb),
            ProductRoleCapabilityMapper.Get(
                ProductRoleCapabilityMapper.RecordingHost).Allowed);
        Assert.Equal(
            Expect(
                ProductCapabilityId.VideoRecording,
                ProductCapabilityId.PhotoCapture,
                ProductCapabilityId.MediaLibrary,
                ProductCapabilityId.LanCollaboration,
                ProductCapabilityId.OrderReceiver,
                ProductCapabilityId.Clip,
                ProductCapabilityId.Compress,
                ProductCapabilityId.InputCamera,
                ProductCapabilityId.InputMicrophone,
                ProductCapabilityId.InputScanner,
                ProductCapabilityId.InputCameraBarcode,
                ProductCapabilityId.InputVisionObb),
            ProductRoleCapabilityMapper.Get(
                ProductRoleCapabilityMapper.RecordingWorkstation).Allowed);
        Assert.Equal(
            Expect(
                ProductCapabilityId.MediaLibrary,
                ProductCapabilityId.LanCollaboration,
                ProductCapabilityId.Userscript),
            ProductRoleCapabilityMapper.Get(
                ProductRoleCapabilityMapper.ViewerClient).Allowed);
        Assert.Equal(
            Expect(
                ProductCapabilityId.MediaLibrary,
                ProductCapabilityId.RemoteMediaAccess,
                ProductCapabilityId.LanCollaboration,
                ProductCapabilityId.ComputerBackupReceive,
                ProductCapabilityId.MobileBackupReceive,
                ProductCapabilityId.NasArchive,
                ProductCapabilityId.OrderReceiver,
                ProductCapabilityId.Clip,
                ProductCapabilityId.Compress,
                ProductCapabilityId.Userscript),
            ProductRoleCapabilityMapper.Get(
                ProductRoleCapabilityMapper.MobileBackupHost).Allowed);
    }

    [Fact]
    public void FullBrandExposesAllFourStableRoles()
    {
        Assert.Equal(4, ProductCapabilityRuntime.AvailableRoles().Count);
        Assert.Null(ProductCapabilityRuntime.SingleAvailablePreset());
    }

    [Fact]
    public void MinimalLocalProductExposesOnlyRecordingHost()
    {
        ProductFeatures localCapture = LocalCaptureFeatures();
        RoleCapabilities host = ProductRoleCapabilityMapper.Get(
            ProductRoleCapabilityMapper.RecordingHost);

        Assert.True(ProductRoleAvailabilityPolicy.IsAvailable(localCapture, host));
        foreach (RoleCapabilities role in ProductRoleCapabilityMapper.AllRoles
                     .Where(role => role.DeploymentPreset != host.DeploymentPreset))
        {
            Assert.False(ProductRoleAvailabilityPolicy.IsAvailable(localCapture, role));
        }
    }

    [Fact]
    public void CameraUnavailableRemovesLocalRecordingRoleAndCameraSettings()
    {
        ProductFeatures localCapture = LocalCaptureFeatures();
        RoleCapabilities host = ProductRoleCapabilityMapper.Get(
            ProductRoleCapabilityMapper.RecordingHost);
        RuntimeEnvironment noCamera = RuntimeEnvironment.FullyAvailable with
        {
            CameraAvailable = false
        };

        Assert.False(ProductRoleAvailabilityPolicy.IsAvailable(
            localCapture,
            host,
            noCamera));
        EffectiveProductCapabilities permissions = EffectiveProductCapabilities.Compose(
            localCapture,
            host,
            RuntimeEnvironment.FullyAvailable,
            new ProductCapabilityIdentity("local-capture", 1, new string('0', 64)));
        EffectiveProductCapabilities effective = EffectiveProductCapabilities.Compose(
            localCapture,
            host,
            noCamera,
            new ProductCapabilityIdentity("local-capture", 1, new string('0', 64)));
        SettingsCapabilities settings = SettingsCapabilities.FromEffective(
            permissions,
            effective,
            canChoosePurpose: false);
        Assert.True(settings.CanUseCamera);
        Assert.True(settings.CanRecordPcVideo);
        Assert.True(settings.CanUseCameraBarcode);
        Assert.False(settings.CanOperateCamera);
        Assert.False(settings.CanOperateVideo);
        Assert.False(settings.CanOperateCameraBarcode);
    }

    [Fact]
    public void RuntimeEnvironmentFollowsConfiguredCameraAndMicrophoneFacts()
    {
        var configured = new AppConfig
        {
            FirstUseWizardCompleted = true,
            CameraMonikerString = "camera-moniker",
            AudioDeviceName = "microphone",
            EnableAudioRecording = true,
            EnableCameraBarcodeRecognition = true
        };
        var missing = new AppConfig
        {
            FirstUseWizardCompleted = true,
            EnableAudioRecording = true
        };

        RuntimeEnvironment configuredEnvironment =
            RuntimeCapabilityEnvironment.FromConfig(configured);
        RuntimeEnvironment missingEnvironment =
            RuntimeCapabilityEnvironment.FromConfig(missing);

        Assert.True(configuredEnvironment.CameraAvailable);
        Assert.True(configuredEnvironment.MicrophoneAvailable);
        Assert.False(missingEnvironment.CameraAvailable);
        Assert.False(missingEnvironment.MicrophoneAvailable);
    }

    [Fact]
    public void PhotoOnlyAndBackupOnlyProductsExposeTheirSingleUsefulRole()
    {
        ProductFeatures photoOnly = LocalCaptureFeatures() with
        {
            VideoRecording = false,
            PhotoCapture = true,
            Inputs = LocalCaptureFeatures().Inputs with
            {
                Microphone = false,
                VisionObb = false
            }
        };
        ProductFeatures backupOnly = EmptyFeatures() with
        {
            MediaLibrary = true,
            MobileBackupReceive = true
        };

        Assert.True(ProductRoleAvailabilityPolicy.IsAvailable(
            photoOnly,
            ProductRoleCapabilityMapper.Get(
                ProductRoleCapabilityMapper.RecordingHost)));
        Assert.False(ProductRoleAvailabilityPolicy.IsAvailable(
            photoOnly,
            ProductRoleCapabilityMapper.Get(
                ProductRoleCapabilityMapper.RecordingWorkstation)));

        Assert.True(ProductRoleAvailabilityPolicy.IsAvailable(
            backupOnly,
            ProductRoleCapabilityMapper.Get(
                ProductRoleCapabilityMapper.MobileBackupHost)));
        Assert.False(ProductRoleAvailabilityPolicy.IsAvailable(
            backupOnly,
            ProductRoleCapabilityMapper.Get(
                ProductRoleCapabilityMapper.RecordingHost)));
    }

    [Fact]
    public void MinimalLocalSettingsCapabilitiesDoNotLeakNetworkOrBackupFeatures()
    {
        EffectiveProductCapabilities effective = EffectiveProductCapabilities.Compose(
            LocalCaptureFeatures(),
            ProductRoleCapabilityMapper.Get(
                ProductRoleCapabilityMapper.RecordingHost),
            RuntimeEnvironment.FullyAvailable,
            new ProductCapabilityIdentity("local-capture", 1, new string('0', 64)));
        SettingsCapabilities settings = SettingsCapabilities.FromEffective(effective);

        Assert.True(settings.IsRecordingDevice);
        Assert.True(settings.CanUseCamera);
        Assert.True(settings.CanRecordAudio);
        Assert.True(settings.CanUseScanner);
        Assert.True(settings.CanConfigureStorage);
        Assert.False(settings.CanRunWebServer);
        Assert.False(settings.CanReceiveMobileBackup);
        Assert.False(settings.CanConnectHost);
        Assert.False(settings.CanGenerateUserscript);
        Assert.False(settings.CanManageRecordingDevices);
    }

    [Theory]
    [InlineData(ProductRoleCapabilityMapper.RecordingHost, true, true, true)]
    [InlineData(ProductRoleCapabilityMapper.RecordingWorkstation, false, true, true)]
    [InlineData(ProductRoleCapabilityMapper.ViewerClient, false, false, false)]
    [InlineData(ProductRoleCapabilityMapper.MobileBackupHost, true, true, false)]
    public void LegacyCapabilityStringsFollowRoleFacts(
        string preset,
        bool expectedHost,
        bool expectedOrderReceiver,
        bool expectedRecording)
    {
        RoleCapabilities role = ProductRoleCapabilityMapper.Get(preset);
        EffectiveProductCapabilities effective = EffectiveProductCapabilities.Compose(
            WithDefaultsWithoutFixedExtensionApi(FullFeatures),
            role,
            RuntimeEnvironment.FullyAvailable,
            BrandMetadata.CapabilityIdentity);
        IReadOnlyList<string> legacy =
            ProductCapabilityProtocolMapper.ToLegacyCapabilities(effective);

        Assert.Equal(expectedHost, legacy.Contains("host", StringComparer.Ordinal));
        Assert.Equal(expectedOrderReceiver,
            legacy.Contains("order-receiver", StringComparer.Ordinal));
        Assert.Equal(expectedRecording,
            legacy.Contains("recording", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(ProductRoleCapabilityMapper.RecordingHost)]
    [InlineData(ProductRoleCapabilityMapper.RecordingWorkstation)]
    [InlineData(ProductRoleCapabilityMapper.ViewerClient)]
    [InlineData(ProductRoleCapabilityMapper.MobileBackupHost)]
    public void LegacyProtocolSetMatchesTheExistingPresetContract(string preset)
    {
        EffectiveProductCapabilities effective = EffectiveProductCapabilities.Compose(
            FullFeatures,
            ProductRoleCapabilityMapper.Get(preset),
            RuntimeEnvironment.FullyAvailable,
            BrandMetadata.CapabilityIdentity);

        Assert.Equal(
            PackingProofCapabilities.ForPreset(preset),
            ProductCapabilityProtocolMapper.ToLegacyCapabilities(effective));
    }

    [Fact]
    public void ComputerOnlyBackupStillAdvertisesTheLegacyMobileBackupCapability()
    {
        ProductFeatures computerOnly = EmptyFeatures() with
        {
            MediaLibrary = true,
            RemoteMediaAccess = true,
            ComputerBackupReceive = true
        };
        EffectiveProductCapabilities effective = EffectiveProductCapabilities.Compose(
            computerOnly,
            ProductRoleCapabilityMapper.Get(
                ProductRoleCapabilityMapper.MobileBackupHost),
            RuntimeEnvironment.FullyAvailable,
            BrandMetadata.CapabilityIdentity);

        Assert.Contains(
            "mobile-backup",
            ProductCapabilityProtocolMapper.ToLegacyCapabilities(effective),
            StringComparer.Ordinal);
    }

    [Fact]
    public void UserscriptAloneIsValidForAViewerAndNeverStartsALocalHost()
    {
        ProductFeatures referenceOnly = EmptyFeatures() with
        {
            MediaLibrary = true,
            LanCollaboration = true,
            Userscript = true
        };
        Assert.Empty(ProductFeatureValidator.Validate(
            referenceOnly,
            new ProductFeatureValidationContext("")));

        EffectiveProductCapabilities effective = EffectiveProductCapabilities.Compose(
            referenceOnly,
            ProductRoleCapabilityMapper.Get(ProductRoleCapabilityMapper.ViewerClient),
            RuntimeEnvironment.FullyAvailable,
            BrandMetadata.CapabilityIdentity);

        Assert.True(effective.Features.Userscript);
        Assert.False(effective.WebHostEnabled);
    }

    [Fact]
    public void LocalUserscriptRouteRequiresAnOrderEntryOnTheHost()
    {
        ProductFeatures mediaAndUserscript = EmptyFeatures() with
        {
            MediaLibrary = true,
            RemoteMediaAccess = true,
            Userscript = true
        };

        WebHostPlan plan = ProductWebRouteGroups.Resolve(
            mediaAndUserscript,
            ProductRoleCapabilityMapper.Get(ProductRoleCapabilityMapper.RecordingHost));

        Assert.True(plan.Enabled);
        Assert.DoesNotContain(plan.EnabledGroups, group => group.Name == "userscript");
    }

    [Fact]
    public void ViewerClientDoesNotStartAWebHostWithoutRemoteRoutes()
    {
        ProductFeatures viewerBrand = FullFeatures with
        {
            VideoRecording = false,
            RemoteMediaAccess = false,
            LanCollaboration = true,
            ComputerBackupReceive = false,
            MobileBackupReceive = false,
            MobileApp = false,
            NasArchive = false,
            Clip = false,
            Compress = false,
            Userscript = true,
            OrderReceiver = false,
            Inputs = new InputFeatures(false, false, false, false, false)
        };

        EffectiveProductCapabilities effective = EffectiveProductCapabilities.Compose(
            viewerBrand,
            ProductRoleCapabilityMapper.Get(ProductRoleCapabilityMapper.ViewerClient),
            RuntimeEnvironment.FullyAvailable,
            BrandMetadata.CapabilityIdentity);

        Assert.False(effective.IsHost);
        Assert.True(effective.Features.ExtensionApi);
        Assert.False(effective.WebHostEnabled);
        Assert.Empty(effective.WebHost.EnabledGroups);
    }

    [Fact]
    public void MediaAndOrderRoutesStartHostButMobileAppAloneDoesNot()
    {
        ProductFeatures media = EmptyFeatures() with
        {
            MediaLibrary = true,
            RemoteMediaAccess = true
        };
        ProductFeatures orders = EmptyFeatures() with { OrderReceiver = true };
        ProductFeatures mobileApp = EmptyFeatures() with { MobileApp = true };
        ProductFeatures lan = EmptyFeatures() with { LanCollaboration = true };
        RoleCapabilities recordingHost = ProductRoleCapabilityMapper.Get(
            ProductRoleCapabilityMapper.RecordingHost);

        Assert.Contains(ProductWebRouteGroups.Resolve(media, recordingHost).EnabledGroups,
            group => group.Name == "media-library");
        Assert.Contains(ProductWebRouteGroups.Resolve(orders, recordingHost).EnabledGroups,
            group => group.Name == "orders-v0");
        Assert.False(ProductWebRouteGroups.Resolve(mobileApp, recordingHost).Enabled);
        Assert.False(ProductWebRouteGroups.Resolve(lan, recordingHost).Enabled);
    }

    [Fact]
    public void IntegrationOnlyHostCanExposeNodeInfoWithoutMediaRoutes()
    {
        ProductFeatures integration = EmptyFeatures() with
        {
            OrderReceiver = true,
            ExtensionApi = true
        };

        WebHostPlan plan = ProductWebRouteGroups.Resolve(
            integration,
            ProductRoleCapabilityMapper.Get(
                ProductRoleCapabilityMapper.RecordingWorkstation));

        Assert.True(plan.Enabled);
        Assert.Contains(plan.EnabledGroups, group => group.Name == "node-info");
        Assert.DoesNotContain(plan.EnabledGroups, group => group.Name == "media-library");
        Assert.DoesNotContain(plan.EnabledGroups, group => group.Name == "mobile-app");
        Assert.DoesNotContain(plan.EnabledGroups, group => group.Name == "local-settings");
    }

    [Fact]
    public void RecordingWorkstationKeepsTheExistingOrderOnlyRouteAllowlist()
    {
        EffectiveProductCapabilities effective = EffectiveProductCapabilities.Compose(
            FullFeatures,
            ProductRoleCapabilityMapper.Get(
                ProductRoleCapabilityMapper.RecordingWorkstation),
            RuntimeEnvironment.FullyAvailable,
            BrandMetadata.CapabilityIdentity);
        string[] groups = effective.WebHost.EnabledGroups
            .Select(group => group.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "extensions-v1",
                "extensions-v1-orders",
                "node-info",
                "orders-v0"
            ],
            groups);
        Assert.DoesNotContain("mobile-app", groups);
        Assert.DoesNotContain("local-settings", groups);
        Assert.DoesNotContain("media-library", groups);
    }

    [Fact]
    public void RuntimeCameraLossNarrowsRecordingAndPhotoWithoutChangingBrandTruth()
    {
        var photoOnly = new ProductFeatures(
            VideoRecording: false,
            PhotoCapture: true,
            MediaLibrary: true,
            RemoteMediaAccess: false,
            LanCollaboration: false,
            ComputerBackupReceive: false,
            MobileBackupReceive: false,
            MobileApp: false,
            NasArchive: false,
            Clip: false,
            Compress: false,
            ExtensionApi: false,
            ExtensionMarket: false,
            Userscript: false,
            OrderReceiver: false,
            Tts: false,
            Inputs: new InputFeatures(true, false, false, false, false));
        RuntimeEnvironment unavailable = RuntimeEnvironment.FullyAvailable with
        {
            CameraAvailable = false
        };

        EffectiveProductCapabilities effective = EffectiveProductCapabilities.Compose(
            photoOnly,
            ProductRoleCapabilityMapper.Get(ProductRoleCapabilityMapper.RecordingHost),
            unavailable,
            BrandMetadata.CapabilityIdentity);

        Assert.False(effective.Features.PhotoCapture);
        Assert.False(effective.Features.Inputs.Camera);
        Assert.Contains(effective.NarrowingReasons,
            reason => reason.CapabilityId == ProductCapabilityId.InputCamera
                && reason.ReasonCode == "camera_unavailable");
    }

    [Fact]
    public void RuntimeNarrowingClosesAllDependenciesOnACameraOrRecording()
    {
        EffectiveProductCapabilities effective = EffectiveProductCapabilities.Compose(
            FullFeatures,
            ProductRoleCapabilityMapper.Get(ProductRoleCapabilityMapper.RecordingHost),
            RuntimeEnvironment.FullyAvailable with { CameraAvailable = false },
            BrandMetadata.CapabilityIdentity);

        Assert.False(effective.Features.VideoRecording);
        Assert.False(effective.Features.PhotoCapture);
        Assert.False(effective.Features.Inputs.Camera);
        Assert.False(effective.Features.Inputs.Microphone);
        Assert.False(effective.Features.Inputs.CameraBarcode);
        Assert.False(effective.Features.Inputs.VisionObb);
        Assert.Empty(ProductFeatureValidator.ValidateRelationships(
            effective.Features));
    }

    [Fact]
    public void ProductionDirectReadsOnlyShrinkAgainstTheFrozenBoundary()
    {
        string root = FindRepositoryRoot();
        string baselinePath = Path.Combine(
            root,
            "tests",
            "ExpressPackingMonitoring.Tests",
            "ProductCapabilityBoundaryBaseline.txt");
        string[] baseline = File.ReadAllLines(baselinePath, System.Text.Encoding.UTF8)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToHashSet(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] current = FindDirectReads(root).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(baseline, current);
    }

    [Fact]
    public void BuildValidationTargetRejectsAnInvalidExternalProfile()
    {
        string root = FindRepositoryRoot();
        string profileDirectory = Path.Combine(
            root,
            "tests",
            "ExpressPackingMonitoring.Tests",
            "Fixtures",
            "BrandProfiles",
            "invalid-conflict");
        string project = Path.Combine(
            root,
            "src",
            "ExpressPackingMonitoring",
            "ExpressPackingMonitoring.csproj");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("-t:ValidateBrandFeatures");
        startInfo.ArgumentList.Add("-p:Configuration=Debug");
        startInfo.ArgumentList.Add("-p:BrandProfile=invalid-conflict");
        startInfo.ArgumentList.Add($"-p:BrandProfileDirectory={profileDirectory}");
        startInfo.ArgumentList.Add("-nologo");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动软件能力构建校验");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "软件能力构建校验超时");

        Assert.True(
            process.ExitCode != 0,
            $"冲突能力不应通过构建：{output}{Environment.NewLine}{error}");
        Assert.Contains(
            "photoCapture",
            output + error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BrandProfileRejectsFeaturePathsOutsideItsDirectory()
    {
        string root = FindRepositoryRoot();
        string profileDirectory = Path.Combine(
            root,
            "tests",
            "ExpressPackingMonitoring.Tests",
            "Fixtures",
            "BrandProfiles",
            "path-traversal");
        (int exitCode, string output) = RunDotNet(
            root,
            "msbuild",
            Path.Combine(
                root,
                "src",
                "ExpressPackingMonitoring",
                "ExpressPackingMonitoring.csproj"),
            "-t:ValidateBrandFeaturesPath",
            "-p:Configuration=Debug",
            "-p:BrandProfile=path-traversal",
            $"-p:BrandProfileDirectory={profileDirectory}",
            "-nologo");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("不能包含上级目录", output, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedFeatureHashMatchesTheSelectedSourceFile()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "branding",
            "profiles",
            BrandMetadata.ProfileId,
            "Features.json");
        string expected = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        Assert.Equal(expected, BrandMetadata.ProductFeaturesSha256);
    }

    private static ProductFeatures WithDefaultsWithoutFixedExtensionApi(
        ProductFeatures features) =>
        features with
        {
            ExtensionApi = false,
            ExtensionMarket = false
        };

    private static ProductFeatures EmptyFeatures() =>
        new(
            VideoRecording: false,
            PhotoCapture: false,
            MediaLibrary: false,
            RemoteMediaAccess: false,
            LanCollaboration: false,
            ComputerBackupReceive: false,
            MobileBackupReceive: false,
            MobileApp: false,
            NasArchive: false,
            Clip: false,
            Compress: false,
            ExtensionApi: false,
            ExtensionMarket: false,
            Userscript: false,
            OrderReceiver: false,
            Tts: false,
            Inputs: new InputFeatures(false, false, false, false, false));

    private static ProductFeatures LocalCaptureFeatures() =>
        EmptyFeatures() with
        {
            VideoRecording = true,
            MediaLibrary = true,
            Inputs = new InputFeatures(
                Camera: true,
                Microphone: true,
                Scanner: true,
                CameraBarcode: true,
                VisionObb: true)
        };

    private static IReadOnlySet<ProductCapabilityId> Expect(
        params ProductCapabilityId[] values) =>
        values.ToHashSet();

    private static (int ExitCode, string Output) RunDotNet(
        string workingDirectory,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 dotnet 子进程");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(120_000), "dotnet 子进程超时");
        return (process.ExitCode, output + error);
    }

    private static ProductFeatures FeatureWith(
        ProductFeatures source,
        string enabledCapability,
        bool enabledValue,
        string dependency,
        bool dependencyValue)
    {
        ProductFeatures changed = dependency switch
        {
            "inputs.camera" => source with
            {
                Inputs = source.Inputs with { Camera = dependencyValue }
            },
            "mediaLibrary" => source with { MediaLibrary = dependencyValue },
            "videoRecording" => source with { VideoRecording = dependencyValue },
            "extensionApi" => source with { ExtensionApi = dependencyValue },
            _ => throw new ArgumentOutOfRangeException(nameof(dependency))
        };
        return enabledCapability switch
        {
            "photoCapture" => changed with { PhotoCapture = enabledValue },
            "videoRecording" => changed with { VideoRecording = enabledValue },
            "remoteMediaAccess" => changed with { RemoteMediaAccess = enabledValue },
            "mobileBackupReceive" => changed with { MobileBackupReceive = enabledValue },
            "computerBackupReceive" => changed with { ComputerBackupReceive = enabledValue },
            "nasArchive" => changed with { NasArchive = enabledValue },
            "clip" => changed with { Clip = enabledValue },
            "compress" => changed with { Compress = enabledValue },
            "extensionMarket" => changed with { ExtensionMarket = enabledValue },
            "inputs.cameraBarcode" => changed with
            {
                Inputs = changed.Inputs with { CameraBarcode = enabledValue }
            },
            "inputs.visionObb" => changed with
            {
                Inputs = changed.Inputs with { VisionObb = enabledValue }
            },
            "inputs.microphone" => changed with
            {
                Inputs = changed.Inputs with { Microphone = enabledValue }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(enabledCapability))
        };
    }

    private static string ProductFeaturesJsonFixture(ProductFeatures features) =>
        $$"""
        {
          "schemaVersion": {{ProductCapabilitySchemaVersion.Current}},
          "capabilities": {
            "videoRecording": {{Json(features.VideoRecording)}},
            "photoCapture": {{Json(features.PhotoCapture)}},
            "mediaLibrary": {{Json(features.MediaLibrary)}},
            "remoteMediaAccess": {{Json(features.RemoteMediaAccess)}},
            "lanCollaboration": {{Json(features.LanCollaboration)}},
            "computerBackupReceive": {{Json(features.ComputerBackupReceive)}},
            "mobileBackupReceive": {{Json(features.MobileBackupReceive)}},
            "mobileApp": {{Json(features.MobileApp)}},
            "nasArchive": {{Json(features.NasArchive)}},
            "clip": {{Json(features.Clip)}},
            "compress": {{Json(features.Compress)}},
            "extensionApi": {{Json(features.ExtensionApi)}},
            "extensionMarket": {{Json(features.ExtensionMarket)}},
            "userscript": {{Json(features.Userscript)}},
            "orderReceiver": {{Json(features.OrderReceiver)}},
            "tts": {{Json(features.Tts)}}
          },
          "inputs": {
            "camera": {{Json(features.Inputs.Camera)}},
            "microphone": {{Json(features.Inputs.Microphone)}},
            "scanner": {{Json(features.Inputs.Scanner)}},
            "cameraBarcode": {{Json(features.Inputs.CameraBarcode)}},
            "visionObb": {{Json(features.Inputs.VisionObb)}}
          }
        }
        """;

    private static string Json(bool value) => value ? "true" : "false";

    private static int ReadSchemaVersion(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.GetProperty("schemaVersion").GetInt32();
    }

    private static IEnumerable<string> FindDirectReads(string root)
    {
        string productionRoot = Path.Combine(
            root,
            "src",
            "ExpressPackingMonitoring");
        var patterns = new[]
        {
            "Config\\s*\\.\\s*DeploymentPreset(?!\\s*=)",
            "DeploymentCapabilities\\s*\\.\\s*ForPreset",
            "PackingProofCapabilities\\s*\\.\\s*ForPreset",
            "BrandMetadata\\s*\\.\\s*MobileAppEnabled",
            "BrandMetadata\\s*\\.\\s*Mobile(?:RepositoryUrl|ReleasesUrl|LatestReleaseApiUrl|ReleasesApiUrl)",
            "EffectiveProductCapabilities\\s*\\.\\s*Compose",
            "BrandMetadata\\s*\\.\\s*ProductFeatures"
        };
        foreach (string file in Directory.EnumerateFiles(
                     productionRoot,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text = File.ReadAllText(file, System.Text.Encoding.UTF8);
            foreach (string pattern in patterns)
            {
                MatchCollection matches = Regex.Matches(
                    text,
                    pattern,
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                if (matches.Count > 0)
                {
                    yield return $"{relative}|{pattern}|{matches.Count}";
                }
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(start);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }
        }
        throw new InvalidOperationException("找不到仓库根目录");
    }
}
