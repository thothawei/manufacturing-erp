using Erp.Application.Abstractions;
using Erp.Application.Common;
using Erp.Domain.Production;

namespace Erp.Application.Production;

public sealed record RoutingStepProgress(
    int StepNo,
    string OperationName,
    decimal PlannedQty,
    decimal CompletedQty,
    RoutingStepStatus Status);

public sealed record WorkOrderProgress(
    string WorkOrderNo,
    string ItemCode,
    decimal PlannedQty,
    WorkOrderStatus Status,
    IReadOnlyList<RoutingStepProgress> RoutingSteps,
    string MaterialIssueStatus);

public sealed class WorkOrderProgressService(IWorkOrderRepository workOrderRepository)
{
    public async Task<WorkOrderProgress> GetProgressAsync(string workOrderNo, CancellationToken ct = default)
    {
        var wo = await workOrderRepository.GetByNoAsync(workOrderNo, ct)
            ?? throw new EntityNotFoundException("工單", workOrderNo);

        var steps = await workOrderRepository.GetRoutingStepsAsync(wo.WorkOrderNo, ct);

        return new WorkOrderProgress(
            wo.WorkOrderNo,
            wo.ItemCode,
            wo.PlannedQty,
            wo.Status,
            [.. steps
                .OrderBy(s => s.StepNo)
                .Select(s => new RoutingStepProgress(
                    s.StepNo, s.OperationName, s.PlannedQty, s.CompletedQty, s.Status))],
            wo.MaterialIssueStatus);
    }
}
