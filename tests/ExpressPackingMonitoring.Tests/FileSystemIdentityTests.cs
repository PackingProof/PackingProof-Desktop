using ExpressPackingMonitoring.Services;
using System.Runtime.InteropServices;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class FileSystemIdentityTests
{
    [Fact]
    public void SameFileIdentity_RecognizesHardLinkAlias()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"packingproof-file-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.mp4");
            string alias = Path.Combine(directory, "alias.mp4");
            File.WriteAllText(source, "source");
            Assert.True(CreateHardLink(alias, source, IntPtr.Zero));

            Assert.True(FileSystemIdentity.AreSameFile(source, alias));
            Assert.False(FileSystemIdentity.AreSameFile(
                source,
                Path.Combine(directory, "different.mp4")));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
