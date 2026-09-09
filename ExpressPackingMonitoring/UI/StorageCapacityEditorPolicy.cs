using ExpressPackingMonitoring.Config;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ExpressPackingMonitoring.UI;

internal sealed record StorageCapacityEditorState(
    StorageLocation Location,
    double? CapacityGB,
    double MaximumGB,
    bool IsAvailable)
{
    public Visibility EditorVisibility =>
        IsAvailable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility UnavailableVisibility =>
        IsAvailable ? Visibility.Collapsed : Visibility.Visible;
}

internal static class StorageCapacityEditorPolicy
{
    public static StorageCapacityEditorState CreateState(StorageLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);

        if (!TryGetVolumeLimits(
                location,
                out long totalCapacityGB,
                out double minimumReserveGB))
        {
            return new StorageCapacityEditorState(location, null, 0, false);
        }

        double maximumCapacityGB = Math.Max(0, totalCapacityGB - minimumReserveGB);
        if (maximumCapacityGB < 1)
            return new StorageCapacityEditorState(location, null, 0, false);

        double capacityGB = CalculateCapacityGB(
            totalCapacityGB,
            minimumReserveGB,
            location.ReserveGB);
        return new StorageCapacityEditorState(
            location,
            capacityGB,
            maximumCapacityGB,
            true);
    }

    public static bool TryApplyCapacity(StorageLocation location, double capacityGB)
    {
        ArgumentNullException.ThrowIfNull(location);

        if (!TryGetVolumeLimits(
                location,
                out long totalCapacityGB,
                out double minimumReserveGB))
        {
            return false;
        }

        location.ReserveGB = CalculateReserveGB(
            totalCapacityGB,
            minimumReserveGB,
            capacityGB);
        return true;
    }

    internal static double CalculateCapacityGB(
        long totalCapacityGB,
        double minimumReserveGB,
        double configuredReserveGB)
    {
        double normalizedMinimum = NormalizeReserve(minimumReserveGB);
        double normalizedConfigured = double.IsFinite(configuredReserveGB)
            && configuredReserveGB > 0
                ? Math.Ceiling(configuredReserveGB)
                : normalizedMinimum;
        double effectiveReserve = Math.Max(
            normalizedMinimum,
            normalizedConfigured);
        double maximumCapacity = Math.Max(0, totalCapacityGB - normalizedMinimum);
        return Math.Clamp(
            totalCapacityGB - effectiveReserve,
            0,
            maximumCapacity);
    }

    internal static double CalculateReserveGB(
        long totalCapacityGB,
        double minimumReserveGB,
        double requestedCapacityGB)
    {
        double normalizedMinimum = NormalizeReserve(minimumReserveGB);
        double maximumCapacity = Math.Max(0, totalCapacityGB - normalizedMinimum);
        if (maximumCapacity < 1)
            return normalizedMinimum;

        double normalizedCapacity = double.IsFinite(requestedCapacityGB)
            ? Math.Round(requestedCapacityGB, MidpointRounding.AwayFromZero)
            : maximumCapacity;
        normalizedCapacity = Math.Clamp(normalizedCapacity, 1, maximumCapacity);
        return Math.Max(normalizedMinimum, totalCapacityGB - normalizedCapacity);
    }

    private static bool TryGetVolumeLimits(
        StorageLocation location,
        out long totalCapacityGB,
        out double minimumReserveGB)
    {
        totalCapacityGB = 0;
        minimumReserveGB = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(location.Path)
                || !StorageVolumeInfo.TryGet(location.Path, out StorageVolumeInfo volume))
            {
                return false;
            }

            totalCapacityGB = volume.TotalSize / StorageSpacePolicy.BytesPerGiB;
            minimumReserveGB = Math.Ceiling(
                StorageSpacePolicy.CalculateMinimumReserveBytes(volume)
                / (double)StorageSpacePolicy.BytesPerGiB);
            return totalCapacityGB > minimumReserveGB;
        }
        catch
        {
            return false;
        }
    }

    private static double NormalizeReserve(double reserveGB) =>
        double.IsFinite(reserveGB) && reserveGB > 0
            ? Math.Ceiling(reserveGB)
            : 0;
}

public sealed class StorageCapacityEditorConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        value is StorageLocation location
            ? StorageCapacityEditorPolicy.CreateState(location)
            : Binding.DoNothing;

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        Binding.DoNothing;
}
