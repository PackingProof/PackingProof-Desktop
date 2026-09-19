using ExpressPackingMonitoring.Config;
using System;
using System.Collections.Generic;

namespace ExpressPackingMonitoring.UI;

/// <summary>预留空间过小的提示内容；没有低于建议值的位置时返回 null</summary>
internal sealed record StorageReserveWarning(
    int LocationCount,
    string SmallestLocationPath,
    double SmallestReserveGB,
    double RecommendedReserveGB)
{
    internal string Message => LocationCount == 1
        ? $"预留空间偏小：{SmallestLocationPath} 只保留 {FormatGB(SmallestReserveGB)}，"
            + $"建议至少 {FormatGB(RecommendedReserveGB)}；磁盘写满时会影响录像和系统运行"
        : $"预留空间偏小：{LocationCount} 个位置只保留约 {FormatGB(SmallestReserveGB)}，"
            + $"建议至少 {FormatGB(RecommendedReserveGB)}；磁盘写满时会影响录像和系统运行";

    private static string FormatGB(double gigabytes) => $"{gigabytes:0.#} GB";
}

/// <summary>
/// 预留空间提示：用户可以把预留下调到底线（系统盘 2GB、其他位置 1GB），
/// 但那样磁盘很快会被写满，所以低于建议值（系统盘 10GB、其他位置 5GB）时在设置页提示，
/// 只提示不阻断保存。
/// </summary>
internal static class StorageReserveWarningPolicy
{
    internal static StorageReserveWarning? Evaluate(
        IEnumerable<StorageLocation>? locations)
    {
        if (locations == null) return null;

        int count = 0;
        string smallestPath = "";
        double smallestReserveGB = double.MaxValue;
        double recommendedGB = 0;
        foreach (StorageLocation? location in locations)
        {
            if (location == null || string.IsNullOrWhiteSpace(location.Path)) continue;

            double reserveGB = StorageSpacePolicy.GetEffectiveReserveGB(location);
            double suggestionGB = StorageSpacePolicy.GetDefaultReserveGB(location.Path);
            if (reserveGB >= suggestionGB) continue;

            count++;
            if (reserveGB < smallestReserveGB)
            {
                smallestReserveGB = reserveGB;
                smallestPath = location.Path;
            }
            recommendedGB = Math.Max(recommendedGB, suggestionGB);
        }

        return count == 0
            ? null
            : new StorageReserveWarning(
                count,
                smallestPath,
                smallestReserveGB,
                recommendedGB);
    }
}
