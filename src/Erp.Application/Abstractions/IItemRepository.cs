using Erp.Domain.Items;

namespace Erp.Application.Abstractions;

public interface IItemRepository
{
    Task<Item?> GetByCodeAsync(string itemCode, CancellationToken ct = default);

    Task<IReadOnlyList<Item>> SearchByKeywordAsync(string keyword, CancellationToken ct = default);

    Task<IReadOnlyList<Item>> GetByCodesAsync(IReadOnlyCollection<string> itemCodes, CancellationToken ct = default);

    Task<ItemSupplyInfo?> GetSupplyInfoAsync(string itemCode, CancellationToken ct = default);

    /// 批次版本。缺料件通常成群出現，逐筆查會變成 N+1。
    Task<IReadOnlyList<ItemSupplyInfo>> GetSupplyInfosAsync(
        IReadOnlyCollection<string> itemCodes, CancellationToken ct = default);
}
