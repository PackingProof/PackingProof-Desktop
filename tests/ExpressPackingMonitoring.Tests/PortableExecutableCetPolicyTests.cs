using ExpressPackingMonitoring.UpdateCore;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class PortableExecutableCetPolicyTests
{
    [Fact]
    public void DisablesCetCompatibilityWithoutChangingOtherPeData()
    {
        string source = Path.Combine(AppContext.BaseDirectory, "ExpressPackingMonitoring.exe");
        Assert.True(File.Exists(source), source);
        Assert.True(PortableExecutableCetPolicy.IsCetCompatibilityEnabled(source), source);

        string fixture = Path.Combine(
            Path.GetTempPath(),
            $"packingproof-cet-{Guid.NewGuid():N}.exe");
        File.Copy(source, fixture);
        try
        {
            Assert.True(PortableExecutableCetPolicy.TryDisableCetCompatibility(
                fixture,
                out bool changed,
                out string? error), error);
            Assert.True(changed);
            Assert.False(PortableExecutableCetPolicy.IsCetCompatibilityEnabled(fixture));

            Assert.True(PortableExecutableCetPolicy.TryDisableCetCompatibility(
                fixture,
                out changed,
                out error), error);
            Assert.False(changed);
        }
        finally
        {
            File.Delete(fixture);
        }
    }
}
