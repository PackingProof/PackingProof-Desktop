using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;

namespace ExpressPackingMonitoring.UpdateCore;

public static class ProductCapabilitySchemaVersion
{
    public const int Current = 1;
}

public sealed record InputFeatures(
    bool Camera,
    bool Microphone,
    bool Scanner,
    bool CameraBarcode,
    bool VisionObb);

public sealed record ProductFeatures(
    bool VideoRecording,
    bool PhotoCapture,
    bool MediaLibrary,
    bool RemoteMediaAccess,
    bool LanCollaboration,
    bool ComputerBackupReceive,
    bool MobileBackupReceive,
    bool MobileApp,
    bool NasArchive,
    bool Clip,
    bool Compress,
    bool ExtensionApi,
    bool ExtensionMarket,
    bool Userscript,
    bool OrderReceiver,
    bool Tts,
    InputFeatures Inputs);

public enum ProductCapabilityId
{
    VideoRecording,
    PhotoCapture,
    MediaLibrary,
    RemoteMediaAccess,
    LanCollaboration,
    ComputerBackupReceive,
    MobileBackupReceive,
    MobileApp,
    NasArchive,
    Clip,
    Compress,
    ExtensionApi,
    ExtensionMarket,
    Userscript,
    OrderReceiver,
    Tts,
    InputCamera,
    InputMicrophone,
    InputScanner,
    InputCameraBarcode,
    InputVisionObb
}

public sealed record ProductFeaturesDocument(
    int SchemaVersion,
    ProductFeatures Features,
    string Sha256);

public sealed record ProductFeatureValidationContext(
    string MobileReleaseUrl);

public static class ProductFeaturesJson
{
    private static readonly string[] RootProperties = ["schemaVersion", "capabilities", "inputs"];

    private static readonly string[] CapabilityProperties =
    [
        "videoRecording",
        "photoCapture",
        "mediaLibrary",
        "remoteMediaAccess",
        "lanCollaboration",
        "computerBackupReceive",
        "mobileBackupReceive",
        "mobileApp",
        "nasArchive",
        "clip",
        "compress",
        "extensionApi",
        "extensionMarket",
        "userscript",
        "orderReceiver",
        "tts"
    ];

    private static readonly string[] InputProperties =
    [
        "camera",
        "microphone",
        "scanner",
        "cameraBarcode",
        "visionObb"
    ];

