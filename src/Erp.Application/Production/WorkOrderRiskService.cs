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
    public async Task<IReadOnlyList<WorkOrderRisk>> GetAtRiskWorkOrdersAsync(
        DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
    {
        var (rangeStart, rangeEnd) = ResolveRange(from, to);

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

        // 缺料件中補料最慢的那個決定能否趕上交期
        var maxLeadTime = 0;
        foreach (var shortage in sufficiency.ShortageComponents)
        {
            var supply = await itemRepository.GetSupplyInfoAsync(shortage.ComponentCode, ct);
            maxLeadTime = Math.Max(maxLeadTime, supply?.LeadTimeDays ?? 0);
        }

        var daysUntilDue = dueDate.DayNumber - clock.Today.DayNumber;
        return Math.Max(0, maxLeadTime - daysUntilDue);
    }

    /// 未指定區間時預設為「本週」（週一到週日）
    private (DateOnly Start, DateOnly End) ResolveRange(DateOnly? from, DateOnly? to)
    {
        if (from.HasValue && to.HasValue)
        {
            if (to.Value < from.Value)
            {
                throw new ArgumentException("結束日期不可早於開始日期", nameof(to));
            }
            return (from.Value, to.Value);
        }

        var today = clock.Today;
        var daysFromMonday = ((int)today.DayOfWeek + 6) % 7;
        var monday = today.AddDays(-daysFromMonday);
        return (from ?? monday, to ?? monday.AddDays(6));
    }

    private static string Format(decimal qty) => qty == decimal.Truncate(qty)
        ? decimal.Truncate(qty).ToString()
        : qty.ToString("0.##");
}
