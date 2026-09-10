using Erp.Application.Abstractions;
using Erp.Domain.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Persistence.Repositories;

public sealed class InventoryRepository(ErpDbContext db) : IInventoryRepository
{
    public async Task<InventoryBalance?> GetBalanceAsync(string itemCode, CancellationToken ct = default)
        => await db.InventoryBalances.AsNoTracking().FirstOrDefaultAsync(b => b.ItemCode == itemCode, ct);

    public async Task<IReadOnlyList<InventoryBalance>> GetBalancesAsync(
        IReadOnlyCollection<string> itemCodes, CancellationToken ct = default)
    {
        if (itemCodes.Count == 0)
        {
            return [];
        }

        return await db.InventoryBalances.AsNoTracking()
            .Where(b => itemCodes.Contains(b.ItemCode))
            .ToListAsync(ct);
    }
}
