using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class DialogWindowChromeTests
{
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsSysMenu = 0x00080000L;

    [Fact]
    public void CloseOnlyChromeKeepsSystemMenuAndResizeFrame()
    {
        long style = WsSysMenu | WsThickFrame | WsMinimizeBox | WsMaximizeBox;

        long updated = DialogWindowChrome.RemoveMinimizeAndMaximize(style);

        Assert.Equal(WsSysMenu, updated & WsSysMenu);
        Assert.Equal(WsThickFrame, updated & WsThickFrame);
        Assert.Equal(0, updated & WsMinimizeBox);
        Assert.Equal(0, updated & WsMaximizeBox);
    }

    [Theory]
    [InlineData("UI", "TransferManagementWindow.xaml.cs")]
    [InlineData("UI", "MobileConnectionWindow.xaml.cs")]
    [InlineData("Workstations", "ManualHostConnectionWindow.xaml.cs")]
    [InlineData("Workstations", "ViewerClientWindow.xaml.cs")]
    [InlineData("Workstations", "WorkstationSelectionWindow.xaml.cs")]
    public void DialogWindowUsesSharedCloseOnlyChrome(string folder, string fileName)
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring",
            folder,
            fileName));

        Assert.Contains(
            "DialogWindowChrome.UseCloseButtonOnly(this)",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
