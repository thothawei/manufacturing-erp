using Erp.Application.Abstractions;
using Erp.Application.Bom;
using Erp.Application.Common;

namespace Erp.Application.Production;

public sealed record WorkOrderRisk(
    string WorkOrderNo,
    string ItemCode,
    DateOnly DueDate,
    int DelayDays,
    string RiskReason);

/// 工單延遲風險判定。兩種風險來源：
/// 1. 已逾交期但尚未完工 —— 延遲天數 = 今天 - 交期
/// 2. 剩餘產量的物料不足 —— 延遲天數 = 補料前置期超出剩餘工作天的部分
/// 同時命中兩者時取延遲天數較大者，理由文字一併說明。
public sealed class WorkOrderRiskService(
    IWorkOrderRepository workOrderRepository,
    IItemRepository itemRepository,
    BomExplosionService bomExplosionService,
    IClock clock)
{
    /// 預設視窗天數。未指定區間也未指定 windowDays 時，查的是「到本週日為止」。
    public const int DefaultWindowDays = 7;

    /// 上限存在的理由：LLM 傳一個離譜的天數（例如 36500）時，
    /// 要以明確的參數錯誤現形，而不是安靜地掃一遍整個資料表。
    private const int MaxWindowDays = 365;

    public async Task<IReadOnlyList<WorkOrderRisk>> GetAtRiskWorkOrdersAsync(
        DateOnly? from = null, DateOnly? to = null, int? windowDays = null,
        CancellationToken ct = default)
    {
        var (rangeStart, rangeEnd) = ResolveRange(from, to, windowDays);

        var workOrders = await workOrderRepository.GetOpenWorkOrdersByDueDateAsync(rangeStart, rangeEnd, ct);
        var risks = new List<WorkOrderRisk>();

        foreach (var wo in workOrders)
        {
            var steps = await workOrderRepository.GetRoutingStepsAsync(wo.WorkOrderNo, ct);
            var remainingQty = WorkOrderRemainingQty.Calculate(wo, steps);
            if (remainingQty <= 0)
            {
                continue; // 產量已做完，等結案而已，不算風險
            }

            var reasons = new List<string>();
            var delayDays = 0;

            var overdueDays = wo.DueDate.DayNumber < clock.Today.DayNumber
                ? clock.Today.DayNumber - wo.DueDate.DayNumber
                : 0;
            if (overdueDays > 0)
            {
                delayDays = overdueDays;
                reasons.Add($"已逾交期 {overdueDays} 天，尚有 {Format(remainingQty)} 個未完工");
            }

            var shortageDelay = await EvaluateMaterialShortageAsync(wo.ItemCode, remainingQty, wo.DueDate, reasons, ct);
            delayDays = Math.Max(delayDays, shortageDelay);

            if (reasons.Count > 0)
            {
                risks.Add(new WorkOrderRisk(
                    wo.WorkOrderNo, wo.ItemCode, wo.DueDate, delayDays, string.Join("；", reasons)));
            }
        }

        return [.. risks.OrderByDescending(r => r.DelayDays).ThenBy(r => r.DueDate)];
    }

    /// 回傳因缺料造成的延遲天數；順便把缺料理由寫進 reasons
    private async Task<int> EvaluateMaterialShortageAsync(
        string itemCode, decimal remainingQty, DateOnly dueDate, List<string> reasons, CancellationToken ct)
    {
        MaterialSufficiencyResult sufficiency;
        try
        {
            sufficiency = await bomExplosionService.CalculateMaxBuildableAsync(itemCode, remainingQty, ct);
        }
        catch (InvalidOperationException)
        {
            // 沒有 BOM 或 BOM 結構異常的工單無法判斷缺料，只保留逾期判定
            return 0;
        }

        if (sufficiency.ShortageComponents.Count == 0)
        {
            return 0;
        }

        var worst = sufficiency.ShortageComponents.MaxBy(c => c.ShortfallQty)!;
        reasons.Add($"缺料：{worst.ComponentCode} 短少 {Format(worst.ShortfallQty)} 件");

        // 缺料件中補料最慢的那個決定能否趕上交期。
        // 一次撈完所有缺料件的補料條件，不要在迴圈裡逐筆查（N+1）。
        var supplyInfos = await itemRepository.GetSupplyInfosAsync(
            [.. sufficiency.ShortageComponents.Select(c => c.ComponentCode)], ct);
        var maxLeadTime = supplyInfos.Count == 0 ? 0 : supplyInfos.Max(s => s.LeadTimeDays);

        var daysUntilDue = dueDate.DayNumber - clock.Today.DayNumber;
        return Math.Max(0, maxLeadTime - daysUntilDue);
    }

    /// 解析查詢區間。三種輸入的優先序（寫進工具說明，LLM 才不會兩種都傳）：
    ///
    /// 1. 明確給了 from／to —— 直接用，windowDays 不再參與。
    /// 2. 只給 windowDays —— 「今天起算 N 天」，讓使用者說得出「未來 14 天」「這個月」。
    /// 3. 什麼都沒給 —— 到本週日為止（DefaultWindowDays 是這個預設的天數來源）。
    ///
    /// 起點未指定時刻意用 MinValue 而不是本週一：已逾交期但尚未結案的工單是最該被看到的
    /// 那一種風險，以本週一為起點會把上週就逾期的工單整批濾掉 ——
    /// 使用者問「這週有哪些工單有延遲風險」，想知道的顯然包含它們。
    /// MRP 那側（MrpCalculationService）本來就是這樣處理的，這裡跟它對齊。
    private (DateOnly Start, DateOnly End) ResolveRange(DateOnly? from, DateOnly? to, int? windowDays)
    {
        if (windowDays is <= 0 or > MaxWindowDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowDays), windowDays, $"視窗天數必須介於 1 到 {MaxWindowDays} 天");
        }

        if (from.HasValue && to.HasValue)
        {
            if (to.Value < from.Value)
            {
                throw new ArgumentException("結束日期不可早於開始日期", nameof(to));
            }
            return (from.Value, to.Value);
        }

        var today = clock.Today;

        // 今天算第一天，所以 N 天的視窗是 today ~ today+(N-1)
        var end = to ?? (windowDays.HasValue
            ? today.AddDays(windowDays.Value - 1)
            : EndOfThisWeek(today));

        var start = from ?? DateOnly.MinValue;

        if (end < start)
        {
            throw new ArgumentException("結束日期不可早於開始日期", nameof(to));
        }

        return (start, end);
    }

    private static DateOnly EndOfThisWeek(DateOnly today)
    {
        var daysFromMonday = ((int)today.DayOfWeek + 6) % 7;
        return today.AddDays(-daysFromMonday).AddDays(DefaultWindowDays - 1);
    }

    private static string Format(decimal qty) => qty == decimal.Truncate(qty)
        ? decimal.Truncate(qty).ToString()
        : qty.ToString("0.##");
}
