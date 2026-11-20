using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class CameraVisionEnablePolicyTests
{
    [Theory]
    [InlineData(AppConfig.CameraVisionEnableSourceAuto, true, true, 0)]
    [InlineData(AppConfig.CameraVisionEnableSourceAuto, false, false, 1)]
    [InlineData(AppConfig.CameraVisionEnableSourceManualOn, false, true, 2)]
    [InlineData(AppConfig.CameraVisionEnableSourceManualOff, true, false, 3)]
    public void BenchmarkOnlyChangesAutoSource(
        string source,
        bool recommended,
        bool expectedEnabled,
        int expectedKind)
    {
        CameraVisionEnableDecision decision =
            CameraVisionAutoEnablePolicy.ResolveEnableDecision(source, recommended);

        Assert.Equal(expectedEnabled, decision.Enabled);
        Assert.Equal((CameraVisionEnableDecisionKind)expectedKind, decision.Kind);
        Assert.Equal(source, decision.Source);
    }

    [Fact]
    public void UnreleasedVisionConfigurationIsResetWithoutCompatibilityMigration()
    {
        var config = JsonSerializer.Deserialize<AppConfig>(
            """
            {
              "EnableCameraBarcodeEnhancedRecognition": true,
              "CameraVisionBenchmarkInput": "old-input",
              "CameraVisionBenchmarkProvider": "CPU",
              "CameraVisionBenchmarkRecommended": true,
              "CameraVisionBenchmarkCache": [
                {
                  "SchemaVersion": 1,
                  "Input": "old-cache",
                  "FrameWidth": 1920,
                  "FrameHeight": 1080
                }
              ]
            }
            """)!;

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(AppConfig.CurrentCameraVisionSchemaVersion, config.CameraVisionSchemaVersion);
        Assert.Equal(AppConfig.CurrentCameraVisionPolicyVersion, config.CameraVisionPolicyVersion);
        Assert.Equal(AppConfig.CameraVisionEnableSourceAuto, config.CameraVisionEnableSource);
        Assert.False(config.EnableCameraBarcodeEnhancedRecognition);
        Assert.Empty(config.CameraVisionBenchmarkInput);
        Assert.Empty(config.CameraVisionBenchmarkProvider);
        Assert.False(config.CameraVisionBenchmarkRecommended);
        Assert.Empty(config.CameraVisionBenchmarkCache);
    }

    [Fact]
    public void UserChoiceSurvivesNormalizationAndJsonRoundTrip()
    {
        var config = new AppConfig();
        AppConfig.NormalizeAfterLoad(config);
        AppConfig.MarkCameraVisionUserChoice(config, enabled: true);

        AppConfig restored = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(config))!;
        AppConfig.NormalizeAfterLoad(restored);

        Assert.Equal(
            AppConfig.CameraVisionEnableSourceManualOn,
            restored.CameraVisionEnableSource);
        Assert.True(restored.EnableCameraBarcodeEnhancedRecognition);
    }

    [Fact]
    public void BenchmarkInputChangesWithModelPolicyAndResolution()
    {
        string input = CameraVisionAutoEnablePolicy.BuildBenchmarkInput(
            "model-sha",
            1920,
            1080,
            CameraBarcodeGuideGeometry.Default,
            0);
        string highResolution = CameraVisionAutoEnablePolicy.BuildBenchmarkInput(
            "model-sha",
            2560,
            1440,
            CameraBarcodeGuideGeometry.Default,
            0);

        Assert.Equal(
            $"model-sha:{AppConfig.CurrentCameraVisionPolicyVersion}:"
                + "1920x1080:g0.850,0.850,0.000,0.000:r0",
            input);
        Assert.NotEqual(input, highResolution);
        Assert.Empty(CameraVisionAutoEnablePolicy.BuildBenchmarkInput(
            "",
            1920,
            1080,
            CameraBarcodeGuideGeometry.Default,
            0));
        Assert.Empty(CameraVisionAutoEnablePolicy.BuildBenchmarkInput(
            "model-sha",
            0,
            1080,
            CameraBarcodeGuideGeometry.Default,
            0));
    }

    [Fact]
    public void RoiPreprocessingUsesANewBenchmarkPolicyVersion()
    {
        Assert.Equal(5, AppConfig.CurrentCameraVisionPolicyVersion);
        Assert.Equal(
            "model-sha:5:1920x1080:g0.850,0.850,0.000,0.000:r0",
            CameraVisionAutoEnablePolicy.BuildBenchmarkInput(
                "model-sha",
                1920,
                1080,
                CameraBarcodeGuideGeometry.Default,
                0));
    }

    [Fact]
    public void BenchmarkInputChangesWithGuideGeometryAndRotation()
    {
        CameraBarcodeGuideGeometry baseline =
            CameraBarcodeGuideGeometry.Default;
        CameraBarcodeGuideGeometry adjusted =
            baseline with { WidthRatio = 0.70, OffsetX = -0.35 };

        string original =
            CameraVisionAutoEnablePolicy.BuildBenchmarkInput(
                "model-sha",
                1920,
                1080,
                baseline,
                0);
        string adjustedGuide =
            CameraVisionAutoEnablePolicy.BuildBenchmarkInput(
                "model-sha",
                1920,
                1080,
                adjusted,
                0);
        string rotated =
            CameraVisionAutoEnablePolicy.BuildBenchmarkInput(
                "model-sha",
                1920,
                1080,
                baseline,
                90);

        Assert.NotEqual(original, adjustedGuide);
        Assert.NotEqual(original, rotated);
    }

    [Fact]
    public void BenchmarkCacheRoundTripsMultipleResolutions()
    {
        var config = new AppConfig
        {
            CameraVisionSchemaVersion = AppConfig.CurrentCameraVisionSchemaVersion,
            CameraVisionPolicyVersion = AppConfig.CurrentCameraVisionPolicyVersion,
            CameraVisionBenchmarkCache =
            [
                new CameraVisionBenchmarkCacheEntry
                {
                    Input = "model:1:1920x1080",
                    FrameWidth = 1920,
                    FrameHeight = 1080,
                    Provider = "DirectML",
                    Recommended = true
                },
                new CameraVisionBenchmarkCacheEntry
                {
                    Input = "model:1:2560x1440",
                    FrameWidth = 2560,
                    FrameHeight = 1440,
                    Provider = "CPU",
                    Recommended = false
                }
            ]
        };

        AppConfig restored = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(config))!;
        AppConfig.NormalizeAfterLoad(restored);

        Assert.Equal(2, restored.CameraVisionBenchmarkCache.Count);
        Assert.Equal(1920, restored.CameraVisionBenchmarkCache[0].FrameWidth);
        Assert.Equal(2560, restored.CameraVisionBenchmarkCache[1].FrameWidth);
    }

    [Fact]
    public void BenchmarkCacheNormalizationDropsOldSchemaEntries()
    {
        var config = new AppConfig
        {
            CameraVisionSchemaVersion =
                AppConfig.CurrentCameraVisionSchemaVersion,
            CameraVisionPolicyVersion =
                AppConfig.CurrentCameraVisionPolicyVersion,
            CameraVisionBenchmarkCache =
            [
                new CameraVisionBenchmarkCacheEntry
                {
                    SchemaVersion = 1,
                    Input = "old"
                },
                new CameraVisionBenchmarkCacheEntry
                {
                    SchemaVersion =
                        AppConfig.CurrentCameraVisionBenchmarkCacheSchemaVersion,
                    Input = "current"
                }
            ]
        };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        CameraVisionBenchmarkCacheEntry remaining =
            Assert.Single(config.CameraVisionBenchmarkCache);
        Assert.Equal("current", remaining.Input);
    }
}
