namespace Erp.Domain.Production;

public enum WorkOrderStatus
{
    Planned,      // 已建立未開工
    Released,     // 已發放
    InProgress,   // 生產中
    Completed,    // 已完工
    Closed,       // 已結案
    Cancelled     // 已取消
}
