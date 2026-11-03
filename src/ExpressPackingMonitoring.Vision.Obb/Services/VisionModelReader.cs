using ExpressPackingMonitoring.Vision;
using Microsoft.ML.OnnxRuntime;
using System.Reflection;

namespace ExpressPackingMonitoring.Vision.Obb;

internal static class VisionModelDiagnostics
{
    internal static VisionModelMetadata? ReadMetadata(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            return null;

        using var session = new InferenceSession(modelPath);
        string runtimeVersion = typeof(InferenceSession).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? typeof(InferenceSession).Assembly.GetName().Version?.ToString()
            ?? "未知";
        return new VisionModelMetadata(
            new Dictionary<string, string>(session.ModelMetadata.CustomMetadataMap),
            runtimeVersion);
    }
}
