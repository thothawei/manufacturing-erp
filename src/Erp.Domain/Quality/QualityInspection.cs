namespace Erp.Domain.Quality;

/// 品管檢驗紀錄（一次檢驗一筆）
public sealed class QualityInspection
{
    public required string InspectionNo { get; init; }
    public required string WorkOrderNo { get; init; }
    public required string ItemCode { get; init; }
    public required DateOnly InspectedAt { get; init; }
    public required decimal InspectedQty { get; init; }
    public required decimal PassedQty { get; init; }

    public decimal FailedQty => InspectedQty - PassedQty;

    /// 不合格原因；全數合格時為 null
    public string? FailReason { get; init; }
}
