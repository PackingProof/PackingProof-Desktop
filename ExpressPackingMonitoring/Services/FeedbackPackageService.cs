using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using System.IO;
using System.IO.Compression;
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

    internal FeedbackPackageService(
        string userDataDir,
        string? feedbackDir = null,
        string? appVersion = null,
        string? commitId = null)
    {
        _userDataDir = userDataDir ?? throw new ArgumentNullException(nameof(userDataDir));
        _feedbackDir = feedbackDir ?? Path.Combine(userDataDir, "backups", "feedback");
        _appVersion = string.IsNullOrWhiteSpace(appVersion) ? AppVersion.Current : appVersion;
        _commitId = string.IsNullOrWhiteSpace(commitId) ? AppVersion.CommitShortId : commitId;
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
        builder.AppendLine("PackingProof 一键反馈信息");
        builder.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"应用版本：{_appVersion}");
        builder.AppendLine($"Commit：{_commitId}");
        builder.AppendLine($"操作系统：{Environment.OSVersion}");
        builder.AppendLine($"用户数据目录：{_userDataDir}");
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
        builder.AppendLine("注意：本压缩包包含完整订单数据库与本地配置，请勿转发给无关人员。");
        File.WriteAllText(
            Path.Combine(stagingDir, "feedback-info.txt"),
            builder.ToString(),
            Encoding.UTF8);
    }

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
        try
        {
            foreach (FileInfo old in Directory
                .EnumerateFiles(_feedbackDir, $"{ZipPrefix}*.zip", SearchOption.TopDirectoryOnly)
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
