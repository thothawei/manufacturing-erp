using Erp.Domain.Production;

namespace Erp.Application.Abstractions;

public interface IWorkOrderRepository
{
    Task<WorkOrder?> GetByNoAsync(string workOrderNo, CancellationToken ct = default);

    /// 取得交期落在區間內、且尚未結案的工單
    Task<IReadOnlyList<WorkOrder>> GetOpenWorkOrdersByDueDateAsync(
        DateOnly dueFrom, DateOnly dueTo, CancellationToken ct = default);

    Task<IReadOnlyList<RoutingStep>> GetRoutingStepsAsync(string workOrderNo, CancellationToken ct = default);
}
