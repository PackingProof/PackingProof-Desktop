using ExpressPackingMonitoring.Services;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class RecordingSamplePackageTests
{
    private const string TestNodeId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void ContinuousModeUsesOnlyTheOpeningWindow()
    {
        var sources = Enumerable.Range(0, 200)
            .Select(index => new RecordingSampleSource(
                index + 1,
                $"video-{index}.mp4",
                new DateTime(2026, 1, 1).AddDays(index),
                60,
                "发货",
                "扫码切换"))
            .ToArray();

        IReadOnlyList<RecordingSamplePick> picks =
            RecordingSampleSelectionPolicy.Select(sources, 120, new Random(12345));

        Assert.Equal(120, picks.Count);
        Assert.Equal(120, picks.Select(pick => pick.Source.Id).Distinct().Count());
        Assert.Equal(new DateTime(2026, 1, 1), picks.Min(pick => pick.Source.StartTime).Date);
        Assert.Equal(new DateTime(2026, 7, 19), picks.Max(pick => pick.Source.StartTime).Date);
        Assert.All(picks, pick =>
        {
            Assert.Equal(RecordingSampleCapturePhase.Start, pick.CapturePhase);
            Assert.InRange(pick.PositionRatio, 0, 0.25);
        });
    }

    [Fact]
    public void SameCodeModeBalancesOpeningAndEndingWindowsAcrossVideos()
    {
        var sources = Enumerable.Range(0, 200)
            .Select(index => new RecordingSampleSource(
                index + 1,
                $"video-{index}.mp4",
                new DateTime(2026, 1, 1).AddDays(index),
                80,
                "发货",
                index % 3 == 0 ? "同码停录（音频异常）" : "同码停录"))
            .ToArray();

        IReadOnlyList<RecordingSamplePick> picks =
            RecordingSampleSelectionPolicy.Select(sources, 120, new Random(12345));

        Assert.Equal(
            60,
            picks.Count(pick => pick.CapturePhase == RecordingSampleCapturePhase.Start));
        Assert.Equal(
            60,
            picks.Count(pick => pick.CapturePhase == RecordingSampleCapturePhase.End));
        Assert.All(picks, pick =>
        {
            if (pick.CapturePhase == RecordingSampleCapturePhase.Start)
                Assert.InRange(pick.PositionRatio, 0, 0.25);
            else
                Assert.InRange(pick.PositionRatio, 0.75, 1);
        });
    }

    [Theory]
    [InlineData("同码停录", true)]
    [InlineData("同码停录（音频异常）", true)]
    [InlineData("扫码切换", false)]
    [InlineData("手动", false)]
    [InlineData("", false)]
    [InlineData("未知模式", false)]
    public void StopReasonSelectsHistoricalCaptureMode(
        string stopReason,
        bool expectedSameCode)
    {
        Assert.Equal(
            expectedSameCode,
            RecordingSampleSelectionPolicy.IsSameCodeStopReason(stopReason));
    }

    [Theory]
    [InlineData(40, 10)]
    [InlineData(60, 15)]
    [InlineData(100, 15)]
    public void CaptureWindowUsesQuarterDurationCappedAtFifteenSeconds(
        double durationSeconds,
        double expectedWindowSeconds)
    {
        Assert.Equal(
            expectedWindowSeconds,
            RecordingSampleSelectionPolicy.GetCaptureWindowSeconds(durationSeconds),
            3);
    }

    [Fact]
    public void SelectionFillsFromSameDatesWhenThereAreFewDates()
    {
        var sources = Enumerable.Range(0, 10)
            .Select(index => new RecordingSampleSource(
                index + 1,
                $"video-{index}.mp4",
                new DateTime(2026, 1, 1).AddHours(index),
                60,
                "发货"))
            .ToArray();

        IReadOnlyList<RecordingSamplePick> picks =
            RecordingSampleSelectionPolicy.Select(sources, 8, new Random(7));

        Assert.Equal(8, picks.Count);
        Assert.Equal(8, picks.Select(pick => pick.Source.Id).Distinct().Count());
        Assert.Single(picks.Select(pick => pick.Source.StartTime.Date).Distinct());
    }

    [Fact]
    public void CreatePackageContainsOnlyImagesManifestAndReadme()
    {
        string directory = CreateTempDirectory();
        try
        {
            var sources = new[]
            {
                new RecordingSampleSource(
                    1,
                    Path.Combine(directory, "recording-a.mp4"),
                    new DateTime(2026, 3, 24, 10, 0, 0),
                    80,
                    "发货"),
                new RecordingSampleSource(
                    2,
                    Path.Combine(directory, "recording-b.mp4"),
                    new DateTime(2026, 6, 12, 14, 0, 0),
                    75,
                    "发货")
            };
            string feedbackDirectory = Path.Combine(directory, "cache", "feedback");
            var service = new RecordingSamplePackageService(
                directory,
                TestNodeId,
                outputDir: feedbackDirectory,
                appVersion: "v-test",
                sourcesProvider: () => sources,
                random: new Random(9),
                frameExtractor: (
                    string _,
                    string outputPath,
                    double _,
                    CancellationToken _,
                    out string error) =>
                {
                    error = "";
                    File.WriteAllBytes(outputPath, new byte[4096]);
                    return true;
                });

            RecordingSamplePackageResult result =
                service.CreatePackage(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, result.ImageCount);
            Assert.Empty(result.Warnings);
            Assert.StartsWith("PackingProof_RecognitionSamples_", Path.GetFileName(result.ZipPath));
            using ZipArchive zip = ZipFile.OpenRead(result.ZipPath);
            string[] names = zip.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Contains("manifest.json", names);
            Assert.Contains("README.txt", names);
            Assert.Contains("images/sample_0001.jpg", names);
            Assert.Contains("images/sample_0002.jpg", names);
            Assert.DoesNotContain("videos.db", names);
            Assert.DoesNotContain("config.json", names);

            using JsonDocument manifest = JsonDocument.Parse(ReadEntry(zip, "manifest.json"));
            Assert.Equal(2, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(2, manifest.RootElement.GetProperty("imageCount").GetInt32());
            Assert.Equal(
                RecordingSamplePackageService.SelectionRuleVersion,
                manifest.RootElement.GetProperty("selectionRule").GetString());
            Assert.Matches(
                "^[0-9a-f]{32}$",
                manifest.RootElement.GetProperty("deviceFingerprint").GetString()!);
            Assert.Matches(
                "^[0-9a-f]{32}$",
                manifest.RootElement.GetProperty("contributionId").GetString()!);
            foreach (JsonElement sample in manifest.RootElement.GetProperty("samples").EnumerateArray())
            {
                string imagePath = sample.GetProperty("file").GetString()!;
                Assert.StartsWith("images/sample_", imagePath);
                Assert.Matches(
                    "^[0-9a-f]{32}$",
                    sample.GetProperty("sourceFingerprint").GetString()!);
                string phase = sample.GetProperty("capturePhase").GetString()!;
                string packingMode = sample.GetProperty("packingMode").GetString()!;
                double position = sample.GetProperty("framePositionRatio").GetDouble();
                Assert.Equal("continuous", packingMode);
                if (phase == "start")
                    Assert.InRange(position, 0, 0.25);
                else
                {
                    Assert.Equal("end", phase);
                    Assert.InRange(position, 0.75, 1);
                }
            }
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void ContributionIdentityPersistsAndPreviouslySampledSourcesAreSkipped()
    {
        string directory = CreateTempDirectory();
        try
        {
            var sources = new[]
            {
                new RecordingSampleSource(1, "a.mp4", new DateTime(2026, 1, 1), 60),
                new RecordingSampleSource(2, "b.mp4", new DateTime(2026, 2, 1), 60)
            };
            var store = new RecordingSampleContributionStore(directory, TestNodeId);
            string initialFingerprint = store.Load().DeviceFingerprint;
            Assert.Equal(initialFingerprint, new RecordingSampleContributionStore(
                directory,
                TestNodeId).Load().DeviceFingerprint);

            RecordingSampleFrameExtractor extractor = (
                string _,
                string outputPath,
                double _,
                CancellationToken _,
                out string error) =>
            {
                error = "";
                File.WriteAllBytes(outputPath, new byte[1024]);
                return true;
            };
            var firstService = new RecordingSamplePackageService(
                directory,
                TestNodeId,
                outputDir: Path.Combine(directory, "cache", "feedback"),
                sourcesProvider: () => sources,
                frameExtractor: extractor);
            RecordingSamplePackageResult first = firstService.CreatePackage(
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, first.ImageCount);

            RecordingSampleContributionState saved = store.Load();
            Assert.Equal(initialFingerprint, saved.DeviceFingerprint);
            Assert.Equal(2, saved.SampledSourceFingerprints.Count);

            var secondService = new RecordingSamplePackageService(
                directory,
                TestNodeId,
                outputDir: Path.Combine(directory, "cache", "feedback"),
                sourcesProvider: () => sources,
                frameExtractor: extractor);
            Assert.Throws<InvalidOperationException>(() =>
                secondService.CreatePackage(
                    cancellationToken: TestContext.Current.CancellationToken));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void CreatePackageSkipsFailedExtractionsAndReportsThem()
    {
        string directory = CreateTempDirectory();
        try
        {
            var sources = new[]
            {
                new RecordingSampleSource(1, "a.mp4", new DateTime(2026, 1, 1), 60),
                new RecordingSampleSource(2, "b.mp4", new DateTime(2026, 2, 1), 60)
            };
            var service = new RecordingSamplePackageService(
                directory,
                TestNodeId,
                outputDir: Path.Combine(directory, "cache", "feedback"),
                sourcesProvider: () => sources,
                random: new Random(3),
                frameExtractor: (
                    string _,
                    string outputPath,
                    double _,
                    CancellationToken _,
                    out string error) =>
                {
                    if (outputPath.EndsWith("0001.jpg", StringComparison.Ordinal))
                    {
                        error = "test failure";
                        return false;
                    }

                    error = "";
                    File.WriteAllBytes(outputPath, new byte[1024]);
                    return true;
                });

            RecordingSamplePackageResult result =
                service.CreatePackage(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, result.ImageCount);
            Assert.Equal(2, result.AttemptedCount);
            Assert.Single(result.Warnings);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void CreatePackageStopsAtTargetCount()
    {
        string directory = CreateTempDirectory();
        try
        {
            var sources = Enumerable.Range(0, 260)
                .Select(index => new RecordingSampleSource(
                    index + 1,
                    $"video-{index}.mp4",
                    new DateTime(2026, 1, 1).AddDays(index),
                    60))
                .ToArray();
            var service = new RecordingSamplePackageService(
                directory,
                TestNodeId,
                outputDir: Path.Combine(directory, "cache", "feedback"),
                sourcesProvider: () => sources,
                random: new Random(11),
                frameExtractor: (
                    string _,
                    string outputPath,
                    double _,
                    CancellationToken _,
                    out string error) =>
                {
                    error = "";
                    File.WriteAllBytes(outputPath, new byte[512]);
                    return true;
                });

            RecordingSamplePackageResult result =
                service.CreatePackage(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(RecordingSamplePackageService.DefaultTargetCount, result.ImageCount);
            Assert.Equal(RecordingSamplePackageService.DefaultTargetCount, result.AttemptedCount);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public void CreatePackageUsesBoundedConcurrentFrameExtraction()
    {
        string directory = CreateTempDirectory();
        try
        {
            var sources = Enumerable.Range(0, 12)
                .Select(index => new RecordingSampleSource(
                    index + 1,
                    $"video-{index}.mp4",
                    new DateTime(2026, 1, 1).AddDays(index),
                    60))
                .ToArray();
            int activeExtractions = 0;
            int maximumActiveExtractions = 0;
            var service = new RecordingSamplePackageService(
                directory,
                TestNodeId,
                outputDir: Path.Combine(directory, "cache", "feedback"),
                sourcesProvider: () => sources,
                frameExtractor: (
                    string _,
                    string outputPath,
                    double _,
                    CancellationToken _,
                    out string error) =>
                {
                    int active = Interlocked.Increment(ref activeExtractions);
                    UpdateMaximum(ref maximumActiveExtractions, active);
                    try
                    {
                        Thread.Sleep(75);
                        error = "";
                        File.WriteAllBytes(outputPath, new byte[512]);
                        return true;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeExtractions);
                    }
                });

            RecordingSamplePackageResult result =
                service.CreatePackage(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(sources.Length, result.ImageCount);
            Assert.Equal(sources.Length, result.AttemptedCount);
            Assert.InRange(
                maximumActiveExtractions,
                2,
                RecordingSamplePackageService.MaxConcurrentFrameExtractions);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "epm-recording-samples",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = Assert.Single(archive.Entries, item => item.FullName == name);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        int observed;
        while (value > (observed = Volatile.Read(ref maximum))
               && Interlocked.CompareExchange(ref maximum, value, observed) != observed)
        {
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch
        {
        }
    }
}
