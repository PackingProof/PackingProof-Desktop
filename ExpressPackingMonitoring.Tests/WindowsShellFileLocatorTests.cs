using ExpressPackingMonitoring.Helpers;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 批量导出整套图片后只打开文件夹，这里覆盖打开文件夹的校验与失败退化。
/// </summary>
public sealed class WindowsShellFileLocatorTests
{
    [Fact]
    public void OpenFolder_OpensExistingFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"packingproof-open-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string? openedFolder = null;
            FileLocationResult result = WindowsShellFileLocator.OpenFolder(
                folder,
                path => openedFolder = path);

            Assert.Equal(FileLocationResult.OpenedFolder, result);
            Assert.Equal(folder, openedFolder);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void OpenFolder_ReportsInvalidForMissingOrBlankPath()
    {
        bool opened = false;
        string missing = Path.Combine(Path.GetTempPath(), $"packingproof-open-{Guid.NewGuid():N}");

        Assert.Equal(
            FileLocationResult.Invalid,
            WindowsShellFileLocator.OpenFolder(missing, _ => opened = true));
        Assert.Equal(
            FileLocationResult.Invalid,
            WindowsShellFileLocator.OpenFolder("  ", _ => opened = true));
        Assert.False(opened);
    }

    [Fact]
    public void OpenFolder_ReportsFailedWhenShellThrows()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"packingproof-open-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            FileLocationResult result = WindowsShellFileLocator.OpenFolder(
                folder,
                _ => throw new InvalidOperationException("boom"));

            Assert.Equal(FileLocationResult.Failed, result);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
