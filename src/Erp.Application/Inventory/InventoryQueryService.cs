using Erp.Application.Abstractions;
using Erp.Application.Common;

namespace Erp.Application.Inventory;

public sealed record ItemStockStatus(
    string ItemCode,
    string ItemName,
    string Unit,
    decimal OnHandQty,
    decimal ReservedQty,
    decimal AvailableQty,
    DateOnly AsOf);

public sealed class InventoryQueryService(
    IItemRepository itemRepository,
    IInventoryRepository inventoryRepository,
    IClock clock)
{
    public async Task<ItemStockStatus> GetStockAsync(string itemCode, CancellationToken ct = default)
    {
        var item = await itemRepository.GetByCodeAsync(itemCode, ct)
            ?? throw new EntityNotFoundException("料件", itemCode);

        var balance = await inventoryRepository.GetBalanceAsync(item.ItemCode, ct);

        // 沒有庫存紀錄代表從未入庫，數量以 0 呈現而非查無資料
        return new ItemStockStatus(
            item.ItemCode,
            item.ItemName,
            item.Unit,
            balance?.OnHandQty ?? 0m,
            balance?.ReservedQty ?? 0m,
            balance?.AvailableQty ?? 0m,
            clock.Today);
    }
}
