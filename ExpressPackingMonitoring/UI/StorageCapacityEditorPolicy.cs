using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
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
        double configuredReserveGB) =>
        StorageCapacityPolicy.CalculateCapacityGB(
            totalCapacityGB,
            minimumReserveGB,
            configuredReserveGB);

    internal static double CalculateReserveGB(
        long totalCapacityGB,
        double minimumReserveGB,
        double requestedCapacityGB) =>
        StorageCapacityPolicy.CalculateReserveGB(
            totalCapacityGB,
            minimumReserveGB,
            requestedCapacityGB);

    private static bool TryGetVolumeLimits(
        StorageLocation location,
        out long totalCapacityGB,
        out double minimumReserveGB) =>
        StorageCapacityPolicy.TryGetVolumeLimits(location, out totalCapacityGB, out minimumReserveGB);
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

public sealed class RecordingCacheLocationConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        value is AppConfig config
            && RecordingWorkstationCachePolicy.GetConfiguredLocation(config) is StorageLocation location
                ? new[] { location }
                : Array.Empty<StorageLocation>();

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        Binding.DoNothing;
}
