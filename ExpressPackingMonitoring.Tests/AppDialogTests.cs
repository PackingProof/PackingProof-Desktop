using ExpressPackingMonitoring.UI;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class AppDialogTests
{
    [Fact]
    public void AppDialog_ExposesThemedMessageAndConfirmationEntryPoints()
    {
        string source = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "AppDialog.cs");
        string xaml = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "ConfirmDialog.xaml");

        Assert.Contains("public static void Information(", source, StringComparison.Ordinal);
        Assert.Contains("public static void Warning(", source, StringComparison.Ordinal);
        Assert.Contains("public static void Error(", source, StringComparison.Ordinal);
        Assert.Contains("public static bool Confirm(", source, StringComparison.Ordinal);
        Assert.Contains("dispatcher.Invoke(action)", source, StringComparison.Ordinal);
        Assert.Contains("WindowStartupLocation.CenterScreen", source, StringComparison.Ordinal);
        Assert.Contains("NotificationVisuals", ReadRepositoryFile(
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

    [Fact]
    public void AppDialog_InformationUsesDedicatedInfoIcon()
    {
        string icons = ReadRepositoryFile("ExpressPackingMonitoring", "Themes", "FluentIcons.xaml");
        string visuals = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "NotificationVisuals.cs");
        string dialogCode = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "ConfirmDialog.xaml.cs");

        Assert.Contains("x:Key=\"FluentInfoIcon\"", icons, StringComparison.Ordinal);
        Assert.Contains("FluentInfoIcon", visuals, StringComparison.Ordinal);
        Assert.Contains("FluentCheckIcon", visuals, StringComparison.Ordinal);
        Assert.DoesNotContain("FluentInfoIcon", dialogCode, StringComparison.Ordinal);
        Assert.DoesNotContain("FluentCheckIcon", dialogCode, StringComparison.Ordinal);
    }

    [Fact]
    public void AppDialogCallSites_UseSemanticMethodsAndExplicitConfirmSeverity()
    {
        string projectDirectory = FindRepositoryPath("ExpressPackingMonitoring");

        foreach ((string file, int line, string block) in FindCallBlocks(projectDirectory, "ShowMessage"))
        {
            int literalSeverityCount =
                Regex.Matches(block, @"AppDialogSeverity\.(Information|Warning|Error)").Count;
            bool isCoreImplementation = file.EndsWith(
                Path.Combine("UI", "AppDialog.cs"),
                StringComparison.OrdinalIgnoreCase);
            Assert.True(
                isCoreImplementation || literalSeverityCount != 1,
                $"AppDialog.ShowMessage 带字面量严重度，请改用语义方法：{Path.GetFileName(file)}:{line}");
        }

        foreach ((string file, int line, string block) in FindCallBlocks(projectDirectory, "Confirm"))
        {
            Assert.True(
                Regex.IsMatch(block, @"AppDialogSeverity\.(Information|Warning|Error)"),
                $"AppDialog.Confirm 必须显式传 severity：{Path.GetFileName(file)}:{line}");
        }
    }

    [Fact]
    public void AppDialogStaticCallSites_UseErrorForFailureTitles()
    {
        string projectDirectory = FindRepositoryPath("ExpressPackingMonitoring");
        string[] errorKeywords = ["失败", "错误", "无法", "未能", "不可用", "不存在", "无效"];
        string[] intentionalWarnings = ["暂时无法更换主机"];

        foreach (string method in new[] { "Information", "Warning" })
        {
            foreach ((string file, int line, string block) in FindCallBlocks(projectDirectory, method))
            {
                string[] arguments = SplitTopLevelArguments(block);
                if (arguments.Length < 3)
                    continue;

                string titleExpression = arguments[2].Trim();
                Match literal = Regex.Match(
                    titleExpression,
                    @"^@?\$?\""((?:[^\""\\]|\\.)*)\""$");
                if (!literal.Success)
                    continue;

                string title = literal.Groups[1].Value;
                if (intentionalWarnings.Contains(title, StringComparer.Ordinal))
                    continue;
                if (errorKeywords.Any(keyword => title.Contains(keyword, StringComparison.Ordinal)))
                {
                    Assert.Fail(
                        $"“{title}”属于失败/错误类提示，应使用 AppDialog.Error：{Path.GetFileName(file)}:{line}");
                }
            }
        }
    }

    [Fact]
    public void ToastMessages_DoNotUseWarningTextPrefix()
    {
        string projectDirectory = FindRepositoryPath("ExpressPackingMonitoring");
        string[] sourceFiles = Directory.GetFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (string sourceFile in sourceFiles)
        {
            string source = File.ReadAllText(sourceFile);
            Assert.DoesNotContain("ShowToast(\"警告：", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ShowToast($\"警告：", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ToastSeverity_IsPlumbedThroughAlertPipeline()
    {
        string alertService = ReadRepositoryFile("ExpressPackingMonitoring", "Services", "AlertService.cs");
        string viewModel = ReadRepositoryFile("ExpressPackingMonitoring", "ViewModels", "MainViewModel.cs");
        string settingsContext = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "SettingsContext.cs");

        Assert.Contains("public enum ToastSeverity", alertService, StringComparison.Ordinal);
        Assert.Contains("ToastSeverity Severity { get; init; } = ToastSeverity.Success", alertService, StringComparison.Ordinal);
        Assert.Contains(
            "public void ShowToast(string message, ToastSeverity severity = ToastSeverity.Success)",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains("Action<string, ToastSeverity>? ShowToast", settingsContext, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowToast_BindsSeverityVisuals()
    {
        string mainWindow = ReadRepositoryFile("ExpressPackingMonitoring", "UI", "MainWindow.xaml");
        string printWorkstation = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "Workstations",
            "PrintWorkstationWindow.xaml.cs");

        Assert.Contains("{Binding ToastSeverity}", mainWindow, StringComparison.Ordinal);
        Assert.Contains("FluentCheckIcon", mainWindow, StringComparison.Ordinal);
        Assert.Contains("FluentWarningIcon", mainWindow, StringComparison.Ordinal);
        Assert.Contains("FluentDismissIcon", mainWindow, StringComparison.Ordinal);
        Assert.Contains("FluentInfoIcon", mainWindow, StringComparison.Ordinal);
        Assert.Contains("NotificationVisuals.GetIconKey", printWorkstation, StringComparison.Ordinal);
        Assert.Contains("NotificationVisuals.GetBrushKey", printWorkstation, StringComparison.Ordinal);
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

    [Fact]
    public void BackupEnrollmentPrompt_ResolvesItsOwnerOnTheUiThread()
    {
        string prompt = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "BackupDeviceEnrollmentApprovalPrompt.cs");
        string mainViewModel = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.cs");

        string window = ReadRepositoryFile(
            "ExpressPackingMonitoring",
            "UI",
            "BackupDeviceEnrollmentApprovalWindow.xaml");

        Assert.Contains("application.Dispatcher.InvokeAsync", prompt, StringComparison.Ordinal);
        Assert.Contains("completion.TrySetResult(BackupDeviceEnrollmentApprovalDecision.Unavailable)", prompt, StringComparison.Ordinal);
        Assert.Contains("shownPrompt.Close()", prompt, StringComparison.Ordinal);
        Assert.Contains("completion.Task.IsCompleted", prompt, StringComparison.Ordinal);
        Assert.Contains("Application.Current?.Windows", prompt, StringComparison.Ordinal);
        Assert.Contains("prompt.Show();", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDialog", prompt, StringComparison.Ordinal);
        Assert.Contains("60 秒内未处理", window, StringComparison.Ordinal);
        Assert.Contains(
            "BackupDeviceEnrollmentApprovalPrompt.Show(null, request)",
            mainViewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "BackupDeviceEnrollmentApprovalPrompt.Show(Application.Current?.MainWindow, request)",
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
            if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                return Path.Combine([directory.FullName, .. parts]);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }

    private static List<(string File, int Line, string Block)> FindCallBlocks(
        string projectDirectory,
        string methodName)
    {
        var result = new List<(string, int, string)>();
        string[] sourceFiles = Directory.GetFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (string sourceFile in sourceFiles)
        {
            string[] lines = File.ReadAllLines(sourceFile);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains($"AppDialog.{methodName}(", StringComparison.Ordinal))
                    continue;

                var block = new StringBuilder();
                int depth = 0;
                bool inString = false;
                int j = i;
                for (; j < lines.Length && j < i + 60; j++)
                {
                    string line = lines[j];
                    block.AppendLine(line);
                    for (int k = 0; k < line.Length; k++)
                    {
                        char c = line[k];
                        if (inString)
                        {
                            if (c == '\\')
                            {
                                k++;
                                continue;
                            }
                            if (c == '"')
                                inString = false;
                            continue;
                        }

                        if (c == '"')
                        {
                            inString = true;
                        }
                        else if (c == '(')
                        {
                            depth++;
                        }
                        else if (c == ')')
                        {
                            depth--;
                            if (depth == 0)
                                break;
                        }
                    }

                    if (depth == 0)
                        break;
                }

                if (depth == 0)
                    result.Add((sourceFile, i + 1, block.ToString()));
            }
        }

        return result;
    }

    private static string[] SplitTopLevelArguments(string callBlock)
    {
        int openParen = callBlock.IndexOf('(');
        if (openParen < 0)
            return [];

        var arguments = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        bool inString = false;
        for (int i = openParen + 1; i < callBlock.Length; i++)
        {
            char c = callBlock[i];
            if (inString)
            {
                current.Append(c);
                if (c == '\\' && i + 1 < callBlock.Length)
                {
                    current.Append(callBlock[++i]);
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
                current.Append(c);
            }
            else if (c == '(')
            {
                depth++;
                current.Append(c);
            }
            else if (c == ')')
            {
                if (depth == 0)
                    break;
                depth--;
                current.Append(c);
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            arguments.Add(current.ToString());
        return arguments.ToArray();
    }
}