    public static ProductFeatures Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(System.Text.Encoding.UTF8.GetBytes(json));
    }

    public static ProductFeatures Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (!TryParse(utf8Json, out ProductFeatures? features, out IReadOnlyList<string> errors))
        {
            throw new InvalidDataException(
                "软件能力配置无效：" + string.Join("；", errors));
        }

        return features!;
    }

    public static bool TryParse(
        string json,
        out ProductFeatures? features,
        out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(json);
        return TryParse(
            System.Text.Encoding.UTF8.GetBytes(json),
            out features,
            out errors);
    }

    public static bool TryParse(
        ReadOnlySpan<byte> utf8Json,
        out ProductFeatures? features,
        out IReadOnlyList<string> errors)
    {
        features = null;
        var problems = new List<string>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8Json.ToArray());
            JsonElement root = document.RootElement;
            if (!ValidateObjectShape(root, "$", RootProperties, problems))
            {
                errors = problems;
                return false;
            }

            if (!TryReadInteger(root, "schemaVersion", "$.schemaVersion", problems, out int schemaVersion)
                || schemaVersion != ProductCapabilitySchemaVersion.Current)
            {
                if (schemaVersion != ProductCapabilitySchemaVersion.Current)
                {
                    problems.Add(
                        $"$.schemaVersion 必须为 {ProductCapabilitySchemaVersion.Current}，实际为 {schemaVersion}");
                }
                errors = problems;
                return false;
            }

            Dictionary<string, bool> capabilities = ReadBooleanObject(
                root.GetProperty("capabilities"),
                "$.capabilities",
                CapabilityProperties,
                problems);
            Dictionary<string, bool> inputs = ReadBooleanObject(
                root.GetProperty("inputs"),
                "$.inputs",
                InputProperties,
                problems);
            if (problems.Count > 0)
            {
                errors = problems;
                return false;
            }

            features = new ProductFeatures(
                capabilities["videoRecording"],
                capabilities["photoCapture"],
                capabilities["mediaLibrary"],
                capabilities["remoteMediaAccess"],
                capabilities["lanCollaboration"],
                capabilities["computerBackupReceive"],
                capabilities["mobileBackupReceive"],
                capabilities["mobileApp"],
                capabilities["nasArchive"],
                capabilities["clip"],
                capabilities["compress"],
                capabilities["extensionApi"],
                capabilities["extensionMarket"],
                capabilities["userscript"],
                capabilities["orderReceiver"],
                capabilities["tts"],
                new InputFeatures(
                    inputs["camera"],
                    inputs["microphone"],
                    inputs["scanner"],
                    inputs["cameraBarcode"],
                    inputs["visionObb"]));
            errors = Array.Empty<string>();
            return true;
        }
        catch (JsonException ex)
        {
            problems.Add($"JSON 无效：{ex.Message}");
            errors = problems;
            return false;
        }
    }

    internal static ProductFeaturesDocument ParseDocument(ReadOnlySpan<byte> utf8Json)
    {
        ProductFeatures features = Parse(utf8Json);
        return new ProductFeaturesDocument(
            ProductCapabilitySchemaVersion.Current,
            features,
            Convert.ToHexString(SHA256.HashData(utf8Json)).ToLowerInvariant());
    }

    public static IReadOnlyDictionary<ProductCapabilityId, bool> ToCapabilityMap(
        ProductFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);
        return new Dictionary<ProductCapabilityId, bool>
        {
            [ProductCapabilityId.VideoRecording] = features.VideoRecording,
            [ProductCapabilityId.PhotoCapture] = features.PhotoCapture,
            [ProductCapabilityId.MediaLibrary] = features.MediaLibrary,
            [ProductCapabilityId.RemoteMediaAccess] = features.RemoteMediaAccess,
            [ProductCapabilityId.LanCollaboration] = features.LanCollaboration,
            [ProductCapabilityId.ComputerBackupReceive] = features.ComputerBackupReceive,
            [ProductCapabilityId.MobileBackupReceive] = features.MobileBackupReceive,
            [ProductCapabilityId.MobileApp] = features.MobileApp,
            [ProductCapabilityId.NasArchive] = features.NasArchive,
            [ProductCapabilityId.Clip] = features.Clip,
            [ProductCapabilityId.Compress] = features.Compress,
            [ProductCapabilityId.ExtensionApi] = features.ExtensionApi,
            [ProductCapabilityId.ExtensionMarket] = features.ExtensionMarket,
            [ProductCapabilityId.Userscript] = features.Userscript,
            [ProductCapabilityId.OrderReceiver] = features.OrderReceiver,
            [ProductCapabilityId.Tts] = features.Tts,
            [ProductCapabilityId.InputCamera] = features.Inputs.Camera,
            [ProductCapabilityId.InputMicrophone] = features.Inputs.Microphone,
            [ProductCapabilityId.InputScanner] = features.Inputs.Scanner,
            [ProductCapabilityId.InputCameraBarcode] = features.Inputs.CameraBarcode,
            [ProductCapabilityId.InputVisionObb] = features.Inputs.VisionObb
        }.ToFrozenDictionary();
    }

    private static Dictionary<string, bool> ReadBooleanObject(
        JsonElement element,
        string path,
        IReadOnlyList<string> expectedProperties,
        ICollection<string> problems)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (!ValidateObjectShape(element, path, expectedProperties, problems))
        {
            return result;
        }

        foreach (string propertyName in expectedProperties)
        {
            if (!TryReadBoolean(
                    element,
                    propertyName,
                    $"{path}.{propertyName}",
                    problems,
                    out bool value))
            {
                continue;
            }
            result[propertyName] = value;
        }
        return result;
    }

    private static bool ValidateObjectShape(
        JsonElement element,
        string path,
        IReadOnlyList<string> expectedProperties,
        ICollection<string> problems)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{path} 必须是对象");
            return false;
        }

        var expected = expectedProperties.ToHashSet(StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        bool valid = true;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!observed.Add(property.Name))
            {
                problems.Add($"{path} 包含重复字段 {property.Name}");
                valid = false;
            }
            if (!expected.Contains(property.Name))
            {
                problems.Add($"{path} 包含未知字段 {property.Name}");
                valid = false;
            }
        }

        foreach (string propertyName in expectedProperties)
        {
            if (!observed.Contains(propertyName))
            {
                problems.Add($"{path} 缺少字段 {propertyName}");
                valid = false;
            }
        }
        return valid;
    }

    private static bool TryReadBoolean(
        JsonElement element,
        string propertyName,
        string path,
        ICollection<string> problems,
        out bool value)
    {
        JsonElement property = element.GetProperty(propertyName);
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            problems.Add($"{path} 必须是 true 或 false");
            value = false;
            return false;
        }
        value = property.GetBoolean();
        return true;
    }

    private static bool TryReadInteger(
        JsonElement element,
        string propertyName,
        string path,
        ICollection<string> problems,
        out int value)
    {
        JsonElement property = element.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out value))
        {
            problems.Add($"{path} 必须是整数");
            value = 0;
            return false;
        }
        return true;
    }
}

