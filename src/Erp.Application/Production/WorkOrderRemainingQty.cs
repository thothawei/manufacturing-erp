using Erp.Domain.Production;

namespace Erp.Application.Production;

/// 工單剩餘待產量的統一算法：以途程最後一站的完工量為實際產出。
/// 風險判定與 MRP 都用它，避免兩邊各算各的而對不起來。
public static class WorkOrderRemainingQty
{
    public static decimal Calculate(WorkOrder workOrder, IReadOnlyList<RoutingStep> steps)
    {
        if (steps.Count == 0)
        {
            return workOrder.PlannedQty;
        }

        var lastStep = steps.MaxBy(s => s.StepNo)!;
        return Math.Max(0m, workOrder.PlannedQty - lastStep.CompletedQty);
    }
}
