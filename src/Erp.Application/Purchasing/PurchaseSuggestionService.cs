using Erp.Application.Abstractions;
using Erp.Application.Common;
using Erp.Application.Mrp;
using Erp.Domain.Purchasing;

namespace Erp.Application.Purchasing;

public sealed record PurchaseSuggestionView(
    string SuggestionNo,
    string ItemCode,
    decimal SuggestedQty,
    string? SupplierCode,
    DateOnly NeededByDate,
    string Reason,
    DateOnly CreatedOn,
    string Status,
    string? DecidedBy,
    DateOnly? DecidedOn,
    string? CreatedPoNo);

/// AI 產生採購建議的結果。
///
/// Note 會被原樣送回給 LLM：它必須知道自己做的是「提出建議」而不是「下單」，
/// 否則它會照著工具名稱的字面意思告訴使用者「已經幫你下單了」。
public sealed record PurchaseSuggestionResult(
    IReadOnlyList<PurchaseSuggestionView> Created,
    IReadOnlyList<string> SkippedItemCodes,
    string Note);

/// 採購建議：AI 寫入建議，人工核准才成立採購單。
///
/// 這個服務刻意把兩件事分在兩個方法、並由兩條完全不同的路徑呼叫：
/// `SuggestFromShortagesAsync` 是 AI 工具唯一通得到的入口，它只會寫出
/// PendingApproval；`ApproveAsync` 只有人工確認端點呼叫得到，
/// 而它是整個系統唯一會新增正式採購單的地方。
public sealed class PurchaseSuggestionService(
    MrpCalculationService mrpCalculationService,
    IPurchaseSuggestionRepository suggestionRepository,
    IPurchaseOrderRepository purchaseOrderRepository,
    IItemRepository itemRepository,
    IClock clock)
{
    /// 依 MRP 短缺分析產生建議。已經有待審建議的料號會被跳過 ——
    /// 同一個缺料情境問兩次不該產生兩筆一樣的建議，而 LLM 重複呼叫同一個工具
    /// 是很常見的事（它看不到上一次呼叫的副作用）。
    public async Task<PurchaseSuggestionResult> SuggestFromShortagesAsync(
        string? itemCode = null, CancellationToken ct = default)
    {
        var analysis = await mrpCalculationService.RunShortageAnalysisAsync(itemCode: itemCode, ct: ct);

        if (analysis.ShortageItems.Count == 0)
        {
            return new PurchaseSuggestionResult([], [], "目前沒有缺料，未產生任何採購建議。");
        }

        var codes = analysis.ShortageItems.Select(s => s.ItemCode).ToList();
        var alreadyPending = (await suggestionRepository.GetPendingItemCodesAsync(codes, ct)).ToHashSet(
            StringComparer.OrdinalIgnoreCase);

        var today = clock.Today;
        var prefix = $"PS-{today:yyyyMMdd}-";
        var sequence = await suggestionRepository.CountBySuggestionNoPrefixAsync(prefix, ct);

        var created = new List<PurchaseSuggestion>();

        foreach (var shortage in analysis.ShortageItems.Where(s => !alreadyPending.Contains(s.ItemCode)))
        {
            sequence++;
            created.Add(new PurchaseSuggestion
            {
                SuggestionNo = $"{prefix}{sequence:D3}",
                ItemCode = shortage.ItemCode,
                SuggestedQty = shortage.SuggestedOrderQty,
                SupplierCode = shortage.SupplierCode,
                NeededByDate = shortage.NeededByDate,
                Reason =
                    $"MRP 試算：毛需求 {Format(shortage.GrossRequirementQty)}、"
                    + $"可用庫存 {Format(shortage.AvailableQty)}、"
                    + $"及時到貨在途 {Format(shortage.InTransitQty)}，"
                    + $"淨缺 {Format(shortage.NetShortageQty)}；"
                    + $"套用最小訂購量與訂購倍量後建議 {Format(shortage.SuggestedOrderQty)}。",
                CreatedOn = today
            });
        }

        if (created.Count > 0)
        {
            await suggestionRepository.AddRangeAsync(created, ct);
            await suggestionRepository.SaveChangesAsync(ct);
        }

        var skipped = codes.Where(alreadyPending.Contains).ToList();

        return new PurchaseSuggestionResult(
            [.. created.Select(ToView)],
            skipped,
            BuildNote(created.Count, skipped));
    }

    public async Task<IReadOnlyList<PurchaseSuggestionView>> ListAsync(
        PurchaseSuggestionStatus? status = null, CancellationToken ct = default)
        => [.. (await suggestionRepository.ListAsync(status, ct)).Select(ToView)];

    /// 核准：建立正式採購單。**只有人工確認端點呼叫得到這裡。**
    public async Task<PurchaseSuggestionView> ApproveAsync(
        string suggestionNo, string decidedBy, CancellationToken ct = default)
    {
        var suggestion = await RequirePendingAsync(suggestionNo, decidedBy, ct);

        var supply = (await itemRepository.GetSupplyInfosAsync([suggestion.ItemCode], ct))
            .FirstOrDefault();

        var today = clock.Today;
        var prefix = $"PO-{today:yyyyMMdd}-";
        var sequence = await purchaseOrderRepository.CountByPoNoPrefixAsync(prefix, ct) + 1;

        var poNo = $"{prefix}{sequence:D3}";

        await purchaseOrderRepository.AddAsync(new PurchaseOrder
        {
            PoNo = poNo,
            SupplierCode = suggestion.SupplierCode ?? "UNKNOWN",
            ItemCode = suggestion.ItemCode,
            OrderedQty = suggestion.SuggestedQty,
            ReceivedQty = 0m,
            // 交期以「今天 + 採購前置期」估算，不用建議裡的需求日 ——
            // 需求日是「什麼時候要用到」，不是「什麼時候會到貨」，兩者混用會讓
            // MRP 下一輪把一張根本來不及的採購單算成及時供給
            ExpectedArrivalDate = today.AddDays(supply?.LeadTimeDays ?? 0),
            Status = PurchaseOrderStatus.Open
        }, ct);

        suggestion.Status = PurchaseSuggestionStatus.Approved;
        suggestion.DecidedBy = decidedBy;
        suggestion.DecidedOn = today;
        suggestion.CreatedPoNo = poNo;

        await purchaseOrderRepository.SaveChangesAsync(ct);
        await suggestionRepository.SaveChangesAsync(ct);

        return ToView(suggestion);
    }

    public async Task<PurchaseSuggestionView> RejectAsync(
        string suggestionNo, string decidedBy, CancellationToken ct = default)
    {
        var suggestion = await RequirePendingAsync(suggestionNo, decidedBy, ct);

        suggestion.Status = PurchaseSuggestionStatus.Rejected;
        suggestion.DecidedBy = decidedBy;
        suggestion.DecidedOn = clock.Today;

        await suggestionRepository.SaveChangesAsync(ct);

        return ToView(suggestion);
    }

    private async Task<PurchaseSuggestion> RequirePendingAsync(
        string suggestionNo, string decidedBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(decidedBy))
        {
            throw new ArgumentException("必須指明是誰做的決定", nameof(decidedBy));
        }

        var suggestion = await suggestionRepository.GetByNoAsync(suggestionNo, ct)
            ?? throw new EntityNotFoundException("採購建議", suggestionNo);

        if (!suggestion.IsPending)
        {
            // 重複核准會變成兩張採購單。狀態不對就擋下來，不是「再做一次也沒差」
            throw new InvalidOperationException(
                $"採購建議 {suggestionNo} 已經是 {suggestion.Status} 狀態，不能重複處理");
        }

        return suggestion;
    }

    private static string BuildNote(int createdCount, IReadOnlyList<string> skipped)
    {
        var note = createdCount > 0
            ? $"已產生 {createdCount} 筆採購建議，狀態為待人工確認。"
            : "沒有新增任何採購建議。";

        if (skipped.Count > 0)
        {
            note += $"另有 {skipped.Count} 個料號（{string.Join("、", skipped)}）已經有待確認的建議，未重複產生。";
        }

        return note + "這些建議尚未成立採購單，必須由人在採購建議清單上核准後才會實際下單。";
    }

    private static PurchaseSuggestionView ToView(PurchaseSuggestion s) => new(
        s.SuggestionNo, s.ItemCode, s.SuggestedQty, s.SupplierCode, s.NeededByDate,
        s.Reason, s.CreatedOn, s.Status.ToString(), s.DecidedBy, s.DecidedOn, s.CreatedPoNo);

    private static string Format(decimal qty) => qty == decimal.Truncate(qty)
        ? decimal.Truncate(qty).ToString()
        : qty.ToString("0.##");
}
