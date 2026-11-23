using System.Windows.Controls;

namespace ExpressPackingMonitoring.UI;

internal static class SettingsPurposeSelector
{
    internal static void Apply(
        ComboBox comboBox,
        IReadOnlyList<DeploymentPurposeOption> availablePurposes)
    {
        if (availablePurposes.Count == 0)
            return;

        HashSet<string> availablePresets = availablePurposes
            .Select(option => option.Preset)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int index = comboBox.Items.Count - 1; index >= 0; index--)
        {
            if (comboBox.Items[index] is ComboBoxItem item
                && item.Tag is string preset
                && !availablePresets.Contains(preset))
            {
                comboBox.Items.RemoveAt(index);
            }
        }
    }
}
