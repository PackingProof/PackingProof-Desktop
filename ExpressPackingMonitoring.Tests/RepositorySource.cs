namespace ExpressPackingMonitoring.Tests;

internal static class RepositorySource
{
    public static string ReadMainViewModel()
    {
        string directory = FindRepositoryDirectory();
        return string.Join(
            Environment.NewLine,
            Directory.GetFiles(directory, "MainViewModel*.cs", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllText));
    }

    public static string ReadMainViewModelParts(params string[] partNames)
    {
        string directory = FindRepositoryDirectory();
        return string.Join(
            Environment.NewLine,
            partNames.Select(partName => File.ReadAllText(Path.Combine(
                directory,
                $"MainViewModel.{partName}.cs"))));
    }

    private static string FindRepositoryDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string solutionPath = Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln");
            if (File.Exists(solutionPath))
                return Path.Combine(directory.FullName, "ExpressPackingMonitoring", "ViewModels");
            directory = directory.Parent;
        }

        throw new FileNotFoundException("ExpressPackingMonitoring.sln");
    }
}
