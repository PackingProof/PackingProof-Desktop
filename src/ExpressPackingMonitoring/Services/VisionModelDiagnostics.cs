using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Vision;
using System.IO;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

/// <summary>从 ONNX 内嵌元数据读取实际模型身份，供诊断信息展示。</summary>
internal static class VisionModelDiagnostics
{
    internal const string MetadataPrefix = "packingproof.";

    internal static string Describe(string? modelPath = null)
    {
        modelPath ??= AppPaths.ResolveCameraVisionModelPath();
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            return $"未找到 {AppPaths.CameraVisionModelFileName}";

        try
        {
            VisionModelMetadata? model = VisionObbHost.DetectorFactory.ReadModelMetadata(modelPath);
            if (model == null)
                return $"{Path.GetFileName(modelPath)}（增强识别组件不可用）";
            return Describe(
                model.CustomMetadata,
                Path.GetFileName(modelPath),
                model.RuntimeVersion);
        }
        catch (Exception ex)
        {
            return $"{Path.GetFileName(modelPath)}（读取失败：{ex.GetType().Name}）";
        }
    }

    internal static string Describe(
        IReadOnlyDictionary<string, string> metadata,
        string fileName,
        string runtimeVersion)
    {
        string modelName = ReadString(metadata, "model", "name")
            ?? Path.GetFileNameWithoutExtension(fileName);
        int? trainingRun = ReadInt(metadata, "training", "run");
        string? fingerprint = ReadString(metadata, "dataset", "fingerprint_sha256");
        string? independent = DescribeIndependentEvaluation(metadata);
        bool hasPackingProofMetadata = metadata.Keys.Any(
            key => key.StartsWith(MetadataPrefix, StringComparison.Ordinal));

        var parts = new List<string>
        {
            $"ONNX Runtime {runtimeVersion}",
            fileName,
            modelName
        };
        if (trainingRun is > 0)
            parts.Add($"训练第 {trainingRun} 次");
        if (!string.IsNullOrWhiteSpace(fingerprint))
            parts.Add($"数据 {fingerprint[..Math.Min(12, fingerprint.Length)]}");
        if (!string.IsNullOrWhiteSpace(independent))
            parts.Add(independent);
        if (!hasPackingProofMetadata)
            parts.Add("未携带 PackingProof 元数据");
        return string.Join("；", parts);
    }

    private static string? DescribeIndependentEvaluation(
        IReadOnlyDictionary<string, string> metadata)
    {
        string? summary = DescribeEvaluationSummary(metadata);
        if (!string.IsNullOrWhiteSpace(summary))
            return summary;

        JsonElement results = ReadJsonElement(
            metadata,
            "independent_evaluation",
            "results_by_confidence");
        if (results.ValueKind != JsonValueKind.Object)
            return null;

        JsonProperty? preferred = null;
        foreach (JsonProperty property in results.EnumerateObject())
        {
            if (property.Name == "0.35")
            {
                preferred = property;
                break;
            }
            preferred ??= property;
        }
        if (preferred is not { } selected
            || selected.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return DescribeMetrics(selected.Value);
    }

    private static string? DescribeEvaluationSummary(
        IReadOnlyDictionary<string, string> metadata)
    {
        JsonElement datasets = ReadJsonElement(metadata, "evaluation_summary", "datasets");
        if (datasets.ValueKind != JsonValueKind.Object)
            return null;

        JsonElement dataset = default;
        if (datasets.TryGetProperty("old_independent", out JsonElement independent)
            && independent.ValueKind == JsonValueKind.Object)
        {
            dataset = independent;
        }
        else
        {
            foreach (JsonProperty property in datasets.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    dataset = property.Value;
                    break;
                }
            }
        }
        if (dataset.ValueKind != JsonValueKind.Object)
            return null;

        string? versionKey = ReadModelVersionKey(ReadString(metadata, "model", "name"));
        if (!string.IsNullOrWhiteSpace(versionKey)
            && dataset.TryGetProperty(versionKey, out JsonElement current)
            && current.ValueKind == JsonValueKind.Object)
        {
            return DescribeMetrics(current);
        }

        foreach (JsonProperty property in dataset.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("map50", out _))
            {
                return DescribeMetrics(property.Value);
            }
        }
        return null;
    }

    private static string? ReadModelVersionKey(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return null;
        int marker = modelName.LastIndexOf("_v", StringComparison.Ordinal);
        if (marker < 0 || marker + 1 >= modelName.Length)
            return null;
        string version = modelName[(marker + 1)..];
        return version.Length > 1
            && version[1..].All(char.IsAsciiDigit)
                ? version
                : null;
    }

    private static string? DescribeMetrics(JsonElement metrics)
    {
        double? map50 = ReadDouble(metrics, "map50");
        double? map50_95 = ReadDouble(metrics, "map50_95");
        if (map50 == null)
            return null;
        return map50_95 == null
            ? $"独立 mAP50 {map50.Value:F3}"
            : $"独立 mAP50 {map50.Value:F3} / 50-95 {map50_95.Value:F3}";
    }

    private static string? ReadString(
        IReadOnlyDictionary<string, string> metadata,
        string group,
        string key)
    {
        JsonElement value = ReadJsonElement(metadata, group, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? ReadInt(
        IReadOnlyDictionary<string, string> metadata,
        string group,
        string key)
    {
        JsonElement value = ReadJsonElement(metadata, group, key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
            ? result
            : null;
    }

    private static double? ReadDouble(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out double result)
            ? result
            : null;

    private static JsonElement ReadJsonElement(
        IReadOnlyDictionary<string, string> metadata,
        string group,
        string key)
    {
        if (!metadata.TryGetValue($"{MetadataPrefix}{group}", out string? json)
            || string.IsNullOrWhiteSpace(json))
        {
            return default;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(key, out JsonElement value)
                ? value.Clone()
                : default;
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
