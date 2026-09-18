using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 模式切换的滑动动画由 ModeTransitionButton 自己播放。
/// ViewModel 里再等一次动画时长才更新 CurrentMode，点击后会有半秒的无反应感，
/// 所以 ToggleMode 必须同步落值。
/// </summary>
public sealed class ModeToggleResponsivenessTests
{
    [Fact]
    public void ToggleMode_AppliesModeSynchronously()
    {
        string source = ReadScannerSource();
        string body = ExtractMethodBody(source, "private void ToggleMode()");

        Assert.Contains("CurrentMode =", body);
        Assert.DoesNotContain("Task.Delay", body);
        Assert.DoesNotContain("await", body);
    }

    [Fact]
    public void ModeTransitionScaffolding_IsNotReintroduced()
    {
        string root = FindRepositoryRoot();
        string project = Path.Combine(root, "ExpressPackingMonitoring");

        string[] offenders = Directory.GetFiles(project, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(project, "*.xaml", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("IsModeTransitionActive", StringComparison.Ordinal)
                || File.ReadAllText(path).Contains("AnimateModeTransitionAsync", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(project, path))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "模式切换不应再依赖 ViewModel 侧的动画等待: " + string.Join(" | ", offenders));
    }

    private static string ReadScannerSource() =>
        File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Scanner.cs"));

    /// <summary>按大括号配平截出方法体，避免正则跨方法误匹配。</summary>
    private static string ExtractMethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"找不到方法: {signature}");

        int open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"方法缺少方法体: {signature}");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }

        throw new InvalidOperationException($"方法体大括号不配平: {signature}");
    }

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("找不到解决方案根目录");
    }
}
