using ExpressPackingMonitoring.Data;
using ExpressPackingMonitoring.Logging;
using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>一轮迁移的结果，用于日志与"已整理 N 个设备目录"的提示。</summary>
internal readonly record struct DeviceFolderMigrationSummary(
    int MovedDirectories,
    int UpdatedRecords,
    int SkippedDirectories)
{
    internal bool DidAnything => MovedDirectories > 0 || UpdatedRecords > 0;
}

/// <summary>
/// 把老的设备录像目录搬到新命名下（完整设备号），并回写数据库里的绝对路径。
///
/// 两代老目录：<c>设备-&lt;后六位&gt;</c>，以及更早的 <c>&lt;昵称&gt;-&lt;后六位&gt;</c>。
/// 匹配方向是**从数据库往磁盘找**：库里的 <c>SourceDeviceId</c> 是完整设备号，
/// 算出它的后六位再去磁盘上找对应目录 —— 反过来拿磁盘上的六位去猜设备是不可靠的。
/// 两台设备的后六位撞上时两边都跳过并告警，绝不猜。
///
/// 几条安全约束（全部按 STORAGE_AND_DATA_SAFETY 的删除/替换规则来）：
/// - 只移动，不复制、不删除。目标已存在时按日期子目录合并，同名文件一律跳过，绝不覆盖
/// - 数据库回写带乐观条件，且只在文件确实已经在新位置时才改，不制造悬空路径
/// - 搬了目录但还没回写数据库时断电：下一轮按"库里还是老路径、文件已在新位置"把数据库补上，
///   所以整个过程可重复执行、天然自愈，不需要额外的日志文件
/// - NAS 不可达就跳过归档那一侧，本地照常迁移，下次启动再补；绝不因为 NAS 离线卡住本地
/// - 文件被占用（正在录像/上传/归档在读）时跳过，下次再来
/// </summary>
internal sealed class RecordingDeviceFolderMigrator
{
    /// <summary>上传布局的两个分类目录。</summary>
    private static readonly string[] CategoryDirectories = ["手机备份", "电脑上传"];

    private readonly VideoDatabase _database;
    private readonly Func<IReadOnlyList<string>> _rootResolver;

