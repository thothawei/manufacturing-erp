using Erp.Application.Abstractions;
using Erp.Domain.Production;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Persistence.Repositories;

public sealed class WorkOrderRepository(ErpDbContext db) : IWorkOrderRepository
{
    public async Task<WorkOrder?> GetByNoAsync(string workOrderNo, CancellationToken ct = default)
        => await db.WorkOrders.AsNoTracking().FirstOrDefaultAsync(w => w.WorkOrderNo == workOrderNo, ct);

    public async Task<IReadOnlyList<WorkOrder>> GetOpenWorkOrdersByDueDateAsync(
        DateOnly dueFrom, DateOnly dueTo, CancellationToken ct = default)
    {
        // 這裡不能寫 w.IsOpen —— 那是 C# 計算屬性，EF Core 無法翻成 SQL。
        // 改用 WorkOrderStatuses.Open（可翻成 SQL 的 IN），與 IsOpen 共用同一份定義。
        var openStatuses = WorkOrderStatuses.Open;

        return await db.WorkOrders.AsNoTracking()
            .Where(w => openStatuses.Contains(w.Status) && w.DueDate >= dueFrom && w.DueDate <= dueTo)
            .OrderBy(w => w.DueDate)
            .ThenBy(w => w.WorkOrderNo)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RoutingStep>> GetRoutingStepsAsync(
        string workOrderNo, CancellationToken ct = default)
        => await db.RoutingSteps.AsNoTracking()
            .Where(s => s.WorkOrderNo == workOrderNo)
            .OrderBy(s => s.StepNo)
            .ToListAsync(ct);
}
