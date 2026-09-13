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

/// 時間分期試算裡的一個時間桶（一週）。
/// ProjectedOnHandQty 是**期末**的預估庫存水位：上一桶的期末，加本桶預期入庫，減本桶需求。
public sealed record MaterialTimeBucket(
    int WeekIndex,
    DateOnly WeekStart,
    DateOnly WeekEnd,
    decimal ScheduledReceiptQty,
    decimal RequirementQty,
    decimal ProjectedOnHandQty);

public sealed record TimePhasedItem(
    string ItemCode,
    string ItemName,
    decimal OpeningAvailableQty,
    IReadOnlyList<MaterialTimeBucket> Buckets,
    /// 預估庫存水位第一次轉負的週次（1 起算）；整個規劃期間都不會缺料時為 null
    int? FirstShortageWeek,
    /// 該週的起始日。用「哪一天」講給人聽，比「第幾週」容易對上行事曆。
    DateOnly? FirstShortageDate);

public sealed record TimePhasedAnalysisResult(
    string Basis,
    DateOnly HorizonStart,
    DateOnly HorizonEnd,
    int WeekCount,
    IReadOnlyList<TimePhasedItem> Items);

/// 單一工單對單一料件的需求。時間分期需要「每筆需求各自的日期」，
/// 不分期的版本才把它們收斂成一個最早日期。
internal sealed record MaterialRequirement(string ComponentCode, DateOnly DueDate, decimal Qty);

