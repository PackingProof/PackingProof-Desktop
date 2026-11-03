using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class VisionModelDiagnosticsTests
{
    [Fact]
    public void Describe_IncludesTrainingAndIndependentEvaluationMetadata()
    {
        var metadata = new Dictionary<string, string>
        {
            ["packingproof.model"] = """{"name":"wallbill_yolo11n_obb_v3"}""",
            ["packingproof.training"] = """{"run":3}""",
            ["packingproof.dataset"] = """{"fingerprint_sha256":"abcdef1234567890"}""",
            ["packingproof.independent_evaluation"] =
                """{"results_by_confidence":{"0.35":{"map50":0.98,"map50_95":0.919}}}"""
        };

        string description = VisionModelDiagnostics.Describe(
            metadata,
            "vision-obb.onnx",
            "1.23.0");

        Assert.Contains("vision-obb.onnx", description);
        Assert.Contains("wallbill_yolo11n_obb_v3", description);
        Assert.Contains("训练第 3 次", description);
        Assert.Contains("abcdef123456", description);
        Assert.Contains("独立 mAP50 0.980 / 50-95 0.919", description);
    }

    [Fact]
    public void Describe_ReadsCurrentModelFromEvaluationSummary()
    {
        var metadata = new Dictionary<string, string>
        {
            ["packingproof.model"] = """{"name":"wallbill_yolo11n_obb_v6"}""",
            ["packingproof.evaluation_summary"] =
                """
                {
                  "datasets": {
                    "old_independent": {
                      "v4": {"map50": 0.985, "map50_95": 0.938},
                      "v6": {"map50": 0.981, "map50_95": 0.932}
                    }
                  }
                }
                """
        };

        string description = VisionModelDiagnostics.Describe(
            metadata,
            "vision-obb.onnx",
            "1.23.0");

        Assert.Contains("独立 mAP50 0.981 / 50-95 0.932", description);
        Assert.DoesNotContain("0.985", description);
    }

    [Fact]
    public void Describe_MarksCustomModelWithoutPackingProofMetadata()
    {
        string description = VisionModelDiagnostics.Describe(
            new Dictionary<string, string>(),
            "vision-obb.onnx",
            "1.23.0");

        Assert.Contains("vision-obb.onnx", description);
        Assert.Contains("未携带 PackingProof 元数据", description);
    }

    [Fact]
    public void Describe_ReadsPackingProofMetadataFromConfiguredModel()
    {
        string? modelPath = Environment.GetEnvironmentVariable(
            "PACKINGPROOF_WAYBILL_OBB_MODEL");
        if (string.IsNullOrWhiteSpace(modelPath))
            return;

        string description = VisionModelDiagnostics.Describe(modelPath);

        Assert.Contains("vision-obb.onnx", description);
        Assert.Contains("训练第 ", description);
        Assert.Contains("独立 mAP50", description);
    }
}
