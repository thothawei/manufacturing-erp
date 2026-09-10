namespace Erp.Domain.Items;

/// 採購補料條件：MRP 產生建議採購量時使用
public sealed class ItemSupplyInfo
{
    public required string ItemCode { get; init; }
    public required string SupplierCode { get; init; }
    public required int LeadTimeDays { get; init; }

    /// 最小訂購量；沒有下限時為 0
    public decimal MinOrderQty { get; init; }

    /// 訂購倍量（例如一箱 50 件）；沒有倍量限制時為 0
    public decimal OrderMultiple { get; init; }
}
