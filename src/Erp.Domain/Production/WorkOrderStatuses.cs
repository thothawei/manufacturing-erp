namespace Erp.Domain.Production;

/// 「未結案」的定義只寫在這裡一份。
/// WorkOrder.IsOpen 是 C# 計算屬性、EF Core 無法翻成 SQL，
/// Repository 的查詢條件必須改用這個集合（可翻譯成 SQL 的 IN），
/// 兩邊共用同一份定義才不會出現「記憶體算的」與「資料庫查的」不一致。
public static class WorkOrderStatuses
{
    public static readonly WorkOrderStatus[] Open =
    [
        WorkOrderStatus.Planned,
        WorkOrderStatus.Released,
        WorkOrderStatus.InProgress
    ];
}
