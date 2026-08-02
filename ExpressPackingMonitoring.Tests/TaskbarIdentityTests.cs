using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class TaskbarIdentityTests
{
    [Fact]
    public void AppAndInstallerUseSameStableIdentity()
    {
        string installer = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Installer", "ExpressPackingMonitoring.iss"));

        Assert.Equal("PackingProof.ExpressPackingMonitoring", TaskbarIdentityService.AppUserModelId);
        Assert.Contains(TaskbarIdentityService.AppUserModelId, installer, StringComparison.Ordinal);
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
