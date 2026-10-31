using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExpressPackingMonitoring.Services;

internal enum RecordingSamplePackageStage
{
    Preparing,
    Capturing,
    Packaging
}

internal sealed record RecordingSamplePackageProgress(
    RecordingSamplePackageStage Stage,
    int Captured,
    int Target,
    int Attempted,
    int CandidateCount);

internal sealed record RecordingSamplePackageResult(
    string ZipPath,
    int ImageCount,
    int AttemptedCount,
    int CandidateCount,
    IReadOnlyList<string> Warnings);

internal sealed record RecordingSampleExtractionResult(
    int AttemptNumber,
    RecordingSamplePick Pick,
    string FileName,
    string OutputPath,
    bool Extracted,
    string Error);

internal delegate bool RecordingSampleFrameExtractor(
    string videoPath,
    string outputPath,
    double seekSeconds,
    CancellationToken cancellationToken,
    out string error);

/// <summary>
/// 从本机历史录像生成轻量训练样本包。只读录像，不修改数据库，
/// 也不携带 videos.db、config.json、日志或录像文件。
/// </summary>
internal sealed class RecordingSamplePackageService
{
    internal const int DefaultTargetCount = RecordingSampleSelectionPolicy.DefaultTargetCount;
    internal const int MaxImageDimension = 1600;
    internal const long MaxImageBytes = 75L * 1024 * 1024;
    internal const int MaxPackagesToKeep = 5;
    internal const int FfmpegThreadCount = 2;
    internal const string ZipPrefix = "PackingProof_RecognitionSamples_";
    internal const string SelectionRuleVersion = "date-stratified-stopreason-window-v4";
    internal static readonly int MaxConcurrentFrameExtractions =
        Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    private readonly string _userDataDir;
    private readonly string _outputDir;
    private readonly string _appVersion;
    private readonly Func<IReadOnlyList<RecordingSampleSource>> _sourcesProvider;
    private readonly RecordingSampleFrameExtractor _frameExtractor;
    private readonly RecordingSampleContributionStore _contributionStore;
    private readonly Random _random;

    internal RecordingSamplePackageService(
        string userDataDir,
        string contributorNodeId,
        string? outputDir = null,
        string? appVersion = null,
        Func<IReadOnlyList<RecordingSampleSource>>? sourcesProvider = null,
        RecordingSampleFrameExtractor? frameExtractor = null,
        Random? random = null,
        RecordingSampleContributionStore? contributionStore = null)
    {
        _userDataDir = userDataDir ?? throw new ArgumentNullException(nameof(userDataDir));
        _outputDir = outputDir ?? Path.Combine(userDataDir, "cache", "feedback");
        _appVersion = string.IsNullOrWhiteSpace(appVersion) ? AppVersion.Current : appVersion;
        _sourcesProvider = sourcesProvider ?? LoadSources;
        _frameExtractor = frameExtractor ?? ExtractFrameWithFfmpeg;
        _contributionStore =
            contributionStore ?? new RecordingSampleContributionStore(
                userDataDir,
                contributorNodeId);
        _random = random ?? Random.Shared;
    }

