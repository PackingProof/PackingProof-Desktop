namespace ExpressPackingMonitoring.Services;

/// <summary>订单附加信息（从快递助手页面推送）</summary>
public class OrderInfo
{
    public string TrackingNumber { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string BuyerMessage { get; set; } = "";
    public string SellerMemo { get; set; } = "";
    public string ProductInfo { get; set; } = "";
    /// <summary>该订单中所有商品的总件数。</summary>
    public int TotalItemCount { get; set; }
    /// <summary>同一快递单号聚合后的订单数量。</summary>
    public int MergedOrderCount { get; set; }
    /// <summary>第三方来源标识，仅用于审计和兼容处理。</summary>
    public string ProviderId { get; set; } = "";
    public bool HasRefund { get; set; }
    public bool IsPrintedRefund { get; set; }
    public string RefundStatus { get; set; } = "";
    public string RefundProductInfo { get; set; } = "";
    public DateTime PushTime { get; set; } = DateTime.Now;
    public bool IsTest { get; set; }
}
