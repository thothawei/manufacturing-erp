using Erp.Application.Abstractions;
using Erp.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Persistence.Repositories;

public sealed class ItemRepository(ErpDbContext db) : IItemRepository
{
    public async Task<Item?> GetByCodeAsync(string itemCode, CancellationToken ct = default)
        => await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.ItemCode == itemCode, ct);

    public async Task<IReadOnlyList<Item>> SearchByKeywordAsync(string keyword, CancellationToken ct = default)
    {
        var pattern = $"%{EscapeLike(keyword)}%";

        return await db.Items.AsNoTracking()
            .Where(i => EF.Functions.Like(i.ItemCode, pattern, LikeEscapeChar)
                     || EF.Functions.Like(i.ItemName, pattern, LikeEscapeChar))
            .OrderBy(i => i.ItemCode)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Item>> GetByCodesAsync(
        IReadOnlyCollection<string> itemCodes, CancellationToken ct = default)
    {
        if (itemCodes.Count == 0)
        {
            return [];
        }

        return await db.Items.AsNoTracking()
            .Where(i => itemCodes.Contains(i.ItemCode))
            .ToListAsync(ct);
    }

    public async Task<ItemSupplyInfo?> GetSupplyInfoAsync(string itemCode, CancellationToken ct = default)
        => await db.ItemSupplyInfos.AsNoTracking().FirstOrDefaultAsync(s => s.ItemCode == itemCode, ct);

    public async Task<IReadOnlyList<ItemSupplyInfo>> GetSupplyInfosAsync(
        IReadOnlyCollection<string> itemCodes, CancellationToken ct = default)
    {
        if (itemCodes.Count == 0)
        {
            return [];
        }

        return await db.ItemSupplyInfos.AsNoTracking()
            .Where(s => itemCodes.Contains(s.ItemCode))
            .ToListAsync(ct);
    }

    private const string LikeEscapeChar = "\\";

    /// 使用者輸入的關鍵字若含 % 或 _，不跳脫就會變成萬用字元，
    /// 例如搜尋 "%" 會撈出全部料件
    private static string EscapeLike(string keyword) => keyword
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");
}