    /// <param name="rootResolver">
    /// 要迁移的根目录集合：本地录像根 + 各个 NAS 归档根。不可达的根会在枚举时被跳过。
    /// </param>
    internal RecordingDeviceFolderMigrator(
        VideoDatabase database,
        Func<IReadOnlyList<string>> rootResolver)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _rootResolver = rootResolver ?? throw new ArgumentNullException(nameof(rootResolver));
    }

    internal DeviceFolderMigrationSummary Run()
    {
        IReadOnlyList<DeviceFolderPathRecord> records = _database.GetDeviceFolderPathRecords();
        if (records.Count == 0)
            return default;

        IReadOnlyDictionary<string, string> devicesByShortId = BuildDeviceLookup(records);
        int moved = 0;
        int skipped = 0;
        foreach (string root in ResolveRoots())
        {
            (int rootMoved, int rootSkipped) = MoveLegacyDirectories(root, devicesByShortId);
            moved += rootMoved;
            skipped += rootSkipped;
        }

        int updated = UpdateDatabasePaths(records, devicesByShortId);
        var summary = new DeviceFolderMigrationSummary(moved, updated, skipped);
        if (summary.DidAnything || skipped > 0)
        {
            RuntimeLog.Info(
                "MobileBackup",
                $"设备目录迁移：已搬 {moved} 个目录，回写 {updated} 条记录，跳过 {skipped} 个目录");
        }
        return summary;
    }

    /// <summary>
    /// 后六位 → 完整设备号。两台设备撞上同一个后六位时这个后六位整条作废
    /// （值置空），它们的目录只能留在原地并告警：猜错归属会把两台设备的录像混在一起。
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildDeviceLookup(
        IReadOnlyList<DeviceFolderPathRecord> records)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string deviceId in records
            .Select(record => record.SourceDeviceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string shortId = BuildLegacyShortId(deviceId);
            if (shortId.Length == 0)
                continue;

            if (lookup.TryGetValue(shortId, out string? existing))
            {
                if (!string.Equals(existing, deviceId, StringComparison.OrdinalIgnoreCase)
                    && existing.Length > 0)
                {
                    lookup[shortId] = "";
                    RuntimeLog.Warn(
                        "MobileBackup",
                        $"两台设备的设备号后六位相同（{shortId}），它们的老目录无法判断归属，"
                            + "保持原样不迁移，请手工确认");
                }
                continue;
            }

            lookup[shortId] = deviceId;
        }
        return lookup;
    }

    /// <summary>老目录名用的短设备号：设备号后六位，只保留字母数字并大写。</summary>
    internal static string BuildLegacyShortId(string? deviceId)
    {
        string normalized = new((deviceId ?? "")
            .Where(char.IsLetterOrDigit)
            .ToArray());
        return normalized.Length switch
        {
            0 => "",
            <= 6 => normalized.ToUpperInvariant(),
            _ => normalized[^6..].ToUpperInvariant(),
        };
    }

    /// <summary>
    /// 从老目录名里取出短设备号。两代格式：<c>设备-XXXXXX</c> 与 <c>&lt;昵称&gt;-XXXXXX</c>，
    /// 都是"最后一个连字符之后的部分"。新格式（完整设备号）返回空，从而被跳过。
    /// </summary>
    internal static string ExtractLegacyShortId(string? directoryName, string? matchedDeviceId)
    {
        string name = directoryName?.Trim() ?? "";
        if (name.Length == 0)
            return "";

        int separator = name.LastIndexOf('-');
        if (separator <= 0 || separator == name.Length - 1)
            return "";

        string candidate = name[(separator + 1)..];
        if (candidate.Length is 0 or > 6 || !candidate.All(char.IsLetterOrDigit))
            return "";

        // 已经是新格式（目录名就是完整设备号）的不要再动：GUID 的最后一段是 12 位，
        // 不会落到这里；但短设备号本身当过目录名的情况要排掉。
        return RecordingDeviceFolderNaming.IsDirectoryNameFor(name, matchedDeviceId)
            ? ""
            : candidate.ToUpperInvariant();
    }

    private IReadOnlyList<string> ResolveRoots()
    {
        IReadOnlyList<string> roots;
        try
        {
            roots = _rootResolver() ?? [];
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("MobileBackup", $"设备目录迁移：解析根目录失败，本轮跳过：{ex.Message}");
            return [];
        }

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private (int Moved, int Skipped) MoveLegacyDirectories(
        string root,
        IReadOnlyDictionary<string, string> devicesByShortId)
    {
        int moved = 0;
        int skipped = 0;
        foreach (string category in CategoryDirectories)
        {
            string categoryPath = Path.Combine(root, category);
            string[] directories;
            try
            {
                // NAS 离线、目录不存在都走这里：静默跳过，下次启动再来。
                if (!Directory.Exists(categoryPath))
                    continue;

                directories = Directory.GetDirectories(categoryPath);
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("MobileBackup", $"设备目录迁移：无法枚举 {category}，本轮跳过：{ex.Message}");
                continue;
            }

            foreach (string directory in directories)
            {
                string name = Path.GetFileName(directory);
                string shortId = ExtractLegacyShortIdForLookup(name, devicesByShortId);
                if (shortId.Length == 0)
                    continue;

                if (!devicesByShortId.TryGetValue(shortId, out string? deviceId)
                    || string.IsNullOrEmpty(deviceId))
                {
                    // 撞号作废，或者磁盘上有库里不认识的目录：都不猜，留在原地。
                    skipped++;
                    continue;
                }

                string target = Path.Combine(
                    categoryPath,
                    RecordingDeviceFolderNaming.BuildDirectoryName(deviceId));
                if (string.Equals(directory, target, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (TryMoveDirectory(directory, target))
                    moved++;
                else
                    skipped++;
            }
        }
        return (moved, skipped);
    }

    private static string ExtractLegacyShortIdForLookup(
        string directoryName,
        IReadOnlyDictionary<string, string> devicesByShortId)
    {
        int separator = directoryName.LastIndexOf('-');
        if (separator <= 0)
            return "";

        string candidate = directoryName[(separator + 1)..].ToUpperInvariant();
        if (!devicesByShortId.ContainsKey(candidate))
            return "";

        devicesByShortId.TryGetValue(candidate, out string? deviceId);
        return ExtractLegacyShortId(directoryName, deviceId);
    }

    /// <summary>
    /// 搬一个设备目录。目标不存在时整体重命名（同卷内是改名，不复制、秒级完成）；
    /// 目标已存在（上次迁移到一半）时按日期子目录合并，同名文件一律跳过。
    /// </summary>
    private static bool TryMoveDirectory(string source, string target)
    {
        try
        {
            if (!Directory.Exists(target))
            {
                Directory.Move(source, target);
                RuntimeLog.Info("MobileBackup", $"设备目录已迁移：{Path.GetFileName(source)} -> {Path.GetFileName(target)}");
                return true;
            }

            return TryMergeDirectory(source, target);
        }
        catch (Exception ex)
        {
            // 占用、权限、跨卷都走这里：不删源、不动数据库，下次启动再试。
            RuntimeLog.Warn(
                "MobileBackup",
                $"设备目录迁移失败，保持原样：{Path.GetFileName(source)}，原因：{ex.Message}");
            return false;
        }
    }

    private static bool TryMergeDirectory(string source, string target)
    {
        bool movedAnything = false;
        foreach (string dateDirectory in Directory.GetDirectories(source))
        {
            string dateTarget = Path.Combine(target, Path.GetFileName(dateDirectory));
            if (!Directory.Exists(dateTarget))
            {
                Directory.Move(dateDirectory, dateTarget);
                movedAnything = true;
                continue;
            }

            foreach (string file in Directory.GetFiles(dateDirectory))
            {
                string fileTarget = Path.Combine(dateTarget, Path.GetFileName(file));
                // 同名文件绝不覆盖：两边可能是不同内容，留在原地交给人判断。
                if (File.Exists(fileTarget))
                    continue;

                File.Move(file, fileTarget);
                movedAnything = true;
            }
        }

        // 顶层散落的文件也搬一下（正常布局不该有，但不能漏）。
        foreach (string file in Directory.GetFiles(source))
        {
            string fileTarget = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(fileTarget))
                continue;

            File.Move(file, fileTarget);
            movedAnything = true;
        }

        TryRemoveEmptyDirectoryTree(source);
        if (movedAnything)
        {
            RuntimeLog.Info(
                "MobileBackup",
                $"设备目录已合并到新目录：{Path.GetFileName(source)} -> {Path.GetFileName(target)}");
        }
        return movedAnything;
    }

    /// <summary>
    /// 只删空目录，而且只删我们刚搬空的那棵。有任何文件留下（同名冲突跳过的）就整棵保留：
    /// 删的必须是确认由自己搬空的空壳，不能顺手清掉还有内容的目录。
    /// </summary>
    private static void TryRemoveEmptyDirectoryTree(string directory)
    {
        try
        {
            foreach (string child in Directory.GetDirectories(directory))
                TryRemoveEmptyDirectoryTree(child);

            if (Directory.GetFileSystemEntries(directory).Length == 0)
                Directory.Delete(directory);
        }
        catch
        {
            // 删不掉就留着，空目录不影响任何功能。
        }
    }

    /// <summary>
    /// 回写数据库路径。只有"文件确实已经在新位置"才改，因此：
    /// 搬完没回写就断电 → 下一轮补上；NAS 离线 → 归档那一侧这轮不动，下次再补。
    /// 两种情况都不会制造指向不存在文件的路径。
    /// </summary>
    private int UpdateDatabasePaths(
        IReadOnlyList<DeviceFolderPathRecord> records,
        IReadOnlyDictionary<string, string> devicesByShortId)
    {
        int updated = 0;
        foreach (DeviceFolderPathRecord record in records)
        {
            string shortId = BuildLegacyShortId(record.SourceDeviceId);
            if (shortId.Length == 0
                || !devicesByShortId.TryGetValue(shortId, out string? deviceId)
                || string.IsNullOrEmpty(deviceId))
            {
                continue;
            }

            string? newFilePath = ResolveMigratedPath(record.FilePath, record.SourceDeviceId);
            string? newArchivePath = ResolveMigratedPath(record.ArchivePath, record.SourceDeviceId);
            if (newFilePath == null && newArchivePath == null)
                continue;

            if (_database.TryMoveDeviceFolderPaths(
                record.Id,
                record.FilePath,
                newFilePath,
                record.ArchivePath,
                newArchivePath))
            {
                updated++;
            }
        }
        return updated;
    }

    /// <summary>
    /// 算出这条路径迁移后的样子。返回 null 表示这一侧不该动：
    /// 路径为空、目录名已经是新格式、不是老格式、或者文件还没出现在新位置。
    /// </summary>
    internal static string? ResolveMigratedPath(string? path, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(deviceId))
            return null;

        string? dateDirectory = Path.GetDirectoryName(path);
        string? deviceDirectory = Path.GetDirectoryName(dateDirectory);
        if (string.IsNullOrEmpty(dateDirectory) || string.IsNullOrEmpty(deviceDirectory))
            return null;

        string deviceDirectoryName = Path.GetFileName(deviceDirectory);
        string expectedName = RecordingDeviceFolderNaming.BuildDirectoryName(deviceId);
        if (string.Equals(deviceDirectoryName, expectedName, StringComparison.OrdinalIgnoreCase))
            return null;

        // 必须确实是这台设备的老目录：后六位对不上就不是，别乱改。
        string shortId = ExtractLegacyShortId(deviceDirectoryName, deviceId);
        if (shortId.Length == 0
            || !string.Equals(shortId, BuildLegacyShortId(deviceId), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? categoryDirectory = Path.GetDirectoryName(deviceDirectory);
        if (string.IsNullOrEmpty(categoryDirectory))
            return null;

        string migrated = Path.Combine(
            categoryDirectory,
            expectedName,
            Path.GetFileName(dateDirectory),
            Path.GetFileName(path));
        try
        {
            // 文件还没到新位置（目录没搬成功、NAS 离线）就不回写，避免悬空路径。
            return File.Exists(migrated) ? migrated : null;
        }
        catch
        {
            return null;
        }
    }
}