    internal RecordingSamplePackageResult CreatePackage(
        IProgress<RecordingSamplePackageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDir);
        string stagingDir = Path.Combine(_outputDir, "samples-staging-" + Guid.NewGuid().ToString("N"));
        string imagesDir = Path.Combine(stagingDir, "images");
        Directory.CreateDirectory(imagesDir);
        string? temporaryZipPath = null;
        try
        {
            progress?.Report(new RecordingSamplePackageProgress(
                RecordingSamplePackageStage.Preparing,
                0,
                DefaultTargetCount,
                0,
                0));

            RecordingSampleContributionState contributionState = _contributionStore.Load();
            string deviceFingerprint = contributionState.DeviceFingerprint;
            var sampledSourceFingerprints = contributionState.SampledSourceFingerprints
                .ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<RecordingSampleSource> sources = _sourcesProvider()
                .Where(source => !sampledSourceFingerprints.Contains(
                    RecordingSampleContributionStore.CreateSourceFingerprint(
                        deviceFingerprint,
                        source.Id,
                        source.StartTime,
                        source.DurationSeconds)))
                .ToList();
            IReadOnlyList<RecordingSamplePick> picks =
                RecordingSampleSelectionPolicy.Select(
                    sources,
                    DefaultTargetCount,
                    _random,
                    maxCandidateCount: DefaultTargetCount * 2);
            if (picks.Count == 0)
                throw new InvalidOperationException("没有找到可读取的本地录像");

            var warnings = new List<string>();
            var entries = new List<RecordingSampleManifestEntry>();
            var capturedSourceFingerprints = new List<string>();
            int attempted = 0;
            int nextPickIndex = 0;
            long accumulatedImageBytes = 0;
            bool imageLimitReached = false;
            while (entries.Count < DefaultTargetCount && nextPickIndex < picks.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int batchSize = Math.Min(
                    MaxConcurrentFrameExtractions,
                    DefaultTargetCount - entries.Count);
                var batch = new List<(int AttemptNumber, RecordingSamplePick Pick)>(batchSize);
                while (batch.Count < batchSize && nextPickIndex < picks.Count)
                {
                    RecordingSamplePick pick = picks[nextPickIndex++];
                    batch.Add((attempted + batch.Count + 1, pick));
                }

                attempted += batch.Count;
                Task<RecordingSampleExtractionResult>[] extractionTasks = batch
                    .Select(item => Task.Run(() =>
                        ExtractSampleFrame(
                            item.AttemptNumber,
                            item.Pick,
                            imagesDir,
                            cancellationToken)))
                    .ToArray();
                RecordingSampleExtractionResult[] extractionResults =
                    Task.WhenAll(extractionTasks).GetAwaiter().GetResult();
                foreach (RecordingSampleExtractionResult extraction in extractionResults)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long imageBytes = extraction.Extracted && File.Exists(extraction.OutputPath)
                        ? new FileInfo(extraction.OutputPath).Length
                        : 0;
                    if (imageBytes <= 0)
                    {
                        TryDeleteFile(extraction.OutputPath);
                        warnings.Add(
                            $"第 {extraction.AttemptNumber} 个录像抽帧失败：" +
                            SanitizeExtractionError(extraction.Error));
                    }
                    else if (accumulatedImageBytes + imageBytes > MaxImageBytes)
                    {
                        TryDeleteFile(extraction.OutputPath);
                        warnings.Add("已达到图片体积上限，后续样本未继续抓取");
                        imageLimitReached = true;
                        break;
                    }
                    else
                    {
                        accumulatedImageBytes += imageBytes;
                        string sourceFingerprint =
                            RecordingSampleContributionStore.CreateSourceFingerprint(
                                deviceFingerprint,
                                extraction.Pick.Source.Id,
                                extraction.Pick.Source.StartTime,
                                extraction.Pick.Source.DurationSeconds);
                        entries.Add(new RecordingSampleManifestEntry(
                            "images/" + extraction.FileName,
                            Math.Round(extraction.Pick.PositionRatio, 4),
                            imageBytes,
                            ComputeSha256(extraction.OutputPath),
                            sourceFingerprint,
                            extraction.Pick.CapturePhase == RecordingSampleCapturePhase.Start
                                ? "start"
                                : "end",
                            extraction.Pick.Source.UsesSameCodeCapture
                                ? "same-code"
                                : "continuous",
                            extraction.Pick.Source.StartTime.ToString(
                                "yyyy-MM",
                                CultureInfo.InvariantCulture),
                            extraction.Pick.Source.StartTime.ToString(
                                "HH",
                                CultureInfo.InvariantCulture),
                            extraction.Pick.Source.Mode));
                        capturedSourceFingerprints.Add(sourceFingerprint);
                    }
                }
                if (imageLimitReached)
                    break;

                progress?.Report(new RecordingSamplePackageProgress(
                    RecordingSamplePackageStage.Capturing,
                    entries.Count,
                    DefaultTargetCount,
                    attempted,
                    picks.Count));
            }

