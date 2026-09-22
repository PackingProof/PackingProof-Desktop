using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using System.Globalization;
using System.Windows.Data;

namespace ExpressPackingMonitoring.UI;

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
        CultureInfo culture) => Binding.DoNothing;
}
