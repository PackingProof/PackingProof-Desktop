using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VisionModelDistributionTests
{
    [Fact]
    public void LockedManifestIdentifiesImmutableModelAsset()
    {
        string repositoryRoot = FindRepositoryRoot();
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "vision-model-baseline.json"),
            Encoding.UTF8));
        JsonElement root = manifest.RootElement;
        string modelId = root.GetProperty("id").GetString()!;
        string trainingRun = root.GetProperty("training_run").GetString()!;
        JsonElement source = root.GetProperty("source");
        string releaseRepository = source.GetProperty("repository").GetString()!;
        string releaseTag = source.GetProperty("release_tag").GetString()!;

        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Matches(new Regex(@"^waybill-yolo11n-obb-v\d+$"), modelId);
        Assert.Matches(new Regex(@"^waybill_yolo11n_obb_v\d+$"), trainingRun);
        Assert.Equal("vision-obb.onnx", root.GetProperty("file_name").GetString());
        Assert.Equal("vision-obb.onnx", root.GetProperty("installed_name").GetString());
        Assert.True(root.GetProperty("size").GetInt64() > 0);
        Assert.Matches(
            new Regex("^[0-9a-fA-F]{64}$"),
            root.GetProperty("sha256").GetString()!);
        Assert.Matches(
            new Regex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"),
            releaseRepository);
        Assert.Matches(new Regex(@"^vision-model-v\d+$"), releaseTag);
        Assert.All(
            source.GetProperty("urls").EnumerateArray(),
            url =>
            {
                string value = url.GetString()!;
                Assert.StartsWith("https://", value, StringComparison.OrdinalIgnoreCase);
                Assert.Contains($"/{releaseRepository}/releases/download/", value);
                Assert.EndsWith($"/{releaseTag}/vision-obb.onnx", value);
            });
    }

    [Fact]
    public void PackagingAndCiRejectMissingOrUnverifiedModel()
    {
        string repositoryRoot = FindRepositoryRoot();
        string publishScript = File.ReadAllText(
            Path.Combine(repositoryRoot, "tools", "Publish-CleanPackage.ps1"),
            Encoding.UTF8);
        string ciWorkflow = File.ReadAllText(
            Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"),
            Encoding.UTF8);
        string releaseWorkflow = File.ReadAllText(
            Path.Combine(repositoryRoot, ".github", "workflows", "release-package.yml"),
            Encoding.UTF8);

        Assert.Contains("Read-VisionModelBaselineManifest", publishScript);
        Assert.Contains("Resolve-VisionModelBaseline", publishScript);
        Assert.Contains("Assert-VisionModelRuntimeFiles", publishScript);
        Assert.Contains(
            "(\"models\\\" + [string]$visionModelBaseline.installed_name)",
            publishScript);
        Assert.Contains("onnxruntime.dll", publishScript);
        Assert.Contains("DirectML.dll", publishScript);
        Assert.Contains("Prepare-PinnedVisionModel.ps1", ciWorkflow);
        Assert.Contains("PACKINGPROOF_WAYBILL_OBB_MODEL", ciWorkflow);
        Assert.Contains("Prepare-PinnedVisionModel.ps1", releaseWorkflow);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