public static class ProductFeatureValidator
{
    public static IReadOnlyList<string> Validate(
        ProductFeatures features,
        ProductFeatureValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(context);
        var errors = new List<string>(ValidateRelationships(features));
        Require(errors,
            features.MobileApp && string.IsNullOrWhiteSpace(context.MobileReleaseUrl),
            "mobileApp 需要配置手机发布地址");
        return errors;
    }

    public static IReadOnlyList<string> ValidateRelationships(ProductFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);
        var errors = new List<string>();
        Require(errors, features.PhotoCapture && !features.MediaLibrary,
            "photoCapture 依赖 mediaLibrary");
        Require(errors, features.VideoRecording && !features.MediaLibrary,
            "videoRecording 依赖 mediaLibrary");
        Require(errors, features.VideoRecording && !features.Inputs.Camera,
            "videoRecording 依赖 inputs.camera");
        Require(errors, features.PhotoCapture && !features.Inputs.Camera,
            "photoCapture 依赖 inputs.camera");
        Require(errors, features.Inputs.CameraBarcode && !features.Inputs.Camera,
            "inputs.cameraBarcode 依赖 inputs.camera");
        Require(errors, features.Inputs.VisionObb && !features.Inputs.Camera,
            "inputs.visionObb 依赖 inputs.camera");
        Require(errors, features.Inputs.VisionObb && !features.VideoRecording,
            "inputs.visionObb 依赖 videoRecording");
        Require(errors, features.Inputs.Microphone && !features.VideoRecording,
            "inputs.microphone 依赖 videoRecording");
        Require(errors, features.RemoteMediaAccess && !features.MediaLibrary,
            "remoteMediaAccess 依赖 mediaLibrary");
        Require(errors, features.MobileBackupReceive && !features.MediaLibrary,
            "mobileBackupReceive 依赖 mediaLibrary");
        Require(errors, features.ComputerBackupReceive && !features.MediaLibrary,
            "computerBackupReceive 依赖 mediaLibrary");
        Require(errors, features.NasArchive && !features.MediaLibrary,
            "nasArchive 依赖 mediaLibrary");
        Require(errors, features.Clip && !features.MediaLibrary,
            "clip 依赖 mediaLibrary");
        Require(errors, features.Compress && !features.MediaLibrary,
            "compress 依赖 mediaLibrary");
        Require(errors, features.ExtensionMarket && !features.ExtensionApi,
            "extensionMarket 依赖 extensionApi");

        IReadOnlyList<string> routeErrors = ProductWebRouteGroups.ValidateDefinitions();
        errors.AddRange(routeErrors);
        return errors;
    }

    public static void ThrowIfRelationshipsInvalid(ProductFeatures features)
    {
        IReadOnlyList<string> errors = ValidateRelationships(features);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "软件能力配置存在冲突：" + string.Join("；", errors));
        }
    }

    public static void ThrowIfInvalid(
        ProductFeatures features,
        ProductFeatureValidationContext context)
    {
        IReadOnlyList<string> errors = Validate(features, context);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(
                "软件能力配置存在冲突：" + string.Join("；", errors));
        }
    }

    private static void Require(
        ICollection<string> errors,
        bool invalid,
        string message)
    {
        if (invalid)
        {
            errors.Add(message);
        }
    }
}
