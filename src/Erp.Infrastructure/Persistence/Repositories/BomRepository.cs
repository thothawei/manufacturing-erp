using Erp.Application.Abstractions;
using Erp.Domain.Bom;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Persistence.Repositories;

public sealed class BomRepository(ErpDbContext db) : IBomRepository
{
    public async Task<IReadOnlyList<BomLine>> GetLinesByParentAsync(
        string parentItemCode, CancellationToken ct = default)
        => await db.BomLines.AsNoTracking()
            .Where(l => l.ParentItemCode == parentItemCode)
            .OrderBy(l => l.ComponentItemCode)
            .ToListAsync(ct);
}
