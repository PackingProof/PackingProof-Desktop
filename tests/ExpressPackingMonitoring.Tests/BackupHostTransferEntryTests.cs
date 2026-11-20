using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class BackupHostTransferEntryTests
{
    [Fact]
    public void BackupHostCardOpensTheSharedTransferManagementWindow()
    {
        string project = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring");
        string xaml = File.ReadAllText(Path.Combine(
            project,
            "Workstations",
            "PrintWorkstationWindow.xaml"));
        string code = File.ReadAllText(Path.Combine(
            project,
            "Workstations",
            "PrintWorkstationWindow.xaml.cs"));

        Assert.Contains(
            "PreviewMouseLeftButtonUp=\"MobileBackupStatusCard_PreviewMouseLeftButtonUp\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "new TransferManagementWindow(snapshotService)",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "CreateTransferManagementSnapshotService",
            code,
            StringComparison.Ordinal);
        Assert.Contains("window.ShowDialog();", code, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到仓库根目录");
    }
}
