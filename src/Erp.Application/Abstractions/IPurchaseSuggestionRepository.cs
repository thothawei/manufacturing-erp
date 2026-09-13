using Erp.Domain.Purchasing;

namespace Erp.Application.Abstractions;

public interface IPurchaseSuggestionRepository
{
    Task<PurchaseSuggestion?> GetByNoAsync(string suggestionNo, CancellationToken ct = default);

    Task<IReadOnlyList<PurchaseSuggestion>> ListAsync(
        PurchaseSuggestionStatus? status = null, CancellationToken ct = default);

    /// 這些料號目前有沒有還沒被處理掉的建議。
    /// 用來擋重複建議 —— 同一個缺料情境問兩次，不該產生兩筆一樣的建議。
    Task<IReadOnlyList<string>> GetPendingItemCodesAsync(
        IReadOnlyList<string> itemCodes, CancellationToken ct = default);

    Task AddRangeAsync(IReadOnlyList<PurchaseSuggestion> suggestions, CancellationToken ct = default);

    /// 單號前綴相符的建議有幾筆，用來編當天的流水號
    Task<int> CountBySuggestionNoPrefixAsync(string prefix, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
