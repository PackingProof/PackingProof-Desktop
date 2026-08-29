using System.Text;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class SourceFileSizeGuardTests
{
    private const int DefaultMaximumLines = 2000;

    private static readonly IReadOnlyDictionary<string, int> LegacyMaximumLines =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [@"Services\WebServer.cs"] = 6184,
            [@"Data\VideoDatabase.cs"] = 4126,
            [@"UI\SettingsWindow.xaml.cs"] = 2986
        };

    [Fact]
    public void ProductionCSharpFiles_DoNotExceedSizeBudgets()
    {
        string projectRoot = Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring");
        string[] violations = Directory
            .GetFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path))
            .Select(path =>
            {
                string relativePath = Path.GetRelativePath(projectRoot, path);
                int maximumLines = LegacyMaximumLines.GetValueOrDefault(
                    relativePath,
                    DefaultMaximumLines);
                int actualLines = File.ReadLines(path, Encoding.UTF8).Count();
                return (relativePath, actualLines, maximumLines);
            })
            .Where(file => file.actualLines > file.maximumLines)
            .OrderByDescending(file => file.actualLines - file.maximumLines)
            .Select(file =>
                $"{file.relativePath}: {file.actualLines} 行，预算 {file.maximumLines} 行")
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "生产源码文件超过规模预算。请抽取独立职责，不要提高预算或压缩排版："
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    private static bool IsGeneratedPath(string path) =>
        path.Contains(
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
        || path.Contains(
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
        || path.Contains(
            $"{Path.DirectorySeparatorChar}bin_publish_tmp{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
        || path.Contains(
            $"{Path.DirectorySeparatorChar}obj_publish_tmp{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }
}
