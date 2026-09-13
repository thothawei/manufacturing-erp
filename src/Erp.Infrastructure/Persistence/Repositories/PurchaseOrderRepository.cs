using Erp.Application.Abstractions;
using Erp.Domain.Purchasing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Persistence.Repositories;

public sealed class PurchaseOrderRepository(ErpDbContext db) : IPurchaseOrderRepository
{
    public async Task<IReadOnlyList<PurchaseOrder>> GetOpenAsync(
        string? supplierCode = null, string? itemCode = null, CancellationToken ct = default)
    {
        // 同樣不能用 p.IsOpen，理由見 WorkOrderRepository
        var openStatuses = PurchaseOrderStatuses.Open;

        var query = db.PurchaseOrders.AsNoTracking()
            .Where(p => openStatuses.Contains(p.Status));

        if (supplierCode is not null)
        {
            query = query.Where(p => p.SupplierCode == supplierCode);
        }

        if (itemCode is not null)
        {
            query = query.Where(p => p.ItemCode == itemCode);
        }

        return await query.OrderBy(p => p.ExpectedArrivalDate).ThenBy(p => p.PoNo).ToListAsync(ct);
    }

    public async Task AddAsync(PurchaseOrder purchaseOrder, CancellationToken ct = default)
        => await db.PurchaseOrders.AddAsync(purchaseOrder, ct);

    public Task<int> CountByPoNoPrefixAsync(string prefix, CancellationToken ct = default)
        => db.PurchaseOrders.AsNoTracking().CountAsync(p => p.PoNo.StartsWith(prefix), ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
