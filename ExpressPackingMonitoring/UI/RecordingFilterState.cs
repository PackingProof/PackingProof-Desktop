using System;
using System.Collections.Generic;
using ExpressPackingMonitoring.Data;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 一个生效中的筛选条件，用于在搜索框旁边显示可移除的胶囊徽章。
    /// <see cref="Key"/> 标识是哪一类筛选，点叉时按它清除对应条件。
    /// </summary>
    public readonly record struct RecordingFilterBadge(string Key, string Text);

    /// <summary>
    /// 回放筛选条件的状态与摘要。
    /// 日期、来源设备、发货退货都收进这里，界面只负责显示徽章与呼出面板，
    /// 判断"哪些筛选生效了"的规则集中在这里，方便单独回归。
    /// </summary>
    public sealed class RecordingFilterState
    {
        public const string DateKey = "date";
        public const string SourceKey = "source";
        public const string ModeKey = "mode";

        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }

        /// <summary>来源设备的显示名；空表示全部设备。</summary>
        public string SourceName { get; set; } = "";

        /// <summary>来源设备标识，随 <see cref="SourceName"/> 一起设置。</summary>
        public string SourceId { get; set; } = "";

        /// <summary>发货/退货筛选，空表示不限。</summary>
        public string Mode { get; set; } = "";

        public bool HasDateFilter => StartDate.HasValue || EndDate.HasValue;
        public bool HasSourceFilter => !string.IsNullOrWhiteSpace(SourceName);
        public bool HasModeFilter => RecordingModeFilter.IsActive(Mode);
        public bool HasAnyFilter => HasDateFilter || HasSourceFilter || HasModeFilter;

        /// <summary>生效筛选的个数，用于按钮上的角标。</summary>
        public int ActiveCount =>
            (HasDateFilter ? 1 : 0) + (HasSourceFilter ? 1 : 0) + (HasModeFilter ? 1 : 0);

        /// <summary>
        /// 生成要显示的胶囊徽章。顺序固定为日期、来源、类型，
        /// 与筛选面板里的排列一致，避免两处对不上。
        /// </summary>
        public IReadOnlyList<RecordingFilterBadge> BuildBadges()
        {
            var badges = new List<RecordingFilterBadge>();

            if (HasDateFilter)
                badges.Add(new RecordingFilterBadge(DateKey, FormatDateRange(StartDate, EndDate)));

            if (HasSourceFilter)
                badges.Add(new RecordingFilterBadge(SourceKey, SourceName.Trim()));

            if (HasModeFilter)
                badges.Add(new RecordingFilterBadge(ModeKey, RecordingModeFilter.ToDisplayText(Mode)));

            return badges;
        }

        /// <summary>
        /// 日期徽章文案。只选了一头时说明是"某日之后/之前"，
        /// 直接写成区间会让人以为另一头也被限制了。
        /// </summary>
        internal static string FormatDateRange(DateTime? start, DateTime? end)
        {
            if (start.HasValue && end.HasValue)
            {
                return start.Value.Date == end.Value.Date
                    ? start.Value.ToString("yyyy-MM-dd")
                    : $"{start.Value:yyyy-MM-dd} 至 {end.Value:yyyy-MM-dd}";
            }

            if (start.HasValue) return $"{start.Value:yyyy-MM-dd} 起";
            if (end.HasValue) return $"截至 {end.Value:yyyy-MM-dd}";
            return "";
        }

        /// <summary>按徽章上的叉清除单个条件。</summary>
        public void Clear(string key)
        {
            switch (key)
            {
                case DateKey:
                    StartDate = null;
                    EndDate = null;
                    break;
                case SourceKey:
                    SourceName = "";
                    SourceId = "";
                    break;
                case ModeKey:
                    Mode = "";
                    break;
            }
        }

        public void ClearAll()
        {
            Clear(DateKey);
            Clear(SourceKey);
            Clear(ModeKey);
        }

        /// <summary>
        /// 起止日期填反时交换，而不是查出空列表让人以为没有录像。
        /// </summary>
        public void NormalizeDateRange()
        {
            if (StartDate.HasValue && EndDate.HasValue && StartDate > EndDate)
                (StartDate, EndDate) = (EndDate, StartDate);
        }
    }
}
