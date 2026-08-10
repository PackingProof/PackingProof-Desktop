using ExpressPackingMonitoring.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class NasArchiveProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "epm-nas-provider-" + Guid.NewGuid().ToString("N"));

    public NasArchiveProviderTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private (string Source, string Dest) Prepare(string content, string expectedSha256)
    {
        string source = Path.Combine(_directory, "source.bin");
        string dest = Path.Combine(_directory, "dest.bin");
        File.WriteAllText(source, content);
        return (source, dest);
    }

    [Fact]
    public async Task PublishFileAsync_CopiesWithLengthCheck()
    {
        (string source, string dest) = Prepare("hello-publish", "ignored");
        var provider = new NasArchiveProvider();

        await provider.PublishFileAsync(source, dest, 1, "ignored", CancellationToken.None);

        Assert.True(File.Exists(dest));
        Assert.Equal("hello-publish", File.ReadAllText(dest));
    }

    [Fact]
    public async Task PublishFileAsync_ExistingSameSizeSameContent_IsIdempotent()
    {
        (string source, string dest) = Prepare("same", "ignored");
        File.WriteAllText(dest, "same");
        var provider = new NasArchiveProvider();
        string expectedSha256 = await NasArchiveProvider.ComputeSha256FileAsync(
            source,
            CancellationToken.None);

        await provider.PublishFileAsync(
            source,
            dest,
            1,
            expectedSha256,
            CancellationToken.None);

        Assert.Equal("same", File.ReadAllText(dest));
    }

    [Fact]
    public async Task PublishFileAsync_ExistingDifferentSize_ThrowsConflict()
    {
        (string source, string dest) = Prepare("short", "ignored");
        File.WriteAllText(dest, "a-much-longer-content");
        var provider = new NasArchiveProvider();

        await Assert.ThrowsAsync<ArchiveConflictException>(() =>
            provider.PublishFileAsync(source, dest, 1, "ignored", CancellationToken.None));
    }

    [Fact]
    public async Task PublishFileAsync_ExistingSameSizeDifferentContent_ThrowsConflict()
    {
        (string source, string dest) = Prepare("abc", "ignored");
        File.WriteAllText(dest, "abd");
        var provider = new NasArchiveProvider();

        await Assert.ThrowsAsync<ArchiveConflictException>(() =>
            provider.PublishFileAsync(source, dest, 1, "ignored", CancellationToken.None));
        Assert.Equal("abd", File.ReadAllText(dest));
    }

    [Fact]
    public async Task PublishFileAsync_CleansLeftoverTempWhenSourceExists()
    {
        (string source, string dest) = Prepare("recover", "ignored");
        string temp = dest + ".7.uploading";
        File.WriteAllText(temp, "incomplete");
        var provider = new NasArchiveProvider();

        await provider.PublishFileAsync(source, dest, 7, "ignored", CancellationToken.None);

        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(dest));
    }
}
