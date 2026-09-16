using ExpressPackingMonitoring.Helpers;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 录像目录里的"昵称 → 设备目录"快捷方式。
///
/// 设备目录名是完整设备号（稳定唯一但认不出是谁），昵称随时可改所以不进目录名，
/// 两者靠快捷方式连起来。这里守的性质：只给真实存在的设备目录建、改名后清掉旧链接、
/// 绝不碰用户自己放的文件、外壳不可用时不影响录像。
/// </summary>
public sealed class RecordingDeviceFolderShortcutsTests : IDisposable
{
    private const string DeviceA = "11111111-1111-1111-1111-1111119abcdef";
    private const string DeviceB = "22222222-2222-2222-2222-222222123456";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"device-shortcuts-{Guid.NewGuid():N}");

    /// <summary>假的快捷方式实现：把目标路径写进文本文件，测试里可读可比。</summary>
    private readonly Dictionary<string, string> _written = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void CreatesOneShortcutPerExistingDeviceDirectory()
    {
        CreateDeviceDirectory("手机备份", DeviceA);
        CreateDeviceDirectory("电脑上传", DeviceB);

        int written = CreateShortcuts().Refresh(
            _root,
            [Device(DeviceA, "安卓1"), Device(DeviceB, "打包台A")]);

        Assert.Equal(2, written);
        Assert.Equal(
            DeviceDirectory("手机备份", DeviceA),
            _written[Path.Combine(_root, "手机备份", "安卓1.lnk")]);
        Assert.Equal(
            DeviceDirectory("电脑上传", DeviceB),
            _written[Path.Combine(_root, "电脑上传", "打包台A.lnk")]);
    }

    /// <summary>
    /// 电脑工位的名字以电脑昵称表为准：那是它唯一的权威来源，手机登记表里只是上次上传时
    /// 同步过去的副本。工位改完名还没再上传时，快捷方式必须已经是新名字。
    /// </summary>
    [Fact]
    public void ComputerWorkstationNameComesFromComputerNicknameTable()
    {
        string deviceDirectory = CreateDeviceDirectory("电脑上传", DeviceB);

        CreateShortcuts().Refresh(
            _root,
            [Device(DeviceB, "电脑1")],
            [new RecordingComputerNicknameInfo(DeviceB, "打包工位A", DateTime.UtcNow)]);

        Assert.Equal(
            deviceDirectory,
            _written[Path.Combine(_root, "电脑上传", "打包工位A.lnk")]);
        Assert.False(
            File.Exists(Path.Combine(_root, "电脑上传", "电脑1.lnk")),
            "手机登记表里的旧名字不该再建链接");
    }

    /// <summary>
    /// 电脑昵称表里名字为空时不能盖掉手机登记表里已有的名字，
    /// 否则会退化成"未命名设备"。
    /// </summary>
    [Fact]
    public void EmptyComputerNicknameDoesNotOverrideRegistryName()
    {
        CreateDeviceDirectory("电脑上传", DeviceB);

        CreateShortcuts().Refresh(
            _root,
            [Device(DeviceB, "电脑1")],
            [new RecordingComputerNicknameInfo(DeviceB, "   ", DateTime.UtcNow)]);

        Assert.True(_written.ContainsKey(Path.Combine(_root, "电脑上传", "电脑1.lnk")));
    }

    /// <summary>
    /// 昵称是 Windows 保留设备名（CON、PRN、COM1…）时要避开：
    /// GetInvalidFileNameChars 不包含它们，但拿它们建文件会直接失败，
    /// 那台设备的快捷方式就永远建不出来。昵称上限 20 字符，用户真能这么起名。
    /// </summary>
    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void ReservedWindowsNamesStillProduceAUsableShortcut(string nickname)
    {
        string deviceDirectory = CreateDeviceDirectory("手机备份", DeviceA);
        string linkPath = Path.Combine(
            _root,
            "手机备份",
            RecordingDeviceFolderShortcuts.BuildShortcutFileName(
                new RecordingDeviceFolderShortcuts.ShortcutTarget(DeviceA, nickname),
                new Dictionary<string, string>()));

        // 真走外壳：保留名会在这里直接失败，用假实现测不出来。
        Assert.True(
            WindowsShellShortcut.TryCreate(linkPath, deviceDirectory, out string error),
            $"昵称“{nickname}”的快捷方式建不出来：{error}");
        Assert.True(File.Exists(linkPath));
    }

    /// <summary>昵称只有点号时不能拼出"."或".."这种目录引用。</summary>
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("   ")]
    public void DegenerateNicknamesFallBackToAPlaceholder(string nickname)
    {
        string fileName = RecordingDeviceFolderShortcuts.BuildShortcutFileName(
            new RecordingDeviceFolderShortcuts.ShortcutTarget(DeviceA, nickname),
            new Dictionary<string, string>());

        Assert.Equal("未命名设备.lnk", fileName);
    }

    /// <summary>没在这台主机留下过录像的设备不该有空链接。</summary>
    [Fact]
    public void SkipsDevicesWithoutDirectories()
    {
        CreateDeviceDirectory("手机备份", DeviceA);

        CreateShortcuts().Refresh(_root, [Device(DeviceA, "安卓1"), Device(DeviceB, "安卓2")]);

        Assert.Single(_written);
        Assert.DoesNotContain("安卓2.lnk", _written.Keys.Select(Path.GetFileName)!);
    }

    /// <summary>设备改名后旧链接要清掉，否则目录里会同时挂着新旧两个名字。</summary>
    [Fact]
    public void RemovesShortcutAfterDeviceRename()
    {
        CreateDeviceDirectory("手机备份", DeviceA);
        RecordingDeviceFolderShortcuts shortcuts = CreateShortcuts();
        shortcuts.Refresh(_root, [Device(DeviceA, "安卓1")]);
        string oldLink = Path.Combine(_root, "手机备份", "安卓1.lnk");
        Assert.True(File.Exists(oldLink));

        shortcuts.Refresh(_root, [Device(DeviceA, "打包台A")]);

        Assert.False(File.Exists(oldLink), "改名后旧链接必须清掉");
        Assert.True(File.Exists(Path.Combine(_root, "手机备份", "打包台A.lnk")));
    }

    /// <summary>
    /// 用户自己放进来的快捷方式和别的文件一律不碰：我们只清理指向本目录下设备目录的链接。
    /// </summary>
    [Fact]
    public void NeverTouchesFilesItDidNotCreate()
    {
        CreateDeviceDirectory("手机备份", DeviceA);
        string categoryPath = Path.Combine(_root, "手机备份");
        string userLink = Path.Combine(categoryPath, "我的收藏.lnk");
        File.WriteAllText(userLink, @"D:\别处\某个目录");
        string userFile = Path.Combine(categoryPath, "说明.txt");
        File.WriteAllText(userFile, "用户自己的文件");

        CreateShortcuts().Refresh(_root, [Device(DeviceA, "安卓1")]);

        Assert.True(File.Exists(userLink), "用户自己的快捷方式不能被删");
        Assert.True(File.Exists(userFile));
    }

    /// <summary>重复刷新时不该反复重写同一个链接（NAS 上每次写盘都有代价）。</summary>
    [Fact]
    public void DoesNotRewriteShortcutsThatAlreadyPointCorrectly()
    {
        CreateDeviceDirectory("手机备份", DeviceA);
        RecordingDeviceFolderShortcuts shortcuts = CreateShortcuts();
        shortcuts.Refresh(_root, [Device(DeviceA, "安卓1")]);

        int second = shortcuts.Refresh(_root, [Device(DeviceA, "安卓1")]);

        Assert.Equal(0, second);
    }

    /// <summary>昵称里的非法字符要净化，净化后撞名的带设备号后缀区分。</summary>
    [Fact]
    public void SanitizesNicknamesAndKeepsCollidingNamesApart()
    {
        CreateDeviceDirectory("手机备份", DeviceA);
        CreateDeviceDirectory("手机备份", DeviceB);

        CreateShortcuts().Refresh(
            _root,
            [Device(DeviceA, "打包:台/A"), Device(DeviceB, "打包?台*A")]);

        string[] names = _written.Keys.Select(Path.GetFileName).OrderBy(name => name).ToArray()!;
        Assert.Equal(2, names.Length);
        Assert.All(names, name => Assert.DoesNotContain(
            name,
            Path.GetInvalidFileNameChars().Select(character => character.ToString())));
        Assert.Equal(2, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>早先那份"设备对照表.txt"由主机自己维护，改用快捷方式后要清掉，免得留过期信息。</summary>
    [Fact]
    public void RemovesLegacyIndexFile()
    {
        Directory.CreateDirectory(_root);
        string legacyPath = Path.Combine(_root, RecordingDeviceFolderShortcuts.LegacyIndexFileName);
        File.WriteAllText(legacyPath, "老的对照表内容");

        CreateShortcuts().Refresh(_root, []);

        Assert.False(File.Exists(legacyPath));
    }

    /// <summary>外壳组件被禁用（建不出快捷方式）时不能抛，录像与备份照常。</summary>
    [Fact]
    public void SurvivesShellFailures()
    {
        CreateDeviceDirectory("手机备份", DeviceA);
        var shortcuts = new RecordingDeviceFolderShortcuts(
            shortcutWriter: (string _, string _, out string error) => { error = "外壳不可用"; return false; },
            shortcutTargetReader: _ => throw new InvalidOperationException("外壳不可用"));

        int written = shortcuts.Refresh(_root, [Device(DeviceA, "安卓1")]);

        Assert.Equal(0, written);
    }

    /// <summary>根目录为空时什么都不做，不能拼出一个空路径去写盘。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IgnoresMissingRecordingRoot(string? root)
    {
        Assert.Equal(0, CreateShortcuts().Refresh(root, [Device(DeviceA, "安卓1")]));
    }

    /// <summary>
    /// 真正走一遍 Windows 外壳：.lnk 要能建出来、读回来的目标要一致。
    ///
    /// 不允许"失败就跳过"：上一版就是写成静默跳过，结果真实环境里建不出来（.NET 8 的
    /// IDispatch 属性赋值失败）也没被发现，等用户打开目录才看出来。
    /// </summary>
    [Fact]
    public void WindowsShellCreatesAndReadsRealShortcut()
    {
        string targetDirectory = CreateDeviceDirectory("手机备份", DeviceA);
        string linkPath = Path.Combine(_root, "安卓1.lnk");

        Assert.True(
            WindowsShellShortcut.TryCreate(linkPath, targetDirectory, out string error),
            $"外壳建快捷方式失败：{error}");
        Assert.True(File.Exists(linkPath));
        Assert.Equal(
            targetDirectory.TrimEnd(Path.DirectorySeparatorChar),
            WindowsShellShortcut.TryReadTarget(linkPath)?.TrimEnd(Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// 外壳按"解析"方式也要能读出目标：资源管理器双击走的就是这条路。
    /// 只写原始路径、不写 ID 列表的链接在这里会读出空串 —— 文件建出来了，
    /// 但链接信息不完整，光看"文件存在"是发现不了的。
    ///
    /// 这里刻意不用 WScript.Shell 来验证：它走 ANSI，系统代码页表示不了路径里的字时
    /// （例如 zh-TW 的 950 表示不了简体"备"）会直接读出空串或报 E_INVALIDARG，
    /// 那是验证手段本身的限制，会把好链接误判成坏的。
    /// </summary>
    [Fact]
    public void RealShortcutResolvesThroughTheShell()
    {
        string targetDirectory = CreateDeviceDirectory("手机备份", DeviceA);
        string linkPath = Path.Combine(_root, "安卓1.lnk");
        Assert.True(WindowsShellShortcut.TryCreate(linkPath, targetDirectory, out string error), error);

        string? resolved = WindowsShellShortcut.TryReadTarget(linkPath, resolve: true);

        Assert.False(
            string.IsNullOrEmpty(resolved),
            "外壳按解析方式读不到目标，说明链接缺少 ID 列表");
        Assert.Equal(
            targetDirectory.TrimEnd(Path.DirectorySeparatorChar),
            resolved!.TrimEnd(Path.DirectorySeparatorChar));
    }

    private RecordingDeviceFolderShortcuts CreateShortcuts() =>
        new(
            shortcutWriter: (string linkPath, string target, out string error) =>
            {
                error = "";
                File.WriteAllText(linkPath, target);
                _written[linkPath] = target;
                return true;
            },
            shortcutTargetReader: linkPath =>
                File.Exists(linkPath) ? File.ReadAllText(linkPath) : null);

    private static MobileOrderReceiverInfo Device(string nodeId, string nodeName) =>
        new(nodeId, nodeName, "192.168.31.20", 5280, [], Online: true);

    private string DeviceDirectory(string category, string deviceId) =>
        Path.Combine(_root, category, RecordingDeviceFolderNaming.BuildDirectoryName(deviceId));

    private string CreateDeviceDirectory(string category, string deviceId)
    {
        string directory = DeviceDirectory(category, deviceId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch
        {
            // 临时目录清理失败不影响断言结果。
        }
    }
}
