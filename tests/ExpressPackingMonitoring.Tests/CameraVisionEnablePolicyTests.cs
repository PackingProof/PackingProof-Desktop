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
            1080);
        string highResolution = CameraVisionAutoEnablePolicy.BuildBenchmarkInput(
            "model-sha",
            2560,
            1440);

        Assert.Equal(
            $"model-sha:{AppConfig.CurrentCameraVisionPolicyVersion}:1920x1080",
            input);
        Assert.NotEqual(input, highResolution);
        Assert.Empty(CameraVisionAutoEnablePolicy.BuildBenchmarkInput("", 1920, 1080));
        Assert.Empty(CameraVisionAutoEnablePolicy.BuildBenchmarkInput("model-sha", 0, 1080));
    }

    [Fact]
    public void RoiPreprocessingUsesANewBenchmarkPolicyVersion()
    {
        Assert.Equal(3, AppConfig.CurrentCameraVisionPolicyVersion);
        Assert.Equal(
            "model-sha:3:1920x1080",
            CameraVisionAutoEnablePolicy.BuildBenchmarkInput(
                "model-sha",
                1920,
                1080));
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
}
