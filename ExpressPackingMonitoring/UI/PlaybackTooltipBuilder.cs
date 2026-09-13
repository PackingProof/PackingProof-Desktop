using System.Text;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 回放列表的悬浮提示文案。
    /// 按"这是哪一单 → 订单附带信息 → 这段录像本身 → 异常与位置"分组，
    /// 每组之间空一行；组内字段固定顺序，店员每次都在同一位置找同一项。
    /// </summary>
    internal static class PlaybackTooltipBuilder
    {
        public static string Build(VideoItem item)
        {
            if (item == null) return "";

            var sections = new List<List<string>>();

            // 1. 身份：先回答"这是哪一单"。
            var identity = new List<string>();
            AddTo(identity, "发退货", NormalizeModeText(item.Mode));
            AddTo(identity, "快递单号", item.TrackingNumber);
            AddTo(identity, "订单号", item.OrderId);
            AddTo(identity, "原始订单号", item.SourceOrderId);
            sections.Add(identity);

            // 2. 订单附带信息：打包时需要核对的备注类内容。
            var orderDetails = new List<string>();
            AddTo(orderDetails, "商品信息", item.ProductInfo);
            AddTo(orderDetails, "买家留言", item.BuyerMessage);
            AddTo(orderDetails, "卖家备注", item.SellerMemo);
            AddTo(orderDetails, "订单信息推送时间", item.OrderInfoPushTime);
            sections.Add(orderDetails);

            // 3. 录像本身的属性。
            var recording = new List<string>();
            if (item.StartTime != default)
                AddTo(recording, "录制时间", item.StartTime.ToString("yyyy-MM-dd HH:mm:ss"));
            AddTo(recording, "时长", item.Duration);
            AddTo(recording, "大小", item.FileSize);
            AddTo(recording, "编码", item.EncoderDisplay);
            AddTo(recording, "来源", item.SourceDisplay);
            AddTo(recording, "结束原因", item.StopReason);
            sections.Add(recording);

            // 4. 异常与位置：放最后，正常录像这一组只剩文件位置一行。
            var status = new List<string>();
            if (item.IsDeleted)
            {
                AddTo(status, "状态", string.IsNullOrWhiteSpace(item.DeleteReason)
                    ? "已清理"
                    : $"已清理（{item.DeleteReason}）");
            }
            else if (item.IsStoredOnHost)
            {
                AddTo(status, "状态", "已保存到主机");
            }
            else if (item.IsMissing)
            {
                AddTo(status, "状态", "文件已丢失，未在记录的位置找到录像");
            }
            else if (item.IsArchiveWarning)
            {
                AddTo(status, "状态", item.ArchiveStatusText);
            }
            AddTo(status, "文件位置", string.IsNullOrWhiteSpace(item.FullPath) ? item.FileName : item.FullPath);
            sections.Add(status);

            var builder = new StringBuilder();
            foreach (List<string> section in sections)
            {
                if (section.Count == 0) continue;
                if (builder.Length > 0) builder.AppendLine();
                foreach (string line in section)
                    builder.AppendLine(line);
            }
            return builder.ToString().TrimEnd();
        }

        private static void AddTo(List<string> target, string label, string? value)
        {
            string normalized = value?.Trim() ?? "";
            if (normalized.Length > 0)
                target.Add($"{label}：{normalized}");
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
