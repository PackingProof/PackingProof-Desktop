using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// “一键反馈”：把运行日志、脱敏后的配置和完整数据库快照打包成 ZIP，
/// 供用户通过微信/网盘/邮件手动发送给开发者。全程本地处理。
/// </summary>
internal sealed class FeedbackPackageService
{
    internal const int MaxLogBytes = 2 * 1024 * 1024;
    internal const int MaxPackagesToKeep = 10;
    private const string RedactedMarker = "已脱敏";
    private const string ZipPrefix = "PackingProof_Feedback_";

    private readonly string _userDataDir;
    private readonly string _feedbackDir;
    private readonly string _appVersion;
    private readonly string _commitId;
    private readonly Func<FeedbackHardwareInfo> _hardwareInfoProvider;

    internal FeedbackPackageService(
        string userDataDir,
        string? feedbackDir = null,
        string? appVersion = null,
        string? commitId = null,
        Func<FeedbackHardwareInfo>? hardwareInfoProvider = null)
    {
        _userDataDir = userDataDir ?? throw new ArgumentNullException(nameof(userDataDir));
        _feedbackDir = feedbackDir ?? Path.Combine(userDataDir, "cache", "feedback");
        _appVersion = string.IsNullOrWhiteSpace(appVersion) ? AppVersion.Current : appVersion;
        _commitId = string.IsNullOrWhiteSpace(commitId) ? AppVersion.CommitShortId : commitId;
        _hardwareInfoProvider = hardwareInfoProvider ?? FeedbackHardwareInfo.Collect;
    }

    internal string CreatePackage(out IReadOnlyList<string> warnings)
    {
        var warningList = new List<string>();
        Directory.CreateDirectory(_feedbackDir);
        string stagingDir = Path.Combine(_feedbackDir, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);
        try
        {
            CopyLogs(stagingDir, warningList);
            CopyConfig(stagingDir, warningList);
            CopyDatabaseSnapshot(stagingDir, warningList);
            WriteInfoFile(stagingDir, warningList);

            string zipPath = GetUniqueZipPath();
            ZipFile.CreateFromDirectory(
                stagingDir,
                zipPath,
                CompressionLevel.Fastest,
                includeBaseDirectory: false);
            PruneOldPackages(warningList);
            warnings = warningList;
            return zipPath;
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    internal static string RedactSensitiveConfig(string raw)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(raw);
            if (root is JsonObject obj)
            {
                RedactKey(obj, "WebAccessKey");
                RedactKey(obj, "LastKnownHostAccessKey");
                return obj.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            }
        }
        catch
        {
            // 解析失败时按原样打包，便于排查配置损坏问题。
        }
        return raw;
    }

    /// <summary>
    /// 在反馈包旁边生成已内嵌压缩包附件的 .eml 邮件草稿。
    /// mailto 无法携带附件，所以用标准 MIME 草稿让默认邮件客户端直接带附件打开。
    /// </summary>
    internal string CreateFeedbackEml(string zipPath, string recipientEmail)
    {
        string emlPath = Path.ChangeExtension(zipPath, ".eml");
        string subject = $"PackingProof 反馈（{_appVersion}）";
        string body = BuildFeedbackBody(zipPath, _appVersion, _commitId);
        File.WriteAllText(
            emlPath,
            BuildEml(recipientEmail, subject, body, zipPath),
            Encoding.UTF8);
        PruneOldFiles($"{ZipPrefix}*.eml", new List<string>());
        return emlPath;
    }

    internal static string BuildFeedbackBody(string zipPath, string appVersion, string commitId)
    {
        var builder = new StringBuilder();
        builder.AppendLine("反馈信息");
        builder.AppendLine();
        builder.AppendLine("问题描述：");
        builder.AppendLine("（请描述遇到的问题）");
        builder.AppendLine();
        builder.AppendLine("复现步骤：");
        builder.AppendLine("1.");
        builder.AppendLine("2.");
        builder.AppendLine();
        builder.AppendLine("期望行为：");
        builder.AppendLine("（期望的结果）");
        builder.AppendLine();
        builder.AppendLine("实际行为：");
        builder.AppendLine("（实际的结果）");
        builder.AppendLine();
        builder.AppendLine($"应用版本：{appVersion}");
        builder.AppendLine($"Commit：{commitId}");
        builder.AppendLine($"操作系统：{Environment.OSVersion}");
        builder.AppendLine($"反馈包生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"反馈包：{Path.GetFileName(zipPath)}（已作为附件）");
        return builder.ToString();
    }

    private static string BuildEml(
        string recipientEmail,
        string subject,
        string body,
        string zipPath)
    {
        string boundary = $"----=_PackingProof_{Guid.NewGuid():N}";
        string fileName = Path.GetFileName(zipPath);
        var builder = new StringBuilder();
        builder.AppendLine("MIME-Version: 1.0");
        builder.AppendLine($"To: {recipientEmail}");
        builder.AppendLine($"Subject: {EncodeHeader(subject)}");
        builder.AppendLine($"Content-Type: multipart/mixed; boundary=\"{boundary}\"");
        builder.AppendLine();
        builder.AppendLine($"--{boundary}");
        builder.AppendLine("Content-Type: text/plain; charset=\"utf-8\"");
        builder.AppendLine("Content-Transfer-Encoding: base64");
        builder.AppendLine();
        builder.AppendLine(Base64Lines(body));
        builder.AppendLine($"--{boundary}");
        builder.AppendLine($"Content-Type: application/zip; name=\"{fileName}\"");
        builder.AppendLine("Content-Transfer-Encoding: base64");
        builder.AppendLine($"Content-Disposition: attachment; filename=\"{fileName}\"");
        builder.AppendLine();
        builder.AppendLine(Base64Lines(File.ReadAllBytes(zipPath)));
        builder.AppendLine($"--{boundary}--");
        return builder.ToString();
    }

    private static string EncodeHeader(string value) =>
        $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}?=";

