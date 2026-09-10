using Erp.Application.Abstractions;
using Erp.Domain.Quality;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Persistence.Repositories;

public sealed class QualityInspectionRepository(ErpDbContext db) : IQualityInspectionRepository
{
    public async Task<IReadOnlyList<QualityInspection>> QueryAsync(
        string? itemCode = null,
        string? workOrderNo = null,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken ct = default)
    {
        var query = db.QualityInspections.AsNoTracking();

        if (itemCode is not null)
        {
            query = query.Where(q => q.ItemCode == itemCode);
        }

        if (workOrderNo is not null)
        {
            query = query.Where(q => q.WorkOrderNo == workOrderNo);
        }

        if (from is not null)
        {
            query = query.Where(q => q.InspectedAt >= from.Value);
        }

        if (to is not null)
        {
            query = query.Where(q => q.InspectedAt <= to.Value);
        }

        return await query.OrderBy(q => q.InspectedAt).ThenBy(q => q.InspectionNo).ToListAsync(ct);
    }
}
