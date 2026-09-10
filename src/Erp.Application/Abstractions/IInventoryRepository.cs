using Erp.Domain.Inventory;

namespace Erp.Application.Abstractions;

public interface IInventoryRepository
{
    Task<InventoryBalance?> GetBalanceAsync(string itemCode, CancellationToken ct = default);

    Task<IReadOnlyList<InventoryBalance>> GetBalancesAsync(IReadOnlyCollection<string> itemCodes, CancellationToken ct = default);
}