    private static string Base64Lines(string text) =>
        Base64Lines(Encoding.UTF8.GetBytes(text));

    private static string Base64Lines(byte[] bytes)
    {
        string base64 = Convert.ToBase64String(bytes);
        var builder = new StringBuilder();
        for (int index = 0; index < base64.Length; index += 76)
        {
            int length = Math.Min(76, base64.Length - index);
            builder.Append(base64, index, length).AppendLine();
        }
        return builder.ToString();
    }

    private void CopyLogs(string stagingDir, List<string> warnings)
    {
        string logDir = Path.Combine(_userDataDir, "log");
        if (!Directory.Exists(logDir))
        {
            warnings.Add("未找到 log 目录，已跳过日志");
            return;
        }

        string stagingLogDir = Path.Combine(stagingDir, "log");
        Directory.CreateDirectory(stagingLogDir);
        foreach (string sourcePath in Directory
            .EnumerateFiles(logDir, "*.log", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string destinationPath = Path.Combine(stagingLogDir, Path.GetFileName(sourcePath));
            if (!TryCopyTail(sourcePath, destinationPath))
                warnings.Add($"日志文件读取失败已跳过：{Path.GetFileName(sourcePath)}");
        }
    }

    private static bool TryCopyTail(string sourcePath, string destinationPath)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var input = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var output = new FileStream(
                    destinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);
                if (input.Length > MaxLogBytes)
                    input.Seek(input.Length - MaxLogBytes, SeekOrigin.Begin);
                input.CopyTo(output);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
        return false;
    }

    private void CopyConfig(string stagingDir, List<string> warnings)
    {
        string sourcePath = Path.Combine(_userDataDir, "config.json");
        if (!File.Exists(sourcePath))
        {
            warnings.Add("未找到 config.json，已跳过配置");
            return;
        }

        try
        {
            string raw = File.ReadAllText(sourcePath);
            File.WriteAllText(
                Path.Combine(stagingDir, "config.json"),
                RedactSensitiveConfig(raw),
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            warnings.Add($"config.json 处理失败：{ex.Message}");
        }
    }

    private void CopyDatabaseSnapshot(string stagingDir, List<string> warnings)
    {
        string dbPath = Path.Combine(_userDataDir, "videos.db");
        if (!File.Exists(dbPath))
        {
            warnings.Add("未找到 videos.db，已跳过数据库快照");
            return;
        }

        try
        {
            VideoDatabase.CreateFeedbackSnapshot(dbPath, Path.Combine(stagingDir, "videos.db"));
        }
        catch (Exception ex)
        {
            warnings.Add($"数据库快照失败：{ex.Message}");
        }
    }

    private void WriteInfoFile(string stagingDir, List<string> warnings)
    {
        var builder = new StringBuilder();
        builder.AppendLine("PackingProof 反馈信息");
        builder.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"应用版本：{_appVersion}");
        builder.AppendLine($"Commit：{_commitId}");
        builder.AppendLine($"操作系统：{Environment.OSVersion}");
        builder.AppendLine($"用户数据目录：{_userDataDir}");
        AppendHardwareInfo(builder, warnings);
        builder.AppendLine();
        builder.AppendLine("包含文件：");
        foreach (string file in Directory
            .EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {Path.GetRelativePath(stagingDir, file)}（{new FileInfo(file).Length} 字节）");
        }
        builder.AppendLine();
        if (warnings.Count == 0)
        {
            builder.AppendLine("提示：无");
        }
        else
        {
            builder.AppendLine("提示/缺失项：");
            foreach (string warning in warnings)
                builder.AppendLine($"- {warning}");
        }
        builder.AppendLine();
        builder.AppendLine("注意：本压缩包包含完整订单数据库与本地配置，请勿转发给无关人员");
        File.WriteAllText(
            Path.Combine(stagingDir, "feedback-info.txt"),
            builder.ToString(),
            Encoding.UTF8);
    }

