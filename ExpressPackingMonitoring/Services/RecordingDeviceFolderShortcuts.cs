using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Logging;
using System.IO;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 录像目录里的"昵称 → 设备目录"快捷方式。
///
/// 设备目录名是完整设备号（稳定、唯一，但认不出是谁），昵称随时可改所以不能进目录名。
/// 用快捷方式把两者连起来：用户在资源管理器里看到的是"安卓1"、"打包台A"，
/// 双击进去就是那台设备的录像目录。它取代了早先的"设备对照表.txt"。
///
/// 几条约束：
/// - 快捷方式放在分类目录里（手机备份\安卓1.lnk -> 手机备份\&lt;设备号&gt;），和设备目录并排，
///   不放录像根目录：同一台设备在"手机备份"和"电脑上传"下可能各有一个目录
/// - 只为磁盘上真实存在的设备目录建，不给没有录像的设备造空链接
/// - 只删自己建的：删之前必须确认那个 .lnk 指向本分类目录下的设备目录，
///   绝不碰用户自己放进来的快捷方式或任何其它文件
/// - 昵称经过文件名净化后可能撞名，撞名时带上设备号后缀区分
/// - 建不出来（COM 不可用、目标是只读的 NAS）只记一次日志，绝不影响录像与备份
/// </summary>
internal sealed class RecordingDeviceFolderShortcuts
{
    private const string ShortcutExtension = ".lnk";

    /// <summary>早先的设备对照表：改用快捷方式后顺手清掉，免得留一份过期信息误导用户。</summary>
    internal const string LegacyIndexFileName = "设备对照表.txt";

    private static readonly string[] CategoryDirectories = ["手机备份", "电脑上传"];

    private readonly ShortcutWriter _shortcutWriter;
    private readonly Func<string, string?> _shortcutTargetReader;
    private bool _loggedWriteFailure;

    /// <summary>建快捷方式：成功返回 true，失败时带出原因（原因必须能进日志）。</summary>
    internal delegate bool ShortcutWriter(string linkPath, string targetPath, out string error);

    /// <param name="shortcutWriter">建快捷方式（链接路径、目标目录）。默认走 Windows 外壳。</param>
    /// <param name="shortcutTargetReader">读快捷方式的目标，用于判断某个 .lnk 是否由我们维护。</param>
    internal RecordingDeviceFolderShortcuts(
        ShortcutWriter? shortcutWriter = null,
        Func<string, string?>? shortcutTargetReader = null)
    {
        _shortcutWriter = shortcutWriter ?? WindowsShellShortcut.TryCreate;
        _shortcutTargetReader = shortcutTargetReader ?? WindowsShellShortcut.TryReadTarget;
    }

    /// <summary>
    /// 按当前昵称刷新一个录像根目录下的快捷方式。主机启动、收到备份、设备改名后各调用一次。
    ///
    /// 两张昵称表都要读：手机登记表覆盖全部设备，电脑昵称表是电脑工位名字的权威来源
    /// （见 <see cref="BackupUploadDeviceRegistration"/>），而它只在电脑工位上传时才同步到
    /// 手机登记表。只读手机登记表的话，电脑刚改完名的那段时间快捷方式还挂着老名字。
    /// </summary>
    internal int Refresh(
        string? recordingRoot,
        IEnumerable<MobileOrderReceiverInfo>? devices,
        IEnumerable<RecordingComputerNicknameInfo>? computers = null)
    {
        string root = recordingRoot?.Trim() ?? "";
        if (root.Length == 0)
            return 0;

        IReadOnlyList<ShortcutTarget> knownDevices = MergeDevices(devices, computers);
        TryRemoveLegacyIndex(root);

        int written = 0;
        foreach (string category in CategoryDirectories)
        {
            string categoryPath = Path.Combine(root, category);
            try
            {
                // NAS 离线、这台主机还没收到过备份：都是目录不存在，静默跳过。
                if (!Directory.Exists(categoryPath))
                    continue;

                written += RefreshCategory(categoryPath, knownDevices);
            }
            catch (Exception ex)
            {
                LogWriteFailureOnce($"刷新 {category} 的设备快捷方式失败：{ex.Message}");
            }
        }
        return written;
    }

