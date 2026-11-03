namespace ExpressPackingMonitoring.Services;

internal static class SampleContributionPromptPolicy
{
    internal static bool ShouldPrompt(
        bool packaged,
        bool allowPrereleaseUpdates,
        string? currentVersion,
        string? previousRunVersion,
        string? promptedVersion)
    {
        if (packaged
            || !allowPrereleaseUpdates
            || string.IsNullOrWhiteSpace(currentVersion)
            || string.IsNullOrWhiteSpace(previousRunVersion))
        {
            return false;
        }

        return !string.Equals(
                   currentVersion.Trim(),
                   previousRunVersion.Trim(),
                   StringComparison.OrdinalIgnoreCase)
               && !string.Equals(
                   currentVersion.Trim(),
                   promptedVersion?.Trim(),
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldPromptAfterEnhancedRecognitionDisabled(
        bool wasEnabled,
        bool isEnabled) =>
        wasEnabled && !isEnabled;
}
