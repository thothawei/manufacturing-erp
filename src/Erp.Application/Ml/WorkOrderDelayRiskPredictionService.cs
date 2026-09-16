using Erp.Application.Abstractions;
using Erp.Application.Bom;
using Erp.Application.Common;
using Erp.Application.Production;

namespace Erp.Application.Ml;

/// 單張工單的兩種風險判斷並陳。
///
/// 兩邊都保留、而且明講差異，是這個模組的重點 ——
/// 規則式可解釋、上線不需要任何歷史資料；模型式抓得到規則沒寫到的組合關聯，
/// 但需要歷史資料、而且說不出「為什麼是 0.62」。
/// 只留一個都會是錯的答案：現場要的是「為什麼」，管理要的是「哪幾張最該先看」。
public sealed record WorkOrderDelayRiskComparison(
    string WorkOrderNo,
    string ItemCode,
    DateOnly DueDate,

    /// 規則式判斷（逾期 + 缺料兩種來源）。沒有被判為風險時是 null
    int? RuleBasedDelayDays,
    string? RuleBasedReason,

    /// 模型預測的延遲機率。模型不可用時是 null
    double? PredictedDelayProbability,

    /// 機率是否超過訓練時挑出來的決策閾值
    bool? ExceedsThreshold,

    WorkOrderDelayFeatures? Features,

    /// 落在訓練資料分布之外的特徵。空清單代表這張工單的每個特徵
    /// 模型在訓練時都見過類似的值
    IReadOnlyList<OutOfDistributionFeature> OutOfDistributionFeatures,

    string ModelDescription,
    string Note);

