namespace ExpressPackingMonitoring.Config;

/// <summary>
/// 存储位置"容量上限"与"预留空间"的换算。
///
/// 设置页以容量上限呈现，配置层保存的却是预留值（<see cref="StorageLocation.ReserveGB"/>），
/// 因此两个方向的换算只能有一份实现：桌面设置窗口、Mac 菜单与自动化都走这里，
/// 免得三处各算一套、同一个磁盘在不同入口显示不同的容量。
/// 最低预留的规则仍然只由 <see cref="StorageSpacePolicy"/> 提供。
/// </summary>
internal static class StorageCapacityPolicy
{
    /// <summary>
    /// 容量上限编辑所需的卷容量口径：卷总容量（整数 GB）与最低预留（GB）。
    /// 读不到卷、或卷小到放不下最低预留时返回 false（此时不该让用户设上限）。
    /// </summary>
    public static bool TryGetVolumeLimits(
        StorageLocation location,
        out long totalCapacityGB,
        out double minimumReserveGB)
    {
        totalCapacityGB = 0;
        minimumReserveGB = 0;
        try
        {
            if (location == null
                || string.IsNullOrWhiteSpace(location.Path)
                || !StorageVolumeInfo.TryGet(location.Path, out StorageVolumeInfo volume)
                || volume.TotalSize <= 0)
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

    /// <summary>该位置当前生效的容量上限（GB）：卷总容量减去有效预留。</summary>
    public static bool TryGetCapacityGB(
        StorageLocation location,
        out double capacityGB,
        out double maximumCapacityGB)
    {
        capacityGB = 0;
        maximumCapacityGB = 0;
        if (!TryGetVolumeLimits(location, out long totalCapacityGB, out double minimumReserveGB))
            return false;

        capacityGB = CalculateCapacityGB(totalCapacityGB, minimumReserveGB, location.ReserveGB);
        maximumCapacityGB = Math.Max(0, totalCapacityGB - minimumReserveGB);
        return true;
    }

    /// <summary>该位置卷的总容量（GB，向下取整）；读不到卷时返回 false。</summary>
    public static bool TryGetTotalGB(StorageLocation location, out long totalGB)
    {
        totalGB = 0;
        if (!TryGetVolumeLimits(location, out long totalCapacityGB, out _)) return false;
        totalGB = totalCapacityGB;
        return true;
    }

    /// <summary>按容量上限（GB）反推并写入预留值；超出最大可用容量时按最大容量收口。</summary>
    public static bool TryApplyCapacityGB(StorageLocation location, double capacityGB)
    {
        if (!TryGetVolumeLimits(location, out long totalCapacityGB, out double minimumReserveGB))
            return false;

        location.ReserveGB = CalculateReserveGB(totalCapacityGB, minimumReserveGB, capacityGB);
        return true;
    }

    /// <summary>按预留空间（GB）直接写入；低于底线时按底线收口。</summary>
    public static bool TryApplyReserveGB(StorageLocation location, double reserveGB)
    {
        if (!TryGetVolumeLimits(location, out long totalCapacityGB, out double minimumReserveGB))
            return false;

        double maximumReserveGB = Math.Max(minimumReserveGB, totalCapacityGB - 1);
        double requested = double.IsFinite(reserveGB) ? Math.Ceiling(reserveGB) : minimumReserveGB;
        location.ReserveGB = Math.Clamp(requested, minimumReserveGB, maximumReserveGB);
        return true;
    }

    /// <summary>容量上限 → 预留值：容量越大留得越少，返回的预留不低于底线。</summary>
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

    /// <summary>预留值 → 容量上限：没单独设过预留时按最低预留算，得到可设的最大容量。</summary>
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
        double effectiveReserve = Math.Max(normalizedMinimum, normalizedConfigured);
        double maximumCapacity = Math.Max(0, totalCapacityGB - normalizedMinimum);
        return Math.Clamp(totalCapacityGB - effectiveReserve, 0, maximumCapacity);
    }

    private static double NormalizeReserve(double reserveGB) =>
        double.IsFinite(reserveGB) && reserveGB > 0
            ? Math.Ceiling(reserveGB)
            : 0;
}
