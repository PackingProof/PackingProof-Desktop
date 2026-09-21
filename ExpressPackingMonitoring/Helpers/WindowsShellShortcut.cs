using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ExpressPackingMonitoring.Helpers;

/// <summary>
/// 创建与读取 Windows 快捷方式（.lnk）。
///
/// .NET 没有 BCL API 能写 .lnk，这里直接用外壳的 <c>IShellLink</c> 接口。
///
/// 不要改回 <c>WScript.Shell</c> 后期绑定（<c>Type.InvokeMember</c>）：在 .NET 8 里给
/// IDispatch 属性赋值会失败，实测 <c>TargetPath</c> 那一步报 E_INVALIDARG，
/// 而同样的调用在 PowerShell 里能成（PowerShell 用的是自己的 COM 适配器），
/// 所以"手工试一下能成"不能作为这条路可用的依据。
///
/// 任何一步失败都返回失败值并带出原因，绝不抛：快捷方式只是给用户看的便利，
/// 不能因为它影响录像与备份（外壳组件被安全软件禁用过是真实场景）。
/// </summary>
internal static class WindowsShellShortcut
{
    /// <summary>读目标时的缓冲区长度，够放长路径。</summary>
    private const int PathBufferLength = 1024;

    /// <summary>建一个指向目录或文件的快捷方式；已存在则覆盖。</summary>
    internal static bool TryCreate(string linkPath, string targetPath) =>
        TryCreate(linkPath, targetPath, out _);

    /// <summary>
    /// 同上，并带出失败原因。外壳调用失败的原因必须能被记下来：
    /// 只报"建不出来"的话，现场根本没法判断是权限、外壳被禁用还是路径问题。
    /// </summary>
    internal static bool TryCreate(string linkPath, string targetPath, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(linkPath) || string.IsNullOrWhiteSpace(targetPath))
        {
            error = "链接路径或目标为空";
            return false;
        }

        // 记录当前步骤：外壳只报 HRESULT，不说是哪一步，没有这个标记只能靠猜。
        string step = "准备目录";
        object? shellLink = null;
        try
        {
            string? directory = Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            step = "创建 ShellLink";
            shellLink = new ShellLink();
            var link = (IShellLinkW)shellLink;

            step = "设置目标路径";
            link.SetPath(targetPath);

            step = "设置工作目录";
            link.SetWorkingDirectory(
                Directory.Exists(targetPath) ? targetPath : Path.GetDirectoryName(targetPath) ?? "");

            // 只有 SetPath 的链接，外壳按"解析"方式读不出目标（WScript.Shell 的 TargetPath
            // 会返回空，资源管理器里也可能打不开）。再写一份 ID 列表，链接才是完整的。
            step = "设置 ID 列表";
            TrySetIdList(link, targetPath);

            step = "保存";
            ((IPersistFile)shellLink).Save(linkPath, true);
            if (File.Exists(linkPath))
                return true;

            error = "保存之后文件仍然不存在";
            return false;
        }
        catch (Exception ex)
        {
            error = $"[{step}] {DescribeFailure(ex)}";
            return false;
        }
        finally
        {
            ReleaseComObject(shellLink);
        }
    }

    /// <summary>读快捷方式指向的目标；读不到返回 null。</summary>
    internal static string? TryReadTarget(string linkPath) => TryReadTarget(linkPath, resolve: false);

    /// <summary>
    /// 读快捷方式指向的目标。
    /// <paramref name="resolve"/> 为 true 时按外壳的解析方式读（资源管理器双击走的就是这条）：
    /// 链接缺少 ID 列表时这种读法会得到空串，正好能验证链接是不是完整的。
    /// </summary>
    internal static string? TryReadTarget(string linkPath, bool resolve)
    {
        if (string.IsNullOrWhiteSpace(linkPath) || !File.Exists(linkPath))
            return null;

        object? shellLink = null;
        try
        {
            shellLink = new ShellLink();
            ((IPersistFile)shellLink).Load(linkPath, ShellLinkNative.StgmRead);
            var buffer = new StringBuilder(PathBufferLength);
            // 默认不解析（SLGP_RAWPATH）：目标目录可能已经被搬走或不可达，
            // 让外壳去解析会触发查找甚至弹窗，日常判断"这个链接是不是指向它"只要原始路径。
            ((IShellLinkW)shellLink).GetPath(
                buffer,
                buffer.Capacity,
                IntPtr.Zero,
                resolve ? ShellLinkNative.SlgpNoResolve : ShellLinkNative.SlgpRawPath);
            string target = buffer.ToString();
            return target.Length == 0 ? null : target;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(shellLink);
        }
    }

    /// <summary>
    /// 把目标的 ID 列表写进链接。失败时不算致命：链接里已经有原始路径，
    /// 大多数场景仍可用，所以只是少一份解析信息，不让整个创建失败。
    /// </summary>
    private static void TrySetIdList(IShellLinkW link, string targetPath)
    {
        IntPtr idList = IntPtr.Zero;
        try
        {
            if (ShellLinkNative.SHParseDisplayName(targetPath, IntPtr.Zero, out idList, 0, out _) != 0
                || idList == IntPtr.Zero)
            {
                return;
            }

            link.SetIDList(idList);
        }
        catch
        {
            // 保持只有原始路径的链接，不影响创建结果。
        }
        finally
        {
            if (idList != IntPtr.Zero)
                Marshal.FreeCoTaskMem(idList);
        }
    }

    private static string DescribeFailure(Exception exception)
    {
        var parts = new List<string>();
        for (Exception? current = exception; current != null; current = current.InnerException)
            parts.Add($"{current.GetType().Name}: {current.Message}");

        return string.Join(" <- ", parts);
    }

    private static void ReleaseComObject(object? instance)
        => Services.WindowsPlatformSupport.ReleaseComObject(instance);

    private static class ShellLinkNative
    {
        /// <summary>STGM_READ：只读打开。</summary>
        internal const int StgmRead = 0;

        /// <summary>SLGP_RAWPATH：原样返回存着的路径，不做解析。</summary>
        internal const uint SlgpRawPath = 0x4;

        /// <summary>按外壳的解析方式取路径（无标志位）：链接信息不完整时会返回空串。</summary>
        internal const uint SlgpNoResolve = 0x0;

        /// <summary>把显示名（这里就是文件系统路径）解析成 ID 列表，用于写完整的链接信息。</summary>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int SHParseDisplayName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            IntPtr pbc,
            out IntPtr ppidl,
            uint sfgaoIn,
            out uint psfgaoOut);
    }

    /// <summary>外壳的快捷方式对象（CLSID_ShellLink）。</summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class ShellLink
    {
    }

    /// <summary>
    /// IShellLinkW。方法顺序就是 vtable 顺序，**不能重排、不能删减**，
    /// 少一个或换位置都会调用到错误的槽位。这里只用到 GetPath/SetPath/SetWorkingDirectory，
    /// 其余成员保留占位。
    /// </summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(
            [Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cchMaxPath,
            IntPtr pfd,
            uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription(
            [Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName,
            int cchMaxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory(
            [Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir,
            int cchMaxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments(
            [Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs,
            int cchMaxPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation(
            [Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cchIconPath,
            out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