    /// <summary>快捷方式要指向的一台设备：设备号 + 当前昵称。</summary>
    internal readonly record struct ShortcutTarget(string DeviceId, string DisplayName);

    /// <summary>
    /// 合并两张昵称表。同一台设备同时出现时**电脑昵称表优先**：
    /// 电脑工位的名字只有那张表一个权威来源，手机登记表里的是它上次上传时同步过去的副本。
    /// </summary>
    internal static IReadOnlyList<ShortcutTarget> MergeDevices(
        IEnumerable<MobileOrderReceiverInfo>? devices,
        IEnumerable<RecordingComputerNicknameInfo>? computers)
    {
        var merged = new Dictionary<string, ShortcutTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (MobileOrderReceiverInfo device in devices ?? [])
        {
            string deviceId = device.NodeId?.Trim() ?? "";
            if (deviceId.Length > 0)
                merged[deviceId] = new ShortcutTarget(deviceId, device.NodeName?.Trim() ?? "");
        }

        foreach (RecordingComputerNicknameInfo computer in computers ?? [])
        {
            string deviceId = computer.NodeId?.Trim() ?? "";
            string name = computer.DisplayName?.Trim() ?? "";
            // 名字为空时不要用它盖掉手机登记表里已有的名字。
            if (deviceId.Length > 0 && name.Length > 0)
                merged[deviceId] = new ShortcutTarget(deviceId, name);
        }

        return merged.Values.ToArray();
    }

