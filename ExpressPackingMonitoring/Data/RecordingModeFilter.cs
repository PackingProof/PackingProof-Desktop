using System;

namespace ExpressPackingMonitoring.Data
{
    /// <summary>
    /// 录像业务类型筛选值的归一化。
    /// 界面与接口上用的是发货/退货，查询层要的是 shipping/return，
    /// 两边口径不一致会让筛选静默失效，所以统一走这里转换。
    /// </summary>
    public static class RecordingModeFilter
    {
        public const string Shipping = "shipping";
        public const string Return = "return";

        /// <summary>
        /// 把界面传来的值转成查询层接受的 token。
        /// 空值、"all" 或无法识别的值一律当作不筛选，返回空字符串，
        /// 避免把用户的"全部"误判成某一种类型。
        /// </summary>
        public static string Normalize(string? value)
        {
            string normalized = value?.Trim() ?? "";
            if (normalized.Length == 0) return "";

            return normalized.ToLowerInvariant() switch
            {
                "shipping" or "发货" => Shipping,
                "return" or "退货" => Return,
                _ => ""
            };
        }

        /// <summary>是否为一个真实生效的筛选值。</summary>
        public static bool IsActive(string? value) => Normalize(value).Length > 0;

        /// <summary>用于展示的中文文案。</summary>
        public static string ToDisplayText(string? value) => Normalize(value) switch
        {
            Return => "退货",
            Shipping => "发货",
            _ => ""
        };

        /// <summary>
        /// 判断一条记录的 Mode 是否命中筛选。
        /// 与 BuildVideoQueryWhere 的 SQL 保持同一口径：
        /// 历史数据里发货可能存成 shipping、发货、空串或 NULL。
        /// </summary>
        public static bool Matches(string? recordMode, string? filter)
        {
            string normalizedFilter = Normalize(filter);
            if (normalizedFilter.Length == 0) return true;

            string recordToken = Normalize(recordMode);
            if (recordToken.Length == 0)
                recordToken = Shipping;

            return string.Equals(recordToken, normalizedFilter, StringComparison.Ordinal);
        }
    }
}