/// MRP 缺料試算：把規劃期間內未結案、且**尚未全數發料**的工單剩餘產量展開成原料需求，
/// 扣掉可用庫存與能及時到貨的在途採購，剩下的就是要補的量。
///
/// 「尚未全數發料」這個條件是必要的，不是保守起見：料一旦發到現場就已經從帳上庫存扣掉了，
/// 再把同一張工單的剩餘產量算成毛需求，等於同一份需求被算兩次，缺料量會憑空變大。
///
/// InventoryBalance.ReservedQty 在這裡刻意不參與需求面的計算 ——
/// 它只透過 AvailableQty（帳上減保留）影響供給面。工單需求是在這裡從未結案工單
/// 動態展開的，不依賴任何人回頭去維護 ReservedQty 欄位。
public sealed class MrpCalculationService(
    IWorkOrderRepository workOrderRepository,
    IItemRepository itemRepository,
    IInventoryRepository inventoryRepository,
    IPurchaseOrderRepository purchaseOrderRepository,
    BomExplosionService bomExplosionService,
    IClock clock)
{
    private const int DefaultHorizonDays = 30;

    /// 八週是刻意的：夠長到能看出需求分散造成的缺口，又短到整張表一眼看得完。
    private const int DefaultTimePhasedWeeks = 8;
    private const int MaxTimePhasedWeeks = 52;

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

        var requirements = await CollectRequirementsAsync(end, ct);

        // 毛需求與需求日期（取最早的交期，代表最急的那張工單）
        var grossByCode = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var neededByCode = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase);

        foreach (var requirement in requirements)
        {
            grossByCode[requirement.ComponentCode] =
                grossByCode.GetValueOrDefault(requirement.ComponentCode) + requirement.Qty;

            if (!neededByCode.TryGetValue(requirement.ComponentCode, out var existing)
                || requirement.DueDate < existing)
            {
                neededByCode[requirement.ComponentCode] = requirement.DueDate;
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

    /// 把規劃期間內的工單需求展開成「一筆一筆帶日期的原料需求」。
    ///
    /// 抽出來的理由：不分期與分期兩種試算差別只在「怎麼把這些需求收斂」——
    /// 前者收斂成一個最早日期，後者落進週桶。展開規則（已發料排除、剩餘產量、
    /// BOM 缺漏跳過）只能有一份，兩邊各寫一次的話，改一處就會漏一處。
    private async Task<List<MaterialRequirement>> CollectRequirementsAsync(
        DateOnly horizonEnd, CancellationToken ct)
    {
        // 查詢起點刻意用 MinValue 而不是今天：已逾交期但尚未結案的工單仍然要料，
        // 而且是最急的需求。以今天為起點會把它們整批漏掉，導致 MRP 少買。
        var workOrders = await workOrderRepository.GetOpenWorkOrdersByDueDateAsync(
            DateOnly.MinValue, horizonEnd, ct);

        var requirements = new List<MaterialRequirement>();

        foreach (var wo in workOrders)
        {
            if (wo.MaterialsFullyIssued)
            {
                // 料已經發到現場、帳上庫存也已經扣過了。再算一次毛需求就是重複計算：
                // 這張工單要的料不會再從倉庫出去第二次。
                continue;
            }

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
                requirements.Add(new MaterialRequirement(
                    leaf.ComponentCode, wo.DueDate, leaf.RequiredPerFinishedUnit * remainingQty));
            }
        }

        return requirements;
    }

    /// 時間分期試算（簡化版 time-phased MRP）。
    ///
    /// 不分期的版本只回答「總共缺多少」，回答不了「什麼時候開始缺」——
    /// 而需求分散在不同交期時，這兩個答案可以差很多：總量看起來夠，
    /// 卻在第三週就先見底，後面的入庫再多也來不及。
    ///
    /// 刻意**不做** lot-sizing（EOQ、Wagner-Whitin 那一類批量最佳化）：
    /// 這裡要展示的是「時間分期怎麼用資料結構表達」，不是重寫一套供應鏈最佳化引擎。
    /// 建議採購量仍由不分期那條路徑的最小訂購量／訂購倍量規則產生。
    ///
    /// 桶以「今天起算每 7 天」切，不對齊日曆週：對齊的話第一桶會是一個長度不定的殘週，
    /// 「第一週就缺料」這種結論會隨著今天是星期幾而變。
    public async Task<TimePhasedAnalysisResult> RunTimePhasedAnalysisAsync(
        int? weeks = null, string? itemCode = null, CancellationToken ct = default)
    {
        var weekCount = weeks ?? DefaultTimePhasedWeeks;
        if (weekCount is <= 0 or > MaxTimePhasedWeeks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(weeks), weeks, $"分期週數必須介於 1 到 {MaxTimePhasedWeeks} 週");
        }

        var start = clock.Today;
        var end = start.AddDays(weekCount * 7 - 1);

        var requirements = await CollectRequirementsAsync(end, ct);

        if (itemCode is not null)
        {
            requirements = [.. requirements.Where(
                r => string.Equals(r.ComponentCode, itemCode, StringComparison.OrdinalIgnoreCase))];
        }

        if (requirements.Count == 0)
        {
            return new TimePhasedAnalysisResult(CalculationBasis.Available, start, end, weekCount, []);
        }

        var codes = requirements.Select(r => r.ComponentCode)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var balances = await inventoryRepository.GetBalancesAsync(codes, ct);
        var availableByCode = balances.ToDictionary(
            b => b.ItemCode, b => b.AvailableQty, StringComparer.OrdinalIgnoreCase);

        var items = await itemRepository.GetByCodesAsync(codes, ct);
        var nameByCode = items.ToDictionary(i => i.ItemCode, i => i.ItemName, StringComparer.OrdinalIgnoreCase);

        var openPurchaseOrders = await purchaseOrderRepository.GetOpenAsync(ct: ct);
        var receiptsByCode = openPurchaseOrders
            .GroupBy(p => p.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var requirementsByCode = requirements
            .GroupBy(r => r.ComponentCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var results = new List<TimePhasedItem>();

        foreach (var code in codes.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            var opening = availableByCode.GetValueOrDefault(code, 0m);
            var buckets = new List<MaterialTimeBucket>(weekCount);

            var running = opening;
            int? firstShortageWeek = null;
            DateOnly? firstShortageDate = null;

            for (var week = 1; week <= weekCount; week++)
            {
                var weekStart = start.AddDays((week - 1) * 7);
                var weekEnd = weekStart.AddDays(6);

                // 第一桶同時承接「已經過期」的需求與到貨：逾期未結案的工單現在就要料，
                // 早該到卻還沒到的採購單也只能算在最近的這一桶。
                var isFirst = week == 1;

                var requirementQty = requirementsByCode[code]
                    .Where(r => InBucket(r.DueDate, weekStart, weekEnd, isFirst))
                    .Sum(r => r.Qty);

                var receiptQty = receiptsByCode.GetValueOrDefault(code, [])
                    .Where(p => InBucket(p.ExpectedArrivalDate, weekStart, weekEnd, isFirst))
                    .Sum(p => p.InTransitQty);

                running = running + receiptQty - requirementQty;

                buckets.Add(new MaterialTimeBucket(
                    week, weekStart, weekEnd, receiptQty, requirementQty, running));

                if (running < 0 && firstShortageWeek is null)
                {
                    firstShortageWeek = week;
                    firstShortageDate = weekStart;
                }
            }

            results.Add(new TimePhasedItem(
                code, nameByCode.GetValueOrDefault(code, code),
                opening, buckets, firstShortageWeek, firstShortageDate));
        }

        return new TimePhasedAnalysisResult(CalculationBasis.Available, start, end, weekCount, results);
    }

    /// 第一個桶要往前吃掉所有更早的日期，否則逾期的需求與到貨會整批落在期間外被忽略
    private static bool InBucket(DateOnly date, DateOnly weekStart, DateOnly weekEnd, bool isFirstBucket)
        => date <= weekEnd && (isFirstBucket || date >= weekStart);

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
