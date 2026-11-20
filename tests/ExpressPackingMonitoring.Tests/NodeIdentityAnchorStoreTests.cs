using ExpressPackingMonitoring.Config;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class NodeIdentityAnchorStoreTests
{
    [Fact]
    public void IndependentAnchorRestoresNodeIdAfterConfigLoss()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string originalNodeId = Guid.NewGuid().ToString("D");
            var original = new AppConfig { NodeId = originalNodeId };
            NodeIdentityAnchorStore.Apply(
                original,
                directory,
                machineIdentitySeed: null);

            var recovered = new AppConfig();
            NodeIdentityAnchorStore.Apply(
                recovered,
                directory,
                machineIdentitySeed: "different-machine");

            Assert.Equal(originalNodeId, recovered.NodeId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AnchorWinsWhenConfigWasReplacedByAnotherIdentity()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string anchoredNodeId = Guid.NewGuid().ToString("D");
            NodeIdentityAnchorStore.Apply(
                new AppConfig { NodeId = anchoredNodeId },
                directory,
                machineIdentitySeed: null);

            var replaced = new AppConfig
            {
                NodeId = Guid.NewGuid().ToString("D")
            };
            Assert.True(NodeIdentityAnchorStore.Apply(
                replaced,
                directory,
                machineIdentitySeed: null));

            Assert.Equal(anchoredNodeId, replaced.NodeId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MachineSeedProducesStableStandardNodeIds()
    {
        string first = Assert.IsType<string>(
            NodeIdentityAnchorStore.CreateNodeIdFromSeed("machine-guid"));
        string second = Assert.IsType<string>(
            NodeIdentityAnchorStore.CreateNodeIdFromSeed("machine-guid"));
        string other = Assert.IsType<string>(
            NodeIdentityAnchorStore.CreateNodeIdFromSeed("other-machine"));

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.True(Guid.TryParse(first, out Guid parsed));
        Assert.NotEqual(Guid.Empty, parsed);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"node-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