    private int RefreshCategory(string categoryPath, IReadOnlyList<ShortcutTarget> devices)
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ShortcutTarget device in devices)
        {
            string deviceDirectory = Path.Combine(
                categoryPath,
                RecordingDeviceFolderNaming.BuildDirectoryName(device.DeviceId));
            // 只给真实存在的目录建：没在这台主机留下过录像的设备不该有空链接。
            if (!Directory.Exists(deviceDirectory))
                continue;

            string linkName = BuildShortcutFileName(device, expected);
            expected[linkName] = deviceDirectory;
        }

        RemoveStaleShortcuts(categoryPath, expected);

        int written = 0;
        foreach ((string linkName, string deviceDirectory) in expected)
        {
            string linkPath = Path.Combine(categoryPath, linkName);
            // 已经指对了就不重写：避免每次心跳都动一遍磁盘（NAS 上尤其明显）。
            if (PointsTo(linkPath, deviceDirectory))
                continue;

            if (_shortcutWriter(linkPath, deviceDirectory, out string error))
                written++;
            else
                LogWriteFailureOnce($"无法创建设备快捷方式 {linkName}：{error}");
        }
        return written;
    }

    /// <summary>
    /// 清掉不再需要的快捷方式（设备改名、目录被删）。
    /// 只删指向本分类目录下设备目录的 .lnk —— 用户自己放的快捷方式一律不碰。
    /// </summary>
    private void RemoveStaleShortcuts(
        string categoryPath,
        IReadOnlyDictionary<string, string> expected)
    {
        foreach (string linkPath in Directory.GetFiles(categoryPath, "*" + ShortcutExtension))
        {
            string linkName = Path.GetFileName(linkPath);
            if (expected.ContainsKey(linkName))
                continue;

            string? target = TryReadTarget(linkPath);
            if (target == null || !IsDeviceDirectoryUnder(categoryPath, target))
                continue;

            try
            {
                File.Delete(linkPath);
            }
            catch (Exception ex)
            {
                LogWriteFailureOnce($"无法清理过期的设备快捷方式 {linkName}：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 目标是否就是本分类目录下的一个设备目录。
    ///
    /// 按"目录名 + 父目录名"比，不整条路径比字符串：外壳存 .lnk 时会把路径展开成长路径形式，
    /// 而我们手里的根目录可能是 8.3 短路径（%TEMP% 就是这样），整条比会误判成"不是我们建的"。
    /// 设备目录名是完整设备号，配上父目录名（手机备份/电脑上传）已经足够独特。
    /// </summary>
    private static bool IsDeviceDirectoryUnder(string categoryPath, string target)
    {
        try
        {
            string normalizedTarget = target.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(normalizedTarget);
            return parent != null
                && string.Equals(
                    Path.GetFileName(parent),
                    Path.GetFileName(categoryPath.TrimEnd(Path.DirectorySeparatorChar)),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>链接是否已经指向这个设备目录（同样按目录名比，理由见上）。</summary>
    private bool PointsTo(string linkPath, string deviceDirectory)
    {
        if (!File.Exists(linkPath))
            return false;

        string? target = TryReadTarget(linkPath);
        if (target == null)
            return false;

        return string.Equals(
            Path.GetFileName(target.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)),
            Path.GetFileName(deviceDirectory.TrimEnd(Path.DirectorySeparatorChar)),
            StringComparison.OrdinalIgnoreCase);
    }

    private string? TryReadTarget(string linkPath)
    {
        try
        {
            return _shortcutTargetReader(linkPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 快捷方式文件名。昵称净化后为空或与别的设备撞名时带上设备号后缀区分：
    /// 主机本身不允许同名设备，这里防的是净化造成的撞名。
    /// </summary>
    internal static string BuildShortcutFileName(
        ShortcutTarget device,
        IReadOnlyDictionary<string, string> taken)
    {
        string name = SanitizeFileName(device.DisplayName);
        if (name.Length == 0)
            name = "未命名设备";

        string candidate = name + ShortcutExtension;
        if (!taken.ContainsKey(candidate))
            return candidate;

        string suffix = RecordingDeviceFolderMigrator.BuildLegacyShortId(device.DeviceId);
        return $"{name}-{suffix}{ShortcutExtension}";
    }

    /// <summary>
    /// Windows 保留设备名。<see cref="Path.GetInvalidFileNameChars"/> 不包含它们，
    /// 但用这些名字建文件会失败 —— 昵称上限 20 个字符，用户完全可以把设备命名为"CON"。
    /// </summary>
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static string SanitizeFileName(string? value)
    {
        string name = value?.Trim() ?? "";
        foreach (char invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length == 0)
            return "";

        // 保留设备名要加后缀避开，否则这台设备的快捷方式永远建不出来。
        // 判断按不含扩展名的部分来：CON.lnk 里的 "CON" 才是被保留的那部分。
        if (ReservedFileNames.Contains(name))
            return name + "_";

        // 纯点号（"."、".."）会被当成目录引用。
        return name.All(character => character == '.') ? "" : name;
    }

    /// <summary>
    /// 删掉早先的"设备对照表.txt"：它由主机自己维护，改用快捷方式后留着只会是过期信息。
    /// 只删这个确定由自己写出的文件名。
    /// </summary>
    private void TryRemoveLegacyIndex(string root)
    {
        try
        {
            string legacyPath = Path.Combine(root, LegacyIndexFileName);
            if (File.Exists(legacyPath))
            {
                File.Delete(legacyPath);
                RuntimeLog.Info("MobileBackup", "已删除旧的设备对照表，目录与昵称的对应改用快捷方式");
            }
        }
        catch
        {
            // 删不掉就留着，不影响任何功能。
        }
    }

    private void LogWriteFailureOnce(string message)
    {
        if (_loggedWriteFailure)
            return;

        _loggedWriteFailure = true;
        RuntimeLog.Warn("MobileBackup", $"{message}（只提示一次，不影响录像与备份）");
    }
}
