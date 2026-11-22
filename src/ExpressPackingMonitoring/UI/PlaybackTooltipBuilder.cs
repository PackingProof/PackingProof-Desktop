using ExpressPackingMonitoring.Localization;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>悬浮提示里的一行：字段名 + 值。</summary>
    public sealed record PlaybackTooltipRow(string Label, string Value);

    /// <summary>一组字段：组内紧挨着，组之间画一条细分隔线，扫读时有段落感。</summary>
    public sealed record PlaybackTooltipGroup(IReadOnlyList<PlaybackTooltipRow> Rows);

    /// <summary>
    /// 回放列表悬浮提示的字段与顺序。
    /// 一行一个字段（字段名一列、值一列），顺序固定：这是哪一单 → 订单附带信息 →
    /// 这段录像本身 → 异常与位置；空字段整行省略，不出现"买家留言："这样的空标签。
    /// </summary>
    internal static class PlaybackTooltipBuilder
    {
        /// <summary>拍平后的字段行，给需要按顺序断言的调用方用。</summary>
        public static IReadOnlyList<PlaybackTooltipRow> BuildRows(VideoItem item) =>
            BuildGroups(item).SelectMany(group => group.Rows).ToArray();

        public static IReadOnlyList<PlaybackTooltipGroup> BuildGroups(VideoItem item)
        {
            if (item == null) return [];

            var groups = new List<PlaybackTooltipGroup>();

            // 1. 身份：先回答"这是哪一单"。
            var identity = new List<PlaybackTooltipRow>();
            AddRow(identity, Label("发退货"), AppLanguage.TranslateRuntime(NormalizeModeText(item.Mode)));
            AddRow(identity, Label("快递单号"), item.TrackingNumber);
            AddRow(identity, Label("订单号"), item.OrderId);
            AddRow(identity, Label("原始订单号"), item.SourceOrderId);
            groups.Add(new PlaybackTooltipGroup(identity));

            // 2. 订单附带信息：打包时需要核对的备注类内容。
            var orderDetails = new List<PlaybackTooltipRow>();
            AddRow(orderDetails, Label("商品信息"), item.ProductInfo);
            AddRow(orderDetails, Label("买家留言"), item.BuyerMessage);
            AddRow(orderDetails, Label("卖家备注"), item.SellerMemo);
            AddRow(orderDetails, Label("订单信息推送时间"), item.OrderInfoPushTime);
            groups.Add(new PlaybackTooltipGroup(orderDetails));

            // 3. 录像本身的属性。
            var recording = new List<PlaybackTooltipRow>();
            if (item.StartTime != default)
                AddRow(recording, Label("录制时间"), item.StartTime.ToString("yyyy-MM-dd HH:mm:ss"));
            AddRow(recording, Label("时长"), item.Duration);
            AddRow(recording, Label("大小"), item.FileSize);
            AddRow(recording, Label("编码"), AppLanguage.TranslateRuntime(item.EncoderDisplay));
            AddRow(recording, Label("来源"), AppLanguage.TranslateRuntime(item.SourceDisplay));
            AddRow(recording, Label("结束原因"), PlaybackReasonText.Translate(item.StopReason));
            groups.Add(new PlaybackTooltipGroup(recording));

            // 4. 异常与位置：放最后，正常录像这里只剩文件位置一行。
            var status = new List<PlaybackTooltipRow>();
            AddRow(status, Label("状态"), ResolveStatusText(item));
            AddRow(status, Label("文件位置"), string.IsNullOrWhiteSpace(item.FullPath) ? item.FileName : item.FullPath);
            groups.Add(new PlaybackTooltipGroup(status));

            return groups.Where(group => group.Rows.Count > 0).ToArray();
        }

        private static string ResolveStatusText(VideoItem item)
        {
            if (item.IsDeleted)
            {
                return string.IsNullOrWhiteSpace(item.DeleteReason)
                    ? Label("已清理")
                    : AppLanguage.Format(
                        "已清理（{0}）",
                        PlaybackReasonText.CleanupReason(item.DeleteReason));
            }
            if (item.IsStoredOnHost)
                return Label("已备份到主机");
            if (item.IsMissing)
                return Label("文件已丢失，未在记录的位置找到录像");
            if (item.IsArchiveWarning)
                return AppLanguage.TranslateRuntime(item.ArchiveStatusText);
            return "";
        }

        private static string Label(string key) => AppLanguage.Get(key);

        private static void AddRow(List<PlaybackTooltipRow> rows, string label, string? value)
        {
            string normalized = value?.Trim() ?? "";
            if (normalized.Length > 0)
                rows.Add(new PlaybackTooltipRow(label, normalized));
        }

        /// <summary>数据库里历史值有中英混用，展示前统一成中文。</summary>
        internal static string NormalizeModeText(string? mode)
        {
            string normalized = mode?.Trim() ?? "";
            if (normalized.Length == 0) return "发货";

            return normalized switch
            {
                "return" => "退货",
                "shipping" => "发货",
                _ => normalized
            };
        }
    }
}
