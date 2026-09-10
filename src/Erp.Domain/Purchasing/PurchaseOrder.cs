namespace Erp.Domain.Purchasing;

/// 採購單（單行單品，MVP 簡化）
public sealed class PurchaseOrder
{
    public required string PoNo { get; init; }
    public required string SupplierCode { get; init; }
    public required string ItemCode { get; init; }
    public required decimal OrderedQty { get; init; }
    public required decimal ReceivedQty { get; init; }
    public required DateOnly ExpectedArrivalDate { get; init; }
    public required PurchaseOrderStatus Status { get; init; }

    /// 尚未入庫的數量，即「在途量」
    public decimal InTransitQty => Math.Max(0m, OrderedQty - ReceivedQty);

    public bool IsOpen => PurchaseOrderStatuses.Open.Contains(Status);
}
