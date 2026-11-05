using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class MkvConversionSafetyTests
{
    [Fact]
    public void HasMkvConversionCandidate_RequiresMkvOrMatchingMp4OnDisk()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string mkvPath = Path.Combine(directory, "recording.mkv");
            Assert.False(MainViewModel.HasMkvConversionCandidate(mkvPath));

            File.WriteAllBytes(mkvPath, [1]);
            Assert.True(MainViewModel.HasMkvConversionCandidate(mkvPath));

            File.Delete(mkvPath);
            File.WriteAllBytes(Path.ChangeExtension(mkvPath, ".mp4"), [1]);
            Assert.True(MainViewModel.HasMkvConversionCandidate(mkvPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IsCompletedConcurrentMkvConversion_AcceptsNonEmptyMp4AfterMkvWasRemoved()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string mkvPath = Path.Combine(directory, "recording.mkv");
            string mp4Path = Path.Combine(directory, "recording.mp4");
            File.WriteAllBytes(mp4Path, [1, 2, 3]);

            Assert.True(MainViewModel.IsCompletedConcurrentMkvConversion(mkvPath, mp4Path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IsCompletedConcurrentMkvConversion_RejectsOutputWhileSourceStillExists()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string mkvPath = Path.Combine(directory, "recording.mkv");
            string mp4Path = Path.Combine(directory, "recording.mp4");
            File.WriteAllBytes(mkvPath, [1]);
            File.WriteAllBytes(mp4Path, [1, 2, 3]);

            Assert.False(MainViewModel.IsCompletedConcurrentMkvConversion(mkvPath, mp4Path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IsCompletedConcurrentMkvConversion_RejectsEmptyOrMissingOutput()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string mkvPath = Path.Combine(directory, "recording.mkv");
            string mp4Path = Path.Combine(directory, "recording.mp4");
            File.WriteAllBytes(mp4Path, []);

            Assert.False(MainViewModel.IsCompletedConcurrentMkvConversion(mkvPath, mp4Path));
            File.Delete(mp4Path);
            Assert.False(MainViewModel.IsCompletedConcurrentMkvConversion(mkvPath, mp4Path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, true, true, true, true)]
    [InlineData(true, true, true, true, false)]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(false, true, true, false, false)]
    public void ExistingMp4MustPassValidationAndFlushBeforeMkvCanBeDeleted(
        bool hasRecoverySidecar,
        bool existsAndIsNonEmpty,
        bool validationPassed,
        bool flushedToDisk,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainViewModel.CanUseExistingConvertedMp4(
                hasRecoverySidecar,
                existsAndIsNonEmpty,
                validationPassed,
                flushedToDisk));
    }

    [Fact]
    public void StaleConversionTemps_AreRemovedWithoutTouchingFreshOrFinalFiles()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string finalPath = Path.Combine(directory, "recording.mp4");
            string stalePath = Path.Combine(directory, "recording.converting-old.mp4");
            string freshPath = Path.Combine(directory, "recording.converting-new.mp4");
            string otherPath = Path.Combine(directory, "recording.cache.mp4");
            File.WriteAllBytes(finalPath, [1]);
            File.WriteAllBytes(stalePath, [2]);
            File.WriteAllBytes(freshPath, [3]);
            File.WriteAllBytes(otherPath, [4]);
            DateTime now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(stalePath, now.AddHours(-2));
            File.SetLastWriteTimeUtc(freshPath, now.AddMinutes(-5));

            int removed = MainViewModel.CleanupStaleMkvConversionTemps(
                [finalPath],
                now,
                TimeSpan.FromHours(1));

            Assert.Equal(1, removed);
            Assert.False(File.Exists(stalePath));
            Assert.True(File.Exists(freshPath));
            Assert.True(File.Exists(finalPath));
            Assert.True(File.Exists(otherPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StaleConversionCleanupRunsBeforeFfmpegAvailabilityCheck()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ExpressPackingMonitoring",
            "ViewModels",
            "MainViewModel.Media.cs"));
        int cleanup = source.IndexOf(
            "CleanupStaleMkvConversionTempsIfNeeded(activePaths);",
            StringComparison.Ordinal);
        int ffmpegCheck = source.IndexOf(
            "string ffmpegPath = FindFFmpeg();",
            cleanup,
            StringComparison.Ordinal);

        Assert.True(cleanup >= 0);
        Assert.True(ffmpegCheck > cleanup);
    }

    [Fact]
    public void FailedSecondTask_DoesNotDeleteMp4CreatedByFirstTask()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string mkvPath = Path.Combine(directory, "recording.mkv");
            string mp4Path = Path.Combine(directory, "recording.mp4");
            File.WriteAllBytes(mkvPath, [1, 2, 3]);

            // Task A completes the conversion and removes the source MKV.
            File.WriteAllBytes(mp4Path, [4, 5, 6]);
            File.Delete(mkvPath);

            // Task B then observes the missing MKV and enters failure cleanup.
            MainViewModel.DeleteIncompleteMp4WhileSourceIsPreserved(mkvPath);

            Assert.True(File.Exists(mp4Path));
            Assert.Equal(3, new FileInfo(mp4Path).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ExpressPackingMonitoring.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "ExpressPackingMonitoring.sln")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("找不到仓库根目录");
    }
}
