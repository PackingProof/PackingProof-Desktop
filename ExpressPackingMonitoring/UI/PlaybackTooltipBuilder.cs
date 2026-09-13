using System.Text;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 回放列表的悬浮提示文案。
    /// 字段与顺序对齐 Web 端 index.html 的 buildVideoTooltip，
    /// 保证同一条录像在网页和上位机上看到的信息一致。
    /// </summary>
    internal static class PlaybackTooltipBuilder
    {
        public static string Build(VideoItem item)
        {
            if (item == null) return "";

            var lines = new List<string>();
            void Add(string label, string? value)
            {
                string normalized = value?.Trim() ?? "";
                if (normalized.Length > 0)
                    lines.Add($"{label}：{normalized}");
            }

            Add("订单号", item.OrderId);
            Add("原始订单号", item.SourceOrderId);
            Add("快递单号", item.TrackingNumber);
            Add("业务类型", NormalizeModeText(item.Mode));
            Add("录像来源", item.SourceDisplay);
            Add("买家留言", item.BuyerMessage);
            Add("卖家备注", item.SellerMemo);
            Add("商品信息", item.ProductInfo);
            Add("订单信息推送时间", item.OrderInfoPushTime);
            if (item.StartTime != default)
                Add("录制时间", item.StartTime.ToString("yyyy-MM-dd HH:mm:ss"));
            Add("时长", item.Duration);
            Add("文件大小", item.FileSize);

            // 编码只展示可读标签；Web 端那句"将转码为 H.264"是浏览器兼容播放的提示，
            // 上位机用本地播放器直出，照搬过来只会误导。
            if (!string.IsNullOrWhiteSpace(item.EncoderDisplay))
                Add("视频编码", item.EncoderDisplay);

            if (item.IsDeleted)
                Add("清理原因", string.IsNullOrWhiteSpace(item.DeleteReason) ? "已清理" : item.DeleteReason);
            else if (item.IsMissing)
                Add("丢失原因", "监控端未在记录的文件位置找到录像，可能已被移动、删除或清理");
            else if (item.IsArchiveWarning)
                Add("归档状态", item.ArchiveStatusText);

            Add("文件位置", string.IsNullOrWhiteSpace(item.FullPath) ? item.FileName : item.FullPath);

            return string.Join(Environment.NewLine, lines);
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
