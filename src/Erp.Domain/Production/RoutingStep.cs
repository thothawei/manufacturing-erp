namespace Erp.Domain.Production;

/// 工單的途程站別與其完工數量
public sealed class RoutingStep
{
    public required string WorkOrderNo { get; init; }
    public required int StepNo { get; init; }
    public required string OperationName { get; init; }
    public required decimal PlannedQty { get; init; }
    public required decimal CompletedQty { get; init; }
    public required RoutingStepStatus Status { get; init; }
}
