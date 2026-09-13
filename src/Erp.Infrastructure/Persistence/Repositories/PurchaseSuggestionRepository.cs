using Erp.Application.Abstractions;
using Erp.Domain.Purchasing;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Persistence.Repositories;

public sealed class PurchaseSuggestionRepository(ErpDbContext db) : IPurchaseSuggestionRepository
{
    /// 這裡刻意**不加** AsNoTracking：核准與駁回要改這個實體的狀態，
    /// 沒有追蹤的話 SaveChanges 什麼都不會寫，而且不會有任何錯誤
    public Task<PurchaseSuggestion?> GetByNoAsync(string suggestionNo, CancellationToken ct = default)
        => db.PurchaseSuggestions.FirstOrDefaultAsync(s => s.SuggestionNo == suggestionNo, ct);

    public async Task<IReadOnlyList<PurchaseSuggestion>> ListAsync(
        PurchaseSuggestionStatus? status = null, CancellationToken ct = default)
    {
        var query = db.PurchaseSuggestions.AsNoTracking();

        if (status.HasValue)
        {
            query = query.Where(s => s.Status == status.Value);
        }

        return await query
            .OrderByDescending(s => s.CreatedOn).ThenBy(s => s.SuggestionNo)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetPendingItemCodesAsync(
        IReadOnlyList<string> itemCodes, CancellationToken ct = default)
        => await db.PurchaseSuggestions.AsNoTracking()
            .Where(s => s.Status == PurchaseSuggestionStatus.PendingApproval && itemCodes.Contains(s.ItemCode))
            .Select(s => s.ItemCode)
            .Distinct()
            .ToListAsync(ct);

    public async Task AddRangeAsync(
        IReadOnlyList<PurchaseSuggestion> suggestions, CancellationToken ct = default)
        => await db.PurchaseSuggestions.AddRangeAsync(suggestions, ct);

    public Task<int> CountBySuggestionNoPrefixAsync(string prefix, CancellationToken ct = default)
        => db.PurchaseSuggestions.AsNoTracking()
            .CountAsync(s => s.SuggestionNo.StartsWith(prefix), ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
