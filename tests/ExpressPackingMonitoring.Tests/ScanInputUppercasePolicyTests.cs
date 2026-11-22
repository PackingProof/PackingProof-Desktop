using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ScanInputUppercasePolicyTests
{
    [Fact]
    public void ScanInputUsesLocalUppercaseWithoutTouchingSystemCapsLock()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ExpressPackingMonitoring",
            "UI",
            "MainWindow.xaml.cs"));
        string integration = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Integration.cs"));
        string settings = File.ReadAllText(Path.Combine(
            root,
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Settings.cs"));

        Assert.Contains("CharacterCasing=\"Upper\"", xaml, StringComparison.Ordinal);
        Assert.Contains("InputMethod.IsInputMethodEnabled=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("InputMethod.PreferredImeState=\"Off\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("keybd_event", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("VK_CAPITAL", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("GetKeyState", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("SuspendCapsLock", integration, StringComparison.Ordinal);
        Assert.DoesNotContain("ResumeCapsLock", integration, StringComparison.Ordinal);
        Assert.DoesNotContain("SuspendCapsLock", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("ResumeCapsLock", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void BarcodeNormalizationStillUppercasesScannerAndManualInput()
    {
        Assert.Equal("ABC123", JdBarcodePolicy.Normalize("  abc123  "));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "src",
                "ExpressPackingMonitoring",
                "Web",
                "index.html");
            if (File.Exists(candidate))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("找不到仓库根目录");
    }
}
