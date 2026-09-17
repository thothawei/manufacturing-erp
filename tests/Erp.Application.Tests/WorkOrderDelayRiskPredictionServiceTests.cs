using Erp.Application.Bom;
using Erp.Application.Common;
using Erp.Application.Ml;
using Erp.Application.Production;
using Erp.Application.Tests.Fakes;
using Erp.Domain.Inventory;
using Erp.Domain.Production;

namespace Erp.Application.Tests;

/// 線上特徵計算。
///
/// 這組驗的是「特徵有沒有從真實工單資料正確算出來」——
/// 它與 HistoricalWorkOrderGenerator 必須算同一件事，否則就是 training/serving skew：
/// 兩邊對「齊套率」的定義差一點點，模型上線後的表現就會與離線評估對不上，而且很難查。
public class WorkOrderDelayRiskPredictionServiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 10);

    private FakeDelayRiskModel _model = null!;
    private FakeRecentPredictionLog<WorkOrderDelayFeatures> _recentPredictionLog = null!;

    private WorkOrderDelayRiskPredictionService CreateService(
        IEnumerable<WorkOrder> workOrders,
        IEnumerable<RoutingStep> steps,
        IEnumerable<InventoryBalance> balances,
        bool modelAvailable = true,
        IReadOnlyList<OutOfDistributionFeature>? outOfDistribution = null)
    {
        var itemRepo = new InMemoryItemRepository(TestData.Items, TestData.SupplyInfos);
        var inventoryRepo = new InMemoryInventoryRepository(balances);
        var bomService = new BomExplosionService(itemRepo, new InMemoryBomRepository(TestData.Bom), inventoryRepo);
        var workOrderRepo = new InMemoryWorkOrderRepository(workOrders, steps);
        var clock = new FakeClock(Today);

        _model = new FakeDelayRiskModel(modelAvailable, outOfDistribution: outOfDistribution);
        _recentPredictionLog = new FakeRecentPredictionLog<WorkOrderDelayFeatures>();

        return new WorkOrderDelayRiskPredictionService(
            workOrderRepo, itemRepo, bomService,
            new WorkOrderRiskService(workOrderRepo, itemRepo, bomService, clock),
            _model, _recentPredictionLog, clock);
    }

    private static WorkOrder Wo(string no, DateOnly due, decimal qty = 100m,
        WorkOrderStatus status = WorkOrderStatus.InProgress) =>
        new() { WorkOrderNo = no, ItemCode = "TV-100", PlannedQty = qty, DueDate = due, Status = status };

    [Fact]
    public async Task 齊套率取覆蓋率最低的那一件_不是平均()
    {
        // 100 台需要 200 片面板、1200 支螺絲。面板只有 80 片（覆蓋 40%），螺絲充足。
        // 取平均的話會是 70%，那會把「面板根本不夠」這件事稀釋掉
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13))],
            [],
            [TestData.Balance("PANEL-01", 80m), TestData.Balance("SCREW-05", 100_000m)]);

        await service.CompareAsync("WO-01");

        Assert.Equal(0.4, _model.LastFeatures!.MaterialReadiness, precision: 4);
    }

    [Fact]
    public async Task 料全齊時齊套率是一()
    {
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13))],
            [],
            [TestData.Balance("PANEL-01", 100_000m), TestData.Balance("SCREW-05", 100_000m)]);

        await service.CompareAsync("WO-01");

        Assert.Equal(1.0, _model.LastFeatures!.MaterialReadiness);
        Assert.Equal(0, _model.LastFeatures.MaxLeadTimeDays);   // 沒缺料就沒有補料前置期
    }

    [Fact]
    public async Task 已逾期的工單剩餘天數是負數_不夾成零()
    {
        // 逾期程度本身就是訊號，夾成 0 等於把「逾期三天」和「今天到期」當成同一件事
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 7))],
            [],
            [TestData.Balance("PANEL-01", 100_000m), TestData.Balance("SCREW-05", 100_000m)]);

        await service.CompareAsync("WO-01");

        Assert.Equal(-3, _model.LastFeatures!.DaysUntilDue);
    }

    [Fact]
    public async Task 進度以途程最後一站計算()
    {
        var steps = new RoutingStep[]
        {
            new() { WorkOrderNo = "WO-01", StepNo = 10, OperationName = "組裝", PlannedQty = 100m,
                    CompletedQty = 60m, Status = RoutingStepStatus.InProgress },
        };

        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 20))],
            steps,
            [TestData.Balance("PANEL-01", 100_000m), TestData.Balance("SCREW-05", 100_000m)]);

        await service.CompareAsync("WO-01");

        Assert.Equal(0.6, _model.LastFeatures!.ProgressRatio, precision: 4);
    }

    [Fact]
    public async Task 品項逾期比例只看同品項的未結案工單()
    {
        var service = CreateService(
        [
            Wo("WO-01", new DateOnly(2026, 9, 20)),                        // 未逾期
            Wo("WO-02", new DateOnly(2026, 9, 1)),                         // 逾期
            new() { WorkOrderNo = "WO-03", ItemCode = "MON-200", PlannedQty = 10m,
                    DueDate = new DateOnly(2026, 9, 1), Status = WorkOrderStatus.InProgress },
        ], [], [TestData.Balance("PANEL-01", 100_000m), TestData.Balance("SCREW-05", 100_000m)]);

        await service.CompareAsync("WO-01");

        // TV-100 有兩張未結案、其中一張逾期 → 0.5。MON-200 那張不算進來
        Assert.Equal(0.5, _model.LastFeatures!.ItemOverdueRate, precision: 4);
    }

    [Fact]
    public async Task BOM件數不論有沒有缺料都是同一個意思()
    {
        // 這條釘住一個端到端實跑才抓到的 skew：第一版寫成「有缺料時用缺料件數、
        // 沒缺料時用葉節點數」，同一個特徵在兩種情況下代表不同的東西，
        // 而訓練資料裡它只有一個意思。不會報錯，離線評估也看不出來。
        var shortage = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13))],
            [],
            [TestData.Balance("PANEL-01", 80m), TestData.Balance("SCREW-05", 100_000m)]);

        await shortage.CompareAsync("WO-01");
        var withShortage = _model.LastFeatures!.BomComponentCount;

        var plenty = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13))],
            [],
            [TestData.Balance("PANEL-01", 100_000m), TestData.Balance("SCREW-05", 100_000m)]);

        await plenty.CompareAsync("WO-01");
        var withoutShortage = _model.LastFeatures!.BomComponentCount;

        Assert.Equal(withoutShortage, withShortage);
        Assert.True(withShortage >= 2, $"TV-100 展開後不只 {withShortage} 個葉節點原料");
    }

    [Fact]
    public async Task 規則式與模型式並存_兩邊的結果都在回傳裡()
    {
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13))],
            [],
            [TestData.Balance("PANEL-01", 80m), TestData.Balance("SCREW-05", 100_000m)]);

        var result = await service.CompareAsync("WO-01");

        Assert.Equal(2, result.RuleBasedDelayDays);                 // 規則式：前置期 5 天、剩 3 天
        Assert.Contains("PANEL-01", result.RuleBasedReason!);
        Assert.Equal(0.42, result.PredictedDelayProbability);       // 模型式
        Assert.True(result.ExceedsThreshold);                       // 0.42 > 0.26
        Assert.Contains("模擬資料", result.Note);
    }

    [Fact]
    public async Task 模型不可用時規則式照常可用_機率是null而不是預設值()
    {
        // 回一個看起來像機率的預設值是最糟的失敗方式
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13))],
            [],
            [TestData.Balance("PANEL-01", 80m), TestData.Balance("SCREW-05", 100_000m)],
            modelAvailable: false);

        var result = await service.CompareAsync("WO-01");

        Assert.Null(result.PredictedDelayProbability);
        Assert.Null(result.ExceedsThreshold);
        Assert.Equal(2, result.RuleBasedDelayDays);
        Assert.Contains("模型不可用", result.Note);
    }

    /// 漂移偵測（S5）要看的是「線上實際收到的輸入」，跟模型當不當下可用是兩件事——
    /// 這條釘的正是這個：模型不可用時特徵仍然要被記錄下來，不然模型修好那一刻，
    /// 之前累積的輸入分布資訊已經永遠遺失了。
    [Fact]
    public async Task 模型不可用時特徵仍然會被記錄下來供漂移偵測用()
    {
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13))],
            [],
            [TestData.Balance("PANEL-01", 80m), TestData.Balance("SCREW-05", 100_000m)],
            modelAvailable: false);

        await service.CompareAsync("WO-01");

        Assert.Single(_recentPredictionLog.Recorded);
    }

    [Fact]
    public async Task 每次呼叫都會把特徵記錄進最近推論記錄()
    {
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13)), Wo("WO-02", new DateOnly(2026, 9, 20))],
            [],
            [TestData.Balance("PANEL-01", 80m), TestData.Balance("SCREW-05", 100_000m)]);

        await service.CompareAsync("WO-01");
        await service.CompareAsync("WO-02");

        Assert.Equal(2, _recentPredictionLog.Recorded.Count);
    }

    [Fact]
    public async Task 已結案的工單不預測()
    {
        var service = CreateService(
            [Wo("WO-DONE", new DateOnly(2026, 9, 13), status: WorkOrderStatus.Completed)],
            [], []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompareAsync("WO-DONE"));
    }

    [Fact]
    public async Task 查無工單時擲出查無資料()
    {
        var service = CreateService([], [], []);

        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.CompareAsync("WO-NOPE"));
    }

    [Fact]
    public async Task 剩餘產量為零時算不出特徵_只回規則式()
    {
        var steps = new RoutingStep[]
        {
            new() { WorkOrderNo = "WO-01", StepNo = 10, OperationName = "組裝", PlannedQty = 100m,
                    CompletedQty = 100m, Status = RoutingStepStatus.Completed },
        };

        var service = CreateService([Wo("WO-01", new DateOnly(2026, 9, 13))], steps, []);

        var result = await service.CompareAsync("WO-01");

        Assert.Null(result.Features);
        Assert.Null(result.PredictedDelayProbability);
        Assert.Contains("算不出模型需要的特徵", result.Note);
    }

    [Fact]
    public async Task 分布外的特徵要一路傳到回應與說明裡()
    {
        // 模型對沒見過的輸入照樣給一個機率，數字外觀與分布內的一模一樣。
        // 這條釘的是「那件事不會靜靜通過」—— 模型標出來了，服務就得講出來
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13))],
            [], [TestData.Balance("PANEL-01", 100_000m), TestData.Balance("SCREW-05", 100_000m)],
            outOfDistribution: [new OutOfDistributionFeature("weekly_load_ratio", 0.125, 0.5088, 2.0896)]);

        var result = await service.CompareAsync("WO-01");

        var feature = Assert.Single(result.OutOfDistributionFeatures);
        Assert.Equal("weekly_load_ratio", feature.Feature);
        Assert.Contains("分布之外", result.Note);
        Assert.Contains("weekly_load_ratio", result.Note);
        Assert.Contains("可信度", result.Note);
    }

    [Fact]
    public async Task 分布內時不要無中生有地警告()
    {
        // 警告只在真的有事時出現，才有人會看
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13))],
            [], [TestData.Balance("PANEL-01", 100_000m), TestData.Balance("SCREW-05", 100_000m)]);

        var result = await service.CompareAsync("WO-01");

        Assert.Empty(result.OutOfDistributionFeatures);
        Assert.DoesNotContain("分布之外", result.Note);
    }

    [Fact]
    public async Task 機率沒校準時要講明它只適合排序()
    {
        // 「0.68」與「六成八會延遲」是兩件事，混為一談的成本是錯誤的決策。
        // 假模型的 IsCalibrated 是 false，說明文字就必須講清楚這一點
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13))],
            [], [TestData.Balance("PANEL-01", 100_000m), TestData.Balance("SCREW-05", 100_000m)]);

        var result = await service.CompareAsync("WO-01");

        Assert.Contains("沒有經過校準", result.Note);
        Assert.Contains("排序", result.Note);
    }
}
