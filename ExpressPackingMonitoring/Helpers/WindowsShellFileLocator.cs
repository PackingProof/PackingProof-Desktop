using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ExpressPackingMonitoring.Helpers;

internal enum FileLocationResult
{
    Invalid,
    Selected,
    OpenedFolder,
    Failed
}

internal static class WindowsShellFileLocator
{
    public static FileLocationResult Locate(string filePath) =>
        Locate(filePath, TrySelectFile, OpenFolderCore);

    /// <summary>
    /// 只打开文件夹、不选中任何文件：批量导出整套图片后直接带用户去看结果。
    /// </summary>
    public static FileLocationResult OpenFolder(string folderPath) =>
        OpenFolder(folderPath, OpenFolderCore);

    internal static FileLocationResult OpenFolder(
        string folderPath,
        Action<string> openFolder)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return FileLocationResult.Invalid;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(folderPath);
        }
        catch
        {
            return FileLocationResult.Invalid;
        }

        if (!Directory.Exists(fullPath))
            return FileLocationResult.Invalid;

        try
        {
            openFolder(fullPath);
            return FileLocationResult.OpenedFolder;
        }
        catch
        {
            return FileLocationResult.Failed;
        }
    }

    internal static FileLocationResult Locate(
        string filePath,
        Func<string, bool> selectFile,
        Action<string> openFolder)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return FileLocationResult.Invalid;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch
        {
            return FileLocationResult.Invalid;
        }

        if (!File.Exists(fullPath))
            return FileLocationResult.Invalid;

        try
        {
            if (selectFile(fullPath))
                return FileLocationResult.Selected;
        }
        catch
        {
        }

        string? folder = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return FileLocationResult.Failed;

        try
        {
            openFolder(folder);
            return FileLocationResult.OpenedFolder;
        }
        catch
        {
            return FileLocationResult.Failed;
        }
    }

    private static bool TrySelectFile(string fullPath)
    {
        int parseResult = SHParseDisplayName(fullPath, IntPtr.Zero, out IntPtr itemIdList, 0, out _);
        if (parseResult < 0 || itemIdList == IntPtr.Zero)
            return false;

        try
        {
            return SHOpenFolderAndSelectItems(itemIdList, 0, IntPtr.Zero, 0) >= 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(itemIdList);
        }
    }

    private static void OpenFolderCore(string folder)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        });
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(
        string name,
        IntPtr bindingContext,
        out IntPtr itemIdList,
        uint attributesIn,
        out uint attributesOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr folderItemIdList,
        uint itemCount,
        IntPtr childItemIdLists,
        uint flags);
}
