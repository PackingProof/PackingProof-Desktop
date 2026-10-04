using System;
using System.Collections.Generic;

namespace ExpressPackingMonitoring.UI.SettingsSearch
{
    /// <summary>
    /// 设置搜索的匹配规则：忽略大小写、命中位置可枚举（给高亮用）。
    /// 纯函数，不碰界面，方便单测。
    /// </summary>
    internal static class SettingsSearchMatcher
    {
        internal static string Normalize(string? query) => (query ?? string.Empty).Trim();

        internal static bool IsMatch(string? text, string query) =>
            !string.IsNullOrEmpty(query)
            && !string.IsNullOrEmpty(text)
            && text.Contains(query, StringComparison.OrdinalIgnoreCase);

        /// <summary>返回 text 里所有命中 query 的区间，按出现顺序；没有命中就是空。</summary>
        internal static IReadOnlyList<(int Start, int Length)> FindRanges(string? text, string query)
        {
            var ranges = new List<(int Start, int Length)>();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query) || query.Length > text.Length)
                return ranges;

            int index = 0;
            while (index <= text.Length - query.Length)
            {
                int hit = text.IndexOf(query, index, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                    break;

                ranges.Add((hit, query.Length));
                index = hit + query.Length;
            }

            return ranges;
        }
    }
}
