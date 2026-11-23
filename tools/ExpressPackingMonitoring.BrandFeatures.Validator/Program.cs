using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ExpressPackingMonitoring.UpdateCore;

return BrandFeaturesValidator.Run(args);

internal static class BrandFeaturesValidator
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        Arguments options;
        try
        {
            if (!TryParseArguments(args, out options, out string argumentError))
            {
                Console.Error.WriteLine(argumentError);
                return 2;
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(options.MigrationOutput))
            {
                WriteMigrationCandidate(options);
                return 0;
            }

            Validate(options);
            return 0;
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void Validate(Arguments options)
    {
        string profileDirectory = Require(
            options.ProfileDirectory,
            "--profile-directory");
        string relativePath = Require(
            options.RelativePath,
            "--relative-path");
        string jsonPath = ResolveProfileRelativeFile(profileDirectory, relativePath);
        string json = File.ReadAllText(jsonPath, Encoding.UTF8);
        ProductFeatures features = ProductFeaturesJson.Parse(json);
        IReadOnlyList<string> errors = ProductFeatureValidator.Validate(
            features,
            new ProductFeatureValidationContext(options.MobileReleaseUrl));
        if (errors.Count == 0)
        {
            Console.WriteLine($"软件能力配置有效：{jsonPath}");
            return;
        }

        throw new InvalidDataException(
            $"软件能力配置 {jsonPath} 无效：{Environment.NewLine}- "
            + string.Join($"{Environment.NewLine}- ", errors));
    }

    private static void WriteMigrationCandidate(Arguments options)
    {
        string outputPath = Path.GetFullPath(
            Require(options.MigrationOutput, "--migration-output"));
        if (File.Exists(outputPath) && !options.Force)
        {
            throw new IOException(
                $"迁移输出已存在：{outputPath}。确认覆盖时请加 --force");
        }
        Directory.CreateDirectory(
            Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("迁移输出必须有父目录"));

        bool mobileAppEnabled = options.MobileAppEnabled
            ?? ReadLegacyMobileAppEnabled(
                Require(options.ProfileDirectory, "--profile-directory"));
        ProductFeatures features = CreateFullFeatures(mobileAppEnabled);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", ProductCapabilitySchemaVersion.Current);
            writer.WriteStartObject("capabilities");
            writer.WriteBoolean("videoRecording", features.VideoRecording);
            writer.WriteBoolean("photoCapture", features.PhotoCapture);
            writer.WriteBoolean("mediaLibrary", features.MediaLibrary);
            writer.WriteBoolean("remoteMediaAccess", features.RemoteMediaAccess);
            writer.WriteBoolean("lanCollaboration", features.LanCollaboration);
            writer.WriteBoolean("computerBackupReceive", features.ComputerBackupReceive);
            writer.WriteBoolean("mobileBackupReceive", features.MobileBackupReceive);
            writer.WriteBoolean("mobileApp", features.MobileApp);
            writer.WriteBoolean("nasArchive", features.NasArchive);
            writer.WriteBoolean("clip", features.Clip);
            writer.WriteBoolean("compress", features.Compress);
            writer.WriteBoolean("extensionApi", features.ExtensionApi);
            writer.WriteBoolean("extensionMarket", features.ExtensionMarket);
            writer.WriteBoolean("userscript", features.Userscript);
            writer.WriteBoolean("orderReceiver", features.OrderReceiver);
            writer.WriteBoolean("tts", features.Tts);
            writer.WriteEndObject();
            writer.WriteStartObject("inputs");
            writer.WriteBoolean("camera", features.Inputs.Camera);
            writer.WriteBoolean("microphone", features.Inputs.Microphone);
            writer.WriteBoolean("scanner", features.Inputs.Scanner);
            writer.WriteBoolean("cameraBarcode", features.Inputs.CameraBarcode);
            writer.WriteBoolean("visionObb", features.Inputs.VisionObb);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        File.WriteAllBytes(
            outputPath,
            stream.ToArray().Append((byte)'\n').ToArray());
        Console.WriteLine($"已生成待审核的软件能力候选：{outputPath}");
    }

    private static ProductFeatures CreateFullFeatures(bool mobileAppEnabled) =>
        new(
            VideoRecording: true,
            PhotoCapture: false,
            MediaLibrary: true,
            RemoteMediaAccess: true,
            LanCollaboration: true,
            ComputerBackupReceive: true,
            MobileBackupReceive: true,
            MobileApp: mobileAppEnabled,
            NasArchive: true,
            Clip: true,
            Compress: true,
            ExtensionApi: true,
            ExtensionMarket: true,
            Userscript: true,
            OrderReceiver: true,
            Tts: true,
            Inputs: new InputFeatures(
                Camera: true,
                Microphone: true,
                Scanner: true,
                CameraBarcode: true,
                VisionObb: true));

    private static string ResolveProfileRelativeFile(
        string profileDirectory,
        string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException(
                "--relative-path 必须是软件配置目录内的相对路径");
        }

        string profileRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(profileDirectory));
        string candidate = Path.GetFullPath(
            Path.Combine(profileRoot, relativePath));
        string prefix = profileRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "--relative-path 不能越出软件配置目录");
        }
        if (!File.Exists(candidate))
        {
            throw new IOException(
                $"找不到软件能力配置：{candidate}");
        }
        return candidate;
    }

    private static string Require(string? value, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"缺少参数 {option}");
        }
        return value;
    }

    private static bool TryParseArguments(
        IReadOnlyList<string> args,
        out Arguments options,
        out string error)
    {
        var parsed = new Arguments();
        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            switch (arg)
            {
                case "--profile-directory":
                    parsed.ProfileDirectory = ReadValue(args, ref index, arg);
                    break;
                case "--relative-path":
                    parsed.RelativePath = ReadValue(args, ref index, arg);
                    break;
                case "--mobile-release-url":
                    parsed.MobileReleaseUrl = ReadValue(args, ref index, arg);
                    break;
                case "--migration-output":
                    parsed.MigrationOutput = ReadValue(args, ref index, arg);
                    break;
                case "--mobile-app-enabled":
                    parsed.MobileAppEnabled = ParseBoolean(
                        ReadValue(args, ref index, arg),
                        arg);
                    break;
                case "--force":
                    parsed.Force = true;
                    break;
                default:
                    options = parsed;
                    error = $"未知参数：{arg}";
                    return false;
            }
        }

        options = parsed;
        error = "";
        return true;
    }

    private static string ReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string option)
    {
        if (++index >= args.Count)
        {
            throw new ArgumentException($"参数 {option} 缺少值");
        }
        return args[index];
    }

    private static bool ParseBoolean(string value, string option) =>
        value.ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new ArgumentException($"{option} 只接受 true 或 false")
        };

    private sealed class Arguments
    {
        public string? ProfileDirectory { get; set; }
        public string? RelativePath { get; set; }
        public string MobileReleaseUrl { get; set; } = "";
        public string? MigrationOutput { get; set; }
        public bool? MobileAppEnabled { get; set; }
        public bool Force { get; set; }
    }

    private static bool ReadLegacyMobileAppEnabled(string profileDirectory)
    {
        string profilePath = Path.Combine(
            Path.GetFullPath(profileDirectory),
            "Brand.props");
        if (!File.Exists(profilePath))
        {
            throw new IOException($"找不到软件配置：{profilePath}");
        }

        XDocument document = XDocument.Parse(
            File.ReadAllText(profilePath, Encoding.UTF8));
        string? value = document
            .Descendants()
            .LastOrDefault(element =>
                element.Name.LocalName == "BrandMobileAppEnabled")
            ?.Value
            .Trim();
        return string.IsNullOrWhiteSpace(value)
            || bool.TryParse(value, out bool enabled) && enabled;
    }
}
