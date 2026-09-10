using Erp.Application.Abstractions;
using Erp.Application.Common;

namespace Erp.Application.Bom;

/// 多階 BOM 展開與可製造量試算。
///
/// 兩個刻意的簡化假設，取捨要說得出理由：
/// 1. 一律展開到葉節點原料，不動用半成品的既有庫存。這會低估可製造量，
///    但不會高估，對「能不能如期交貨」的判斷是安全的方向。
/// 2. 不考慮多張工單對同一原料的競爭，那是 MrpCalculationService 的職責。
public sealed class BomExplosionService(
    IItemRepository itemRepository,
    IBomRepository bomRepository,
    IInventoryRepository inventoryRepository)
{
    /// BOM 巢狀深度上限，超過視為資料異常
    private const int MaxDepth = 20;

    /// 把料件展開成葉節點需求清單，用量已換算成「最終成品一個單位」
    public async Task<IReadOnlyList<ExplodedComponent>> ExplodeToLeavesAsync(
        string itemCode, CancellationToken ct = default)
    {
        var item = await itemRepository.GetByCodeAsync(itemCode, ct)
            ?? throw new EntityNotFoundException("料件", itemCode);

        var accumulated = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        await ExplodeRecursiveAsync(item.ItemCode, 1m, new List<string>(), accumulated, ct);

        if (accumulated.Count == 0)
        {
            return [];
        }

        var names = await itemRepository.GetByCodesAsync([.. accumulated.Keys], ct);
        var nameByCode = names.ToDictionary(i => i.ItemCode, i => i.ItemName, StringComparer.OrdinalIgnoreCase);

        return [.. accumulated
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new ExplodedComponent(
                kv.Key,
                nameByCode.GetValueOrDefault(kv.Key, kv.Key),
                kv.Value))];
    }

    /// 以可用庫存計算最多能製造幾個；給定 plannedQty 時同時判斷是否足夠
    public async Task<MaterialSufficiencyResult> CalculateMaxBuildableAsync(
        string itemCode, decimal? plannedQty = null, CancellationToken ct = default)
    {
        if (plannedQty is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(plannedQty), "計畫產量必須大於 0");
        }

        var item = await itemRepository.GetByCodeAsync(itemCode, ct)
            ?? throw new EntityNotFoundException("料件", itemCode);

        var topLines = await bomRepository.GetLinesByParentAsync(item.ItemCode, ct);
        if (topLines.Count == 0)
        {
            throw new InvalidOperationException($"料件 {item.ItemCode} 沒有 BOM，無法計算可製造量");
        }

        var bomVersion = topLines[0].BomVersion;
        var leaves = await ExplodeToLeavesAsync(item.ItemCode, ct);

        var balances = await inventoryRepository.GetBalancesAsync([.. leaves.Select(l => l.ComponentCode)], ct);
        var availableByCode = balances.ToDictionary(
            b => b.ItemCode, b => b.AvailableQty, StringComparer.OrdinalIgnoreCase);

        // 沒有庫存紀錄的料件視為可用量 0，而不是無限制
        decimal AvailableOf(string code) => availableByCode.GetValueOrDefault(code, 0m);

        var maxBuildable = leaves.Min(l => decimal.Floor(AvailableOf(l.ComponentCode) / l.RequiredPerFinishedUnit));

        // 未指定產量時，以「做 1 個」為門檻，讓連 1 個都做不出來的瓶頸能被列出來
        var targetQty = plannedQty ?? 1m;

        var shortages = leaves
            .Select(l => new
            {
                Leaf = l,
                Available = AvailableOf(l.ComponentCode),
                Required = l.RequiredPerFinishedUnit * targetQty
            })
            .Where(x => x.Available < x.Required)
            .Select(x => new ShortageComponent(
                x.Leaf.ComponentCode,
                x.Leaf.ComponentName,
                x.Leaf.RequiredPerFinishedUnit,
                x.Available,
                x.Required - x.Available))
            .ToList();

        return new MaterialSufficiencyResult(
            item.ItemCode,
            bomVersion,
            CalculationBasis.Available,
            maxBuildable,
            plannedQty.HasValue ? maxBuildable >= plannedQty.Value : null,
            shortages);
    }

    private async Task ExplodeRecursiveAsync(
        string parentCode,
        decimal multiplier,
        List<string> path,
        Dictionary<string, decimal> accumulated,
        CancellationToken ct)
    {
        if (path.Contains(parentCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"BOM 結構有循環參照：{string.Join(" → ", path)} → {parentCode}");
        }

        if (path.Count >= MaxDepth)
        {
            throw new InvalidOperationException($"BOM 巢狀深度超過 {MaxDepth} 階，疑似資料異常：{parentCode}");
        }

        var lines = await bomRepository.GetLinesByParentAsync(parentCode, ct);
        if (lines.Count == 0)
        {
            // 葉節點：累加需求量
            if (path.Count > 0)
            {
                accumulated[parentCode] = accumulated.GetValueOrDefault(parentCode) + multiplier;
            }
            return;
        }

        path.Add(parentCode);
        foreach (var line in lines)
        {
            if (line.QtyPer <= 0)
            {
                throw new InvalidOperationException(
                    $"BOM 用量必須大於 0：{line.ParentItemCode} → {line.ComponentItemCode} = {line.QtyPer}");
            }

            await ExplodeRecursiveAsync(line.ComponentItemCode, multiplier * line.QtyPer, path, accumulated, ct);
        }
        path.RemoveAt(path.Count - 1);
    }
}
