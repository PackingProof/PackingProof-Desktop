using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ExtensionDocumentationTests
{
    [Fact]
    public void ExtensionApiV1_DocumentsImplementedSignedLifecycleAndJavaScriptExample()
    {
        string root = FindRepositoryRoot();
        string document = File.ReadAllText(Path.Combine(root, "docs", "EXTENSION_API_V1.md"));
        string examplePath = Path.Combine(root, "docs", "examples", "extension-v1-minimal.js");
        string example = File.ReadAllText(examplePath);
        string scaleExamplePath = Path.Combine(root, "docs", "examples", "extension-v1-serial-scale.js");
        string scaleExample = File.ReadAllText(scaleExamplePath);
        string examplesPackage = File.ReadAllText(Path.Combine(root, "docs", "examples", "package.json"));

        Assert.Contains("POST /api/extensions/v1/enroll", document);
        Assert.Contains("packingproof-extension-request-v1", document);
        Assert.Contains("GET /api/extensions/v1/scan-tasks/next?waitSeconds=20", document);
        Assert.Contains("POST /api/extensions/v1/scan-results", document);
        Assert.Contains("extension_auth_replay_detected", document);
        Assert.Contains("examples/extension-v1-minimal.js", document);
        Assert.Contains("examples/extension-v1-serial-scale.js", document);
        Assert.Contains("ST,NT,+32.1000kg", document);
        Assert.DoesNotContain("EXTENSION_API_ROADMAP.md", document);

        Assert.Contains("export class PackingProofExtensionClient", example);
        Assert.Contains("globalThis.crypto.subtle", example);
        Assert.Contains("credentialState", example);
        Assert.DoesNotContain("const credential =", example);

        Assert.Contains("baudRate: 9600", scaleExample);
        Assert.Contains("dataBits: 8", scaleExample);
        Assert.Contains("stopBits: 1", scaleExample);
        Assert.Contains("parity: 'none'", scaleExample);
        Assert.Contains("parseYingzhanFrame", scaleExample);
        Assert.Contains("measurementType: 'weight'", scaleExample);
        Assert.Contains("client.nextTask(20)", scaleExample);
        Assert.DoesNotContain("const credential =", scaleExample);
        Assert.Contains("\"type\": \"module\"", examplesPackage);
        Assert.Contains("\"serialport\": \"^13.0.0\"", examplesPackage);
    }

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(startPath));
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }
}
