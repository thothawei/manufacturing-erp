using Erp.Domain.Items;

namespace Erp.Application.Abstractions;

public interface IItemRepository
{
    Task<Item?> GetByCodeAsync(string itemCode, CancellationToken ct = default);

    Task<IReadOnlyList<Item>> SearchByKeywordAsync(string keyword, CancellationToken ct = default);

    Task<IReadOnlyList<Item>> GetByCodesAsync(IReadOnlyCollection<string> itemCodes, CancellationToken ct = default);

    Task<ItemSupplyInfo?> GetSupplyInfoAsync(string itemCode, CancellationToken ct = default);
}
