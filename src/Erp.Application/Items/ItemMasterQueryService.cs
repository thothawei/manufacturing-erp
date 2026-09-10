using Erp.Application.Abstractions;
using Erp.Domain.Items;

namespace Erp.Application.Items;

public sealed record ItemSearchResult(string ItemCode, string ItemName, ItemType ItemType);

public sealed class ItemMasterQueryService(IItemRepository itemRepository)
{
    public async Task<IReadOnlyList<ItemSearchResult>> SearchByKeywordAsync(
        string keyword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return [];
        }

        var items = await itemRepository.SearchByKeywordAsync(keyword.Trim(), ct);
        return [.. items.Select(i => new ItemSearchResult(i.ItemCode, i.ItemName, i.ItemType))];
    }
}
