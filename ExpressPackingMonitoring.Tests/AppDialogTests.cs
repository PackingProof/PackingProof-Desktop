using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class AppDialogTests
{
    [Fact]
    public void AppDialog_ExposesThemedMessageAndConfirmationEntryPoints()
    {
        string source = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "AppDialog.cs");
        string xaml = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "ConfirmDialog.xaml");

        Assert.Contains("public static void ShowMessage(", source, StringComparison.Ordinal);
        Assert.Contains("public static bool Confirm(", source, StringComparison.Ordinal);
        Assert.Contains("dispatcher.Invoke(action)", source, StringComparison.Ordinal);
        Assert.Contains("WindowStartupLocation.CenterScreen", source, StringComparison.Ordinal);
        Assert.Contains("Fluent", ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "ConfirmDialog.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"14\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"430\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PrimaryButtonStyle", ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "ConfirmDialog.xaml.cs"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AppDialogSeverity.Information)]
    [InlineData(AppDialogSeverity.Warning)]
    [InlineData(AppDialogSeverity.Error)]
    public void AppDialogSeverity_DefinesSupportedVisualLevels(AppDialogSeverity severity)
    {
        Assert.True(Enum.IsDefined(severity));
    }

    [Fact]
    public void DesktopCode_UsesOnlyTheUnifiedDialogEntryPoint()
    {
        string projectDirectory = FindRepositoryPath("ExpressPackingMonitoring");
        string[] sourceFiles = Directory.GetFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (string sourceFile in sourceFiles)
        {
            string source = File.ReadAllText(sourceFile);
            Assert.DoesNotContain("MessageBox.Show", source, StringComparison.Ordinal);

            if (!sourceFile.EndsWith(
                    Path.Combine("UI", "AppDialog.cs"),
                    StringComparison.OrdinalIgnoreCase))
            {
                Assert.DoesNotContain("new ConfirmDialog", source, StringComparison.Ordinal);
            }
        }

        string settingsSource = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "SettingsWindow.xaml.cs");
        Assert.Contains("\"移除\"", settingsSource, StringComparison.Ordinal);
        Assert.Contains("\"继续保存\"", settingsSource, StringComparison.Ordinal);
        Assert.Contains("\"返回设置\"", settingsSource, StringComparison.Ordinal);
        Assert.Contains("\"使用建议方案\"", settingsSource, StringComparison.Ordinal);
        Assert.Contains("\"取消保存\"", settingsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileAppUpdatePrompt_IsNonModalAndDoesNotStealScannerFocus()
    {
        string prompt = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "MobileAppUpdatePrompt.cs");
        string window = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "MobileAppUpdatePromptWindow.xaml");
        string mainViewModel = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.cs");

        Assert.Contains("prompt.Show();", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", prompt, StringComparison.Ordinal);
        Assert.Contains("ShowActivated=\"False\"", window, StringComparison.Ordinal);
        Assert.Contains("ShowInTaskbar=\"False\"", window, StringComparison.Ordinal);
        Assert.DoesNotContain("_pendingMobileAppUpdate", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "停止录制后将提示更新",
            mainViewModel,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] parts)
        => File.ReadAllText(FindRepositoryPath(parts));

    private static string FindRepositoryPath(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path))
                return path;
            if (Directory.Exists(path))
                return path;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