/// 工單延遲風險預測：規則式與模型式並存比較。
public sealed class WorkOrderDelayRiskPredictionService(
    IWorkOrderRepository workOrderRepository,
    IItemRepository itemRepository,
    BomExplosionService bomExplosionService,
    WorkOrderRiskService workOrderRiskService,
    IDelayRiskModel model,
    IClock clock)
{
    /// 產線一週的基準工單數。除以它是為了讓「負載」變成一個沒有單位的比值，
    /// 模型換到別條產線時不必重訓。這個值是設定，不是量測出來的 ——
    /// 真實系統該從產能主檔取。
    private const double WeeklyCapacityWorkOrders = 8.0;

    public async Task<WorkOrderDelayRiskComparison> CompareAsync(
        string workOrderNo, CancellationToken ct = default)
    {
        var workOrder = await workOrderRepository.GetByNoAsync(workOrderNo, ct)
            ?? throw new EntityNotFoundException("工單", workOrderNo);

        if (!workOrder.IsOpen)
        {
            throw new InvalidOperationException(
                $"工單 {workOrderNo} 已經是 {workOrder.Status} 狀態，不需要預測延遲風險");
        }

        // 規則式那一側直接用既有服務，不重寫一份判斷邏輯
        var ruleBased = (await workOrderRiskService.GetAtRiskWorkOrdersAsync(
                workOrder.DueDate, workOrder.DueDate, ct: ct))
            .FirstOrDefault(r => r.WorkOrderNo == workOrderNo);

        var features = await BuildFeaturesAsync(workOrder, ct);

        double? probability = null;
        bool? exceeds = null;
        IReadOnlyList<OutOfDistributionFeature> outOfDistribution = [];

        if (model.IsAvailable && features is not null)
        {
            probability = model.PredictDelayProbability(features);
            exceeds = probability >= model.DecisionThreshold;
            outOfDistribution = model.FindOutOfDistributionFeatures(features);
        }

        return new WorkOrderDelayRiskComparison(
            workOrder.WorkOrderNo,
            workOrder.ItemCode,
            workOrder.DueDate,
            ruleBased?.DelayDays,
            ruleBased?.RiskReason,
            probability,
            exceeds,
            features,
            outOfDistribution,
            model.Description,
            BuildNote(ruleBased is not null, probability, features is not null, outOfDistribution));
    }

    /// 從真實工單資料組出模型要的特徵。
    ///
    /// **這個方法與 HistoricalWorkOrderGenerator 必須算同一件事**，
    /// 否則就是 training/serving skew。特徵的定義集中在 WorkOrderDelayFeatures 上，
    /// 這裡負責的是「去哪裡取值」。
    private async Task<WorkOrderDelayFeatures?> BuildFeaturesAsync(
        Domain.Production.WorkOrder workOrder, CancellationToken ct)
    {
        var steps = await workOrderRepository.GetRoutingStepsAsync(workOrder.WorkOrderNo, ct);
        var remainingQty = WorkOrderRemainingQty.Calculate(workOrder, steps);

        if (remainingQty <= 0)
        {
            return null;   // 產量已做完，沒有東西可以預測
        }

        MaterialSufficiencyResult sufficiency;
        try
        {
            sufficiency = await bomExplosionService.CalculateMaxBuildableAsync(
                workOrder.ItemCode, remainingQty, ct);
        }
        catch (Exception ex) when (ex is EntityNotFoundException or InvalidOperationException)
        {
            return null;   // 沒有 BOM 的工單算不出物料特徵
        }

        // 齊套率：缺料件裡最嚴重的那個決定整張工單做不做得出來，
        // 所以取「覆蓋率最低的那一件」而不是平均 —— 平均會被齊全的料稀釋掉。
        var readiness = 1.0;
        foreach (var shortage in sufficiency.ShortageComponents)
        {
            var required = shortage.RequiredPerFinishedUnit * remainingQty;
            if (required <= 0)
            {
                continue;
            }

            var available = Math.Max(0m, required - shortage.ShortfallQty);
            readiness = Math.Min(readiness, (double)(available / required));
        }

        var maxLeadTime = 0;
        if (sufficiency.ShortageComponents.Count > 0)
        {
            var supplyInfos = await itemRepository.GetSupplyInfosAsync(
                [.. sufficiency.ShortageComponents.Select(c => c.ComponentCode)], ct);
            maxLeadTime = supplyInfos.Count == 0 ? 0 : supplyInfos.Max(s => s.LeadTimeDays);
        }

        var progress = workOrder.PlannedQty > 0
            ? (double)(1 - (remainingQty / workOrder.PlannedQty))
            : 0;

        // 一次撈完所有未結案工單，品項逾期比例與當週負載都從這份資料算
        var openWorkOrders = await workOrderRepository.GetOpenWorkOrdersByDueDateAsync(
            DateOnly.MinValue, DateOnly.MaxValue, ct);

        var sameItem = openWorkOrders
            .Where(w => string.Equals(w.ItemCode, workOrder.ItemCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var overdueRate = sameItem.Count == 0
            ? 0
            : sameItem.Count(w => w.DueDate < clock.Today) / (double)sameItem.Count;

        var weekStart = workOrder.DueDate.AddDays(-3);
        var weekEnd = workOrder.DueDate.AddDays(3);
        var load = openWorkOrders.Count(w => w.DueDate >= weekStart && w.DueDate <= weekEnd)
                   / WeeklyCapacityWorkOrders;

        return new WorkOrderDelayFeatures(
            MaterialReadiness: Math.Round(readiness, 4),
            DaysUntilDue: workOrder.DueDate.DayNumber - clock.Today.DayNumber,
            MaxLeadTimeDays: maxLeadTime,
            ProgressRatio: Math.Round(Math.Clamp(progress, 0, 1), 4),
            ItemOverdueRate: Math.Round(overdueRate, 4),
            WeeklyLoadRatio: Math.Round(load, 4),
            // 一律是 BOM 葉節點數。
            //
            // 第一版寫成「有缺料時用缺料件數、沒缺料時用葉節點數」，端到端實跑才抓到：
            // 同一個特徵在兩種情況下代表不同的東西，而訓練資料裡它只有一個意思
            // （BOM 葉節點數）。這就是 training/serving skew 的真面目 ——
            // 不會報錯，離線評估看不出來，線上算出來的機率卻是拿錯尺量的。
            BomComponentCount: await CountBomLeavesAsync(workOrder.ItemCode, ct));
    }

    private async Task<int> CountBomLeavesAsync(string itemCode, CancellationToken ct)
    {
        try
        {
            return (await bomExplosionService.ExplodeToLeavesAsync(itemCode, ct)).Count;
        }
        catch (Exception ex) when (ex is EntityNotFoundException or InvalidOperationException)
        {
            return 0;
        }
    }

    private string BuildNote(
        bool ruleFlagged,
        double? probability,
        bool hasFeatures,
        IReadOnlyList<OutOfDistributionFeature> outOfDistribution)
    {
        if (!hasFeatures)
        {
            return "這張工單的剩餘產量為 0 或沒有 BOM，算不出模型需要的特徵，只能用規則式判斷。";
        }

        if (probability is null)
        {
            return $"模型不可用（{model.Description}），只有規則式判斷可用。";
        }

        var note =
            "規則式與模型式是兩種不同的判斷：規則式看得到「為什麼」（逾期幾天、缺哪個料），"
            + "模型只給一個機率、說不出理由，但它抓得到規則沒寫進去的組合關聯。";

        if (ruleFlagged)
        {
            note += "這張工單兩邊都認為有風險。";
        }
        else
        {
            note += "規則式沒有把它列為風險 —— 模型的機率可以當作「要不要多看一眼」的排序依據，"
                  + "不能當作它一定會延遲的結論。";
        }

        note += model.IsCalibrated
            ? "模型輸出的機率經過校準，可以當成發生率解讀。"
            : "機率沒有經過校準：它適合拿來排序（0.68 比 0.42 更值得先看），"
              + "但不保證「0.68 就是六成八會延遲」。";

        // 分布外的輸入是這類模型最危險的失敗方式：它照樣給一個機率，
        // 外觀與分布內的完全一樣，只是可信度低而沒有任何徵兆。講出來，不要讓它靜靜通過。
        if (outOfDistribution.Count > 0)
        {
            var detail = string.Join("、", outOfDistribution.Select(
                f => $"{f.Feature}={f.Value:0.####}（訓練範圍 {f.TrainingLow:0.####}~{f.TrainingHigh:0.####}）"));

            note += $"**注意：這張工單有 {outOfDistribution.Count} 個特徵落在訓練資料的分布之外**（{detail}）。"
                  + "模型沒見過這種輸入，仍然會給出機率，但那個數字的可信度比分布內低，"
                  + "轉述時必須一併說明。";
        }

        return note + "模型是用模擬資料訓練的，不是真實產線資料。";
    }
}
