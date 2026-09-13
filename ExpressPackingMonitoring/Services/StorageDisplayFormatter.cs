using System;

namespace ExpressPackingMonitoring.Services
{
    /// <summary>
    /// 存储容量与保存天数的展示换算。
    /// 从 WebServer 抽出来：那里是规模冻结的历史例外，只允许缩小。
    /// </summary>
    internal static class StorageDisplayFormatter
    {
        private const double BytesPerGB = 1073741824.0;

        internal static double BytesToGB(long bytes) => bytes / BytesPerGB;

        /// <summary>
        /// 已保存天数按自然日跨度算，首尾都算在内。
        /// 缺任一端时返回 0；有数据时至少算 1 天，避免当天录像显示成 0 天。
        /// </summary>
        internal static int CalculateSavedDays(DateTime? oldest, DateTime? latest)
        {
            if (!oldest.HasValue || !latest.HasValue) return 0;
            int days = (latest.Value.Date - oldest.Value.Date).Days + 1;
            return Math.Max(1, days);
        }

        /// <summary>10GB 以上不再显示小数位，避免"123.4GB"这种过细的精度。</summary>
        internal static string FormatGB(long bytes)
        {
            double gb = BytesToGB(bytes);
            return gb >= 10 ? $"{gb:F0}GB" : $"{gb:F1}GB";
        }
    }
}
