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
    public string MaterialIssueStatus { get; init; } = MaterialIssueStatuses.NotIssued;

    /// 尚未結案的工單才會佔用物料與產能。
    /// 判斷依據集中在 WorkOrderStatuses.Open，Repository 查詢也用同一份。
    public bool IsOpen => WorkOrderStatuses.Open.Contains(Status);

    /// 料已經全數發到現場 —— 也就是**已經從庫存扣掉了**。
    ///
    /// 這個判斷是物料需求試算的前提：已發料工單的剩餘產量不該再算一次毛需求，
    /// 那份料已經反映在帳上庫存的減少裡了（見 MrpCalculationService）。
    public bool MaterialsFullyIssued =>
        string.Equals(MaterialIssueStatus, MaterialIssueStatuses.FullyIssued, StringComparison.Ordinal);
}

/// 領料狀態的字面值集中在這裡一份。
/// 散在各處手寫字串的話，改一個字就會讓「已發料」的判斷靜默失效。
public static class MaterialIssueStatuses
{
    public const string NotIssued = "未發料";
    public const string PartiallyIssued = "部分發料";
    public const string FullyIssued = "已全數發料";
}
