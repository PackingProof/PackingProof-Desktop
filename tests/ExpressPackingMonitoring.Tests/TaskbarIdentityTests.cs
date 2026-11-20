using ExpressPackingMonitoring.Services;
using System.Buffers.Binary;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class TaskbarIdentityTests
{
    [Fact]
    public void AppAndInstallerUseSameStableIdentity()
    {
        string installer = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "tools", "Installer", "ExpressPackingMonitoring.iss"));

        Assert.Equal("PackingProof.ExpressPackingMonitoring", TaskbarIdentityService.AppUserModelId);
        Assert.Contains(TaskbarIdentityService.AppUserModelId, installer, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationIconProvidesFullBrandingAtTaskbarSizes()
    {
        string projectDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring");
        byte[] icon = File.ReadAllBytes(
            Path.Combine(projectDirectory, "app.ico"));
        int frameCount = BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(4, 2));
        var sizes = new HashSet<int>();
        for (int index = 0; index < frameCount; index++)
        {
            int width = icon[6 + index * 16];
            sizes.Add(width == 0 ? 256 : width);
        }

        Assert.Contains(32, sizes);
        Assert.Contains(40, sizes);
        Assert.Contains(48, sizes);

        string generator = File.ReadAllText(
            Path.Combine(projectDirectory, "GenAdvancedIcon.ps1"));
        Assert.Contains("if (size >= 32)", generator, StringComparison.Ordinal);
        Assert.DoesNotContain("if (size >= 48)", generator, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"D:\repo\src\ExpressPackingMonitoring\bin\Debug\net10.0-windows\win-x64\ExpressPackingMonitoring.exe", true)]
    [InlineData(@"D:\repo\src\ExpressPackingMonitoring\bin\Release\net10.0-windows\win-x64\ExpressPackingMonitoring.exe", true)]
    [InlineData(@"C:\Users\user\AppData\Local\Programs\ExpressPackingMonitoring\app\ExpressPackingMonitoring.exe", false)]
    public void OnlyInstalledLikePathsUseTheExplicitTaskbarIdentity(
        string executablePath,
        bool expected)
    {
        Assert.Equal(
            expected,
            TaskbarIdentityService.IsDevelopmentOutputPath(executablePath));
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