    private void AppendHardwareInfo(StringBuilder builder, List<string> warnings)
    {
        try
        {
            FeedbackHardwareInfo info = _hardwareInfoProvider();
            builder.AppendLine();
            builder.AppendLine("硬件与编码环境：");
            builder.AppendLine($"- CPU：{ValueOrUnavailable(info.CpuName)}");
            builder.AppendLine($"- 逻辑处理器：{info.LogicalProcessorCount}");
            builder.AppendLine($"- 物理内存：{FormatMemory(info.TotalPhysicalMemoryBytes)}");
            if (info.Gpus.Count == 0)
            {
                builder.AppendLine("- GPU：未获取");
            }
            else
            {
                for (int index = 0; index < info.Gpus.Count; index++)
                {
                    FeedbackGpuInfo gpu = info.Gpus[index];
                    string driver = string.IsNullOrWhiteSpace(gpu.DriverVersion)
                        ? "驱动版本未获取"
                        : $"驱动 {gpu.DriverVersion}";
                    builder.AppendLine($"- GPU {index + 1}：{gpu.Name}（{driver}）");
                }
            }
            builder.AppendLine($"- FFmpeg：{ValueOrUnavailable(info.FfmpegVersion)}");
        }
        catch (Exception ex)
        {
            warnings.Add($"硬件与编码环境采集失败：{ex.Message}");
        }
    }

    private static string ValueOrUnavailable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "未获取" : value.Trim();

    private static string FormatMemory(ulong bytes) =>
        bytes == 0 ? "未获取" : $"{bytes / 1024d / 1024d / 1024d:F1} GB";

    private string GetUniqueZipPath()
    {
        string baseName = Path.Combine(_feedbackDir, $"{ZipPrefix}{DateTime.Now:yyyyMMdd-HHmmss}");
        string candidate = baseName + ".zip";
        int suffix = 2;
        while (File.Exists(candidate))
            candidate = $"{baseName}_{suffix++}.zip";
        return candidate;
    }

    private void PruneOldPackages(List<string> warnings)
    {
        PruneOldFiles($"{ZipPrefix}*.zip", warnings);
        PruneOldFiles($"{ZipPrefix}*.eml", warnings);
    }

    private void PruneOldFiles(string pattern, List<string> warnings)
    {
        try
        {
            foreach (FileInfo old in Directory
                .EnumerateFiles(_feedbackDir, pattern, SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Skip(MaxPackagesToKeep))
            {
                try
                {
                    old.Delete();
                }
                catch (Exception ex)
                {
                    warnings.Add($"清理旧反馈包失败：{old.Name}（{ex.Message}）");
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"清理旧反馈包失败：{ex.Message}");
        }
    }

    private static void RedactKey(JsonObject obj, string key)
    {
        if (obj[key] is JsonNode node && node.GetValueKind() == JsonValueKind.String)
            obj[key] = RedactedMarker;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            // 临时目录清理失败不影响反馈包本身。
        }
    }
}

internal sealed record FeedbackGpuInfo(string Name, string DriverVersion);

internal sealed record FeedbackHardwareInfo(
    string CpuName,
    int LogicalProcessorCount,
    ulong TotalPhysicalMemoryBytes,
    IReadOnlyList<FeedbackGpuInfo> Gpus,
    string FfmpegVersion)
{
    private const string DisplayAdapterClassPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    internal static FeedbackHardwareInfo Collect()
    {
        return new FeedbackHardwareInfo(
            ReadCpuName(),
            Environment.ProcessorCount,
            ReadTotalPhysicalMemory(),
            ReadGpus(),
            ReadFfmpegVersion());
    }

    private static string ReadCpuName()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static IReadOnlyList<FeedbackGpuInfo> ReadGpus()
    {
        var result = new List<FeedbackGpuInfo>();
        try
        {
            using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(DisplayAdapterClassPath);
            if (classKey == null)
                return result;

            foreach (string subKeyName in classKey.GetSubKeyNames())
            {
                using RegistryKey? adapterKey = classKey.OpenSubKey(subKeyName);
                string matchingDeviceId = adapterKey?.GetValue("MatchingDeviceId")?.ToString() ?? "";
                if (!matchingDeviceId.StartsWith("PCI\\VEN_", StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = adapterKey?.GetValue("DriverDesc")?.ToString()?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                string driverVersion = adapterKey?.GetValue("DriverVersion")?.ToString()?.Trim() ?? "";
                var gpu = new FeedbackGpuInfo(name, driverVersion);
                if (!result.Contains(gpu))
                    result.Add(gpu);
            }
        }
        catch
        {
            // 单项采集失败不应阻止用户生成反馈包。
        }
        return result;
    }

    private static ulong ReadTotalPhysicalMemory()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhysical : 0;
    }

    private static string ReadFfmpegVersion()
    {
        string ffmpegPath = AppPaths.FindFFmpeg();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            return "";

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "";
            }
            if (!Task.WaitAll([stdout, stderr], 1000))
                return "";
            string output = string.IsNullOrWhiteSpace(stdout.Result) ? stderr.Result : stdout.Result;
            return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
