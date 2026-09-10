using Erp.Application.Abstractions;
using Erp.Application.Bom;
using Erp.Application.Common;
using Erp.Application.Production;

namespace Erp.Application.Mrp;

public sealed record ShortageItem(
    string ItemCode,
    string ItemName,
    decimal GrossRequirementQty,
    decimal AvailableQty,
    decimal InTransitQty,
    decimal NetShortageQty,
    DateOnly NeededByDate,
    decimal SuggestedOrderQty,
    string? SupplierCode,
    int LeadTimeDays);

public sealed record ShortageAnalysisResult(
    string Basis,
    DateOnly HorizonStart,
    DateOnly HorizonEnd,
    IReadOnlyList<ShortageItem> ShortageItems);

/// MRP 缺料試算：把規劃期間內所有未結案工單的剩餘產量展開成原料需求，
/// 扣掉可用庫存與能及時到貨的在途採購，剩下的就是要補的量。
///
/// 假設（要說得出理由）：工單的物料需求尚未反映在 InventoryBalance.ReservedQty 上。
/// 若工單已實際發料，本算法會高估需求量——方向偏保守（寧可多買也不缺料），
/// 但正式版本應改為「只計算尚未保留的部分」。
public sealed class MrpCalculationService(
    IWorkOrderRepository workOrderRepository,
    IItemRepository itemRepository,
    IInventoryRepository inventoryRepository,
    IPurchaseOrderRepository purchaseOrderRepository,
    BomExplosionService bomExplosionService,
    IClock clock)
{
    private const int DefaultHorizonDays = 30;

    public async Task<ShortageAnalysisResult> RunShortageAnalysisAsync(
        int? planningHorizonDays = null, string? itemCode = null, CancellationToken ct = default)
    {
        var horizonDays = planningHorizonDays ?? DefaultHorizonDays;
        if (horizonDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(planningHorizonDays), "規劃期間必須大於 0 天");
        }

        var start = clock.Today;
        var end = start.AddDays(horizonDays);

        // 查詢起點刻意用 MinValue 而不是今天：已逾交期但尚未結案的工單仍然要料，
        // 而且是最急的需求。以今天為起點會把它們整批漏掉，導致 MRP 少買。
        var workOrders = await workOrderRepository.GetOpenWorkOrdersByDueDateAsync(DateOnly.MinValue, end, ct);

        // 毛需求與需求日期（取最早的交期，代表最急的那張工單）
        var grossByCode = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var neededByCode = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase);

        foreach (var wo in workOrders)
        {
            var steps = await workOrderRepository.GetRoutingStepsAsync(wo.WorkOrderNo, ct);
            var remainingQty = WorkOrderRemainingQty.Calculate(wo, steps);
            if (remainingQty <= 0)
            {
                continue;
            }

            IReadOnlyList<ExplodedComponent> leaves;
            try
            {
                leaves = await bomExplosionService.ExplodeToLeavesAsync(wo.ItemCode, ct);
            }
            catch (Exception ex) when (ex is EntityNotFoundException or InvalidOperationException)
            {
                continue; // BOM 缺漏或異常的工單不納入試算，避免整份報表跟著炸掉
            }

            foreach (var leaf in leaves)
            {
                grossByCode[leaf.ComponentCode] =
                    grossByCode.GetValueOrDefault(leaf.ComponentCode) + leaf.RequiredPerFinishedUnit * remainingQty;

                if (!neededByCode.TryGetValue(leaf.ComponentCode, out var existing) || wo.DueDate < existing)
                {
                    neededByCode[leaf.ComponentCode] = wo.DueDate;
                }
            }
        }

        if (itemCode is not null)
        {
            var keys = grossByCode.Keys
                .Where(k => !string.Equals(k, itemCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var key in keys)
            {
                grossByCode.Remove(key);
            }
        }

        if (grossByCode.Count == 0)
        {
            return new ShortageAnalysisResult(CalculationBasis.Available, start, end, []);
        }

        var codes = grossByCode.Keys.ToList();
        var balances = await inventoryRepository.GetBalancesAsync(codes, ct);
        var availableByCode = balances.ToDictionary(b => b.ItemCode, b => b.AvailableQty, StringComparer.OrdinalIgnoreCase);

        var items = await itemRepository.GetByCodesAsync(codes, ct);
        var nameByCode = items.ToDictionary(i => i.ItemCode, i => i.ItemName, StringComparer.OrdinalIgnoreCase);

        // 採購單與補料條件都在迴圈外一次撈完，避免每個缺料料號各打一次資料庫
        var allOpenPurchaseOrders = await purchaseOrderRepository.GetOpenAsync(ct: ct);
        var openPosByItem = allOpenPurchaseOrders
            .GroupBy(p => p.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var supplyInfos = await itemRepository.GetSupplyInfosAsync(codes, ct);
        var supplyByCode = supplyInfos.ToDictionary(s => s.ItemCode, StringComparer.OrdinalIgnoreCase);

        var shortages = new List<ShortageItem>();

        foreach (var (code, gross) in grossByCode)
        {
            var neededBy = neededByCode[code];
            var available = availableByCode.GetValueOrDefault(code, 0m);

            // 只有能在需求日之前到貨的採購單才算得上供給
            var inTransit = openPosByItem.GetValueOrDefault(code, [])
                .Where(p => p.ExpectedArrivalDate <= neededBy)
                .Sum(p => p.InTransitQty);

            var net = gross - available - inTransit;
            if (net <= 0)
            {
                continue;
            }

            var supply = supplyByCode.GetValueOrDefault(code);

            shortages.Add(new ShortageItem(
                code,
                nameByCode.GetValueOrDefault(code, code),
                gross,
                available,
                inTransit,
                net,
                neededBy,
                CalculateSuggestedOrderQty(net, supply?.MinOrderQty ?? 0m, supply?.OrderMultiple ?? 0m),
                supply?.SupplierCode,
                supply?.LeadTimeDays ?? 0));
        }

        return new ShortageAnalysisResult(
            CalculationBasis.Available,
            start,
            end,
            [.. shortages.OrderBy(s => s.NeededByDate).ThenBy(s => s.ItemCode, StringComparer.OrdinalIgnoreCase)]);
    }

    /// 建議採購量：先滿足最小訂購量，再向上湊到訂購倍量
    internal static decimal CalculateSuggestedOrderQty(decimal netShortage, decimal minOrderQty, decimal orderMultiple)
    {
        var qty = Math.Max(netShortage, minOrderQty);

        if (orderMultiple > 0)
        {
            qty = decimal.Ceiling(qty / orderMultiple) * orderMultiple;
        }

        return qty;
    }
}
