namespace Erp.Domain.Production;

/// 生產工單
public sealed class WorkOrder
{
    public required string WorkOrderNo { get; init; }
    public required string ItemCode { get; init; }
    public required decimal PlannedQty { get; init; }
    public required DateOnly DueDate { get; init; }
    public required WorkOrderStatus Status { get; init; }

    /// 領料狀態說明（例如「已全數發料」「部分發料」）
    public string MaterialIssueStatus { get; init; } = "未發料";

    /// 尚未結案的工單才會佔用物料與產能。
    /// 判斷依據集中在 WorkOrderStatuses.Open，Repository 查詢也用同一份。
    public bool IsOpen => WorkOrderStatuses.Open.Contains(Status);
}
