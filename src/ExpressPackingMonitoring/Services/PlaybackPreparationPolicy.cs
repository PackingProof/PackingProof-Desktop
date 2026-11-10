using ExpressPackingMonitoring.Data;
using System.IO;

namespace ExpressPackingMonitoring.Services;

internal enum PlaybackPreparationState
{
    Ready,
    FallbackSource,
    Unavailable
}

internal readonly record struct PlaybackPreparationResult(
    PlaybackPreparationState State,
    string FilePath,
    bool ConversionFailed = false,
    bool TimedOut = false);

internal static class PlaybackPreparationPolicy
{
    private static readonly TimeSpan DefaultPollInterval =
        TimeSpan.FromMilliseconds(500);

    internal static async Task<PlaybackPreparationResult> PrepareAsync(
        string sourcePath,
        Func<CancellationToken, Task<MkvConversionResult>>? converter,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken,
        TimeSpan? pollInterval = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return new PlaybackPreparationResult(
                PlaybackPreparationState.Unavailable,
                "");

        string path = ResolveSourcePath(sourcePath);

        if (!path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
        {
            return IsCompletedFile(path)
                ? new PlaybackPreparationResult(
                    PlaybackPreparationState.Ready,
                    path)
                : new PlaybackPreparationResult(
                    PlaybackPreparationState.Unavailable,
                    path);
        }

        string convertedPath = Path.ChangeExtension(path, ".mp4");
        bool sourceMkvExists = IsCompletedFile(path);
        bool convertedExists = IsCompletedFile(convertedPath);
        if (convertedExists
            && (!sourceMkvExists
                || (converter == null && !HasRecoverySidecar(path))))
        {
            return new PlaybackPreparationResult(
                PlaybackPreparationState.Ready,
                convertedPath);
        }

        if (converter != null)
        {
            MkvConversionResult conversion;
            try
            {
                conversion = await converter(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                conversion = MkvConversionResult.Fail("");
            }

            if (conversion.Success
                && IsCompletedFile(conversion.FilePath))
            {
                return new PlaybackPreparationResult(
                    PlaybackPreparationState.Ready,
                    conversion.FilePath);
            }
            return sourceMkvExists
                ? new PlaybackPreparationResult(
                    PlaybackPreparationState.FallbackSource,
                    path,
                    ConversionFailed: true)
                : new PlaybackPreparationResult(
                    PlaybackPreparationState.Unavailable,
                    path,
                    ConversionFailed: true);
        }

        TimeSpan interval = pollInterval is { } requested
            && requested > TimeSpan.Zero
                ? requested
                : DefaultPollInterval;
        DateTime deadline = DateTime.UtcNow + waitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(interval, cancellationToken)
                .ConfigureAwait(false);
            bool sourceStillExists = File.Exists(path);
            if (IsCompletedFile(convertedPath)
                && (!sourceStillExists || !HasRecoverySidecar(path)))
            {
                return new PlaybackPreparationResult(
                    PlaybackPreparationState.Ready,
                    convertedPath);
            }
            if (!sourceStillExists)
                break;
        }

        if (!File.Exists(path) && IsCompletedFile(convertedPath))
        {
            return new PlaybackPreparationResult(
                PlaybackPreparationState.Ready,
                convertedPath);
        }
        return IsCompletedFile(path)
            ? new PlaybackPreparationResult(
                PlaybackPreparationState.FallbackSource,
                path,
                TimedOut: true)
            : new PlaybackPreparationResult(
                PlaybackPreparationState.Unavailable,
                path,
                TimedOut: true);
    }

    internal static bool HasRecoverySidecar(string mkvPath) =>
        File.Exists(Path.ChangeExtension(mkvPath, ".wav"))
        || File.Exists(Path.ChangeExtension(mkvPath, ".audio.log"))
        || File.Exists(Path.ChangeExtension(mkvPath, ".embedded-audio"));

    internal static bool CanConvertOnThisMachine(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrWhiteSpace(root)
                && new DriveInfo(root).DriveType != DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    internal static string ResolveSourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || IsCompletedFile(path))
        {
            return path;
        }

        string sourceMkv = Path.ChangeExtension(path, ".mkv");
        return IsCompletedFile(sourceMkv) ? sourceMkv : path;
    }

    private static bool IsCompletedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