            if (entries.Count == 0)
                throw new InvalidOperationException("没有成功抽取任何画面");

            cancellationToken.ThrowIfCancellationRequested();
            string contributionId = Guid.NewGuid().ToString("N");
            WriteManifest(
                stagingDir,
                entries,
                deviceFingerprint,
                contributionId);
            WriteReadme(stagingDir, entries.Count);
            progress?.Report(new RecordingSamplePackageProgress(
                RecordingSamplePackageStage.Packaging,
                entries.Count,
                DefaultTargetCount,
                attempted,
                picks.Count));

            string zipPath = GetUniqueZipPath();
            temporaryZipPath = zipPath + ".partial";
            ZipFile.CreateFromDirectory(
                stagingDir,
                temporaryZipPath,
                CompressionLevel.Fastest,
                includeBaseDirectory: false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryZipPath, zipPath);
            temporaryZipPath = null;
            contributionState.SampledSourceFingerprints.AddRange(
                capturedSourceFingerprints);
            try
            {
                _contributionStore.Save(contributionState);
            }
            catch (Exception ex)
            {
                TryDeleteFile(zipPath);
                throw new InvalidOperationException("无法保存贡献去重状态，样本包未生成", ex);
            }
            PruneOldPackages(warnings);
            return new RecordingSamplePackageResult(
                zipPath,
                entries.Count,
                attempted,
                picks.Count,
                warnings);
        }
        finally
        {
            TryDeleteFile(temporaryZipPath ?? "");
            TryDeleteDirectory(stagingDir);
        }
    }

    private RecordingSampleExtractionResult ExtractSampleFrame(
        int attemptNumber,
        RecordingSamplePick pick,
        string imagesDir,
        CancellationToken cancellationToken)
    {
        string fileName = $"sample_{attemptNumber:D4}.jpg";
        string outputPath = Path.Combine(imagesDir, fileName);
        double seekSeconds = Math.Clamp(
            pick.Source.DurationSeconds * pick.PositionRatio,
            0,
            Math.Max(0, pick.Source.DurationSeconds - 0.05));
        bool extracted = false;
        string error = "";
        try
        {
            extracted = _frameExtractor(
                pick.Source.FilePath,
                outputPath,
                seekSeconds,
                cancellationToken,
                out error);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(outputPath);
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return new RecordingSampleExtractionResult(
            attemptNumber,
            pick,
            fileName,
            outputPath,
            extracted,
            error);
    }

    private IReadOnlyList<RecordingSampleSource> LoadSources()
    {
        string databasePath = Path.Combine(_userDataDir, "videos.db");
        if (!File.Exists(databasePath))
            return [];

        using var database = new VideoDatabase(databasePath);
        return database.QueryVideos(null, null)
            .Where(record =>
                !record.IsDeleted
                && record.EndTime != DateTime.MinValue
                && record.DurationSeconds > 0
                && !string.IsNullOrWhiteSpace(record.FilePath)
                && File.Exists(record.FilePath)
                && IsSupportedVideoFile(record.FilePath))
            .Select(record => new RecordingSampleSource(
                record.Id,
                record.FilePath,
                record.StartTime,
                record.DurationSeconds,
                record.Mode,
                record.StopReason))
            .ToList();
    }

    private void WriteManifest(
        string stagingDir,
        IReadOnlyList<RecordingSampleManifestEntry> entries,
        string deviceFingerprint,
        string contributionId)
    {
        var manifest = new RecordingSampleManifest(
            2,
            DateTimeOffset.UtcNow,
            _appVersion,
            SelectionRuleVersion,
            deviceFingerprint,
            contributionId,
            DefaultTargetCount,
            MaxImageDimension,
            entries.Count,
            entries);
        string json = JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        File.WriteAllText(Path.Combine(stagingDir, "manifest.json"), json, Encoding.UTF8);
    }

    private static void WriteReadme(string stagingDir, int imageCount)
    {
        string content =
            "PackingProof 面单识别样本包\r\n\r\n" +
            $"图片数量：{imageCount}\r\n" +
            "本包只包含从本机历史录像中随机抽取的画面和 manifest.json，不包含录像、数据库、配置或运行日志。\r\n" +
            "manifest.json 包含匿名设备标识和录像来源指纹，仅用于区分贡献和去重。\r\n" +
            "画面可能包含面单、人物或其他现场信息，请检查确认后再交给 PackingProof 维护者。\r\n";
        File.WriteAllText(Path.Combine(stagingDir, "README.txt"), content, Encoding.UTF8);
    }

    private string GetUniqueZipPath()
    {
        string baseName = Path.Combine(_outputDir, $"{ZipPrefix}{DateTime.Now:yyyyMMdd-HHmmss}");
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
                         .EnumerateFiles(_outputDir, $"{ZipPrefix}*.zip", SearchOption.TopDirectoryOnly)
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
                    warnings.Add($"清理旧样本包失败：{old.Name}（{ex.Message}）");
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"清理旧样本包失败：{ex.Message}");
        }
    }

    private static bool IsSupportedVideoFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ExtractFrameWithFfmpeg(
        string videoPath,
        string outputPath,
        double seekSeconds,
        CancellationToken cancellationToken,
        out string error)
    {
        error = "";
        string? ffmpegPath = AppPaths.FindFFmpeg();
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            error = "未找到 FFmpeg";
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };
        foreach (string argument in new[]
                 {
                     "-y",
                     "-nostdin",
                     "-hide_banner",
                     "-loglevel",
                     "error",
                     "-filter_threads",
                     "1",
                     "-threads",
                     FfmpegThreadCount.ToString(CultureInfo.InvariantCulture),
                     "-ss",
                     seekSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                     "-i",
                     videoPath,
                     "-map",
                     "0:v:0",
                     "-map_metadata",
                     "-1",
                     "-an",
                     "-sn",
                     "-dn",
                     "-frames:v",
                     "1",
                     "-threads",
                     FfmpegThreadCount.ToString(CultureInfo.InvariantCulture),
                     "-vf",
                     $"scale={MaxImageDimension}:{MaxImageDimension}:force_original_aspect_ratio=decrease",
                     "-q:v",
                     "4",
                     "-update",
                     "1",
                     outputPath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                error = "FFmpeg 无法启动";
                return false;
            }

            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            });
            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                error = "FFmpeg 抽帧超时";
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            error = stderrTask.Wait(1000) ? stderrTask.Result.Trim() : "";
            return process.ExitCode == 0
                   && File.Exists(outputPath)
                   && new FileInfo(outputPath).Length > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string SanitizeExtractionError(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "未知错误";
        string firstLine = error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "未知错误";
        return firstLine.Length <= 240 ? firstLine : firstLine[..240] + "…";
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
        }
    }
}

internal sealed record RecordingSampleManifest(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string AppVersion,
    string SelectionRule,
    string DeviceFingerprint,
    string ContributionId,
    int TargetCount,
    int MaxImageDimension,
    int ImageCount,
    IReadOnlyList<RecordingSampleManifestEntry> Samples);

internal sealed record RecordingSampleManifestEntry(
    string File,
    double FramePositionRatio,
    long SizeBytes,
    string Sha256,
    string SourceFingerprint,
    string CapturePhase,
    string PackingMode,
    string SourceMonth,
    string SourceHour,
    string Mode);
