using Erp.Application.Bom;
using Erp.Application.Common;
using Erp.Application.Mrp;
using Erp.Application.Tests.Fakes;
using Erp.Domain.Inventory;
using Erp.Domain.Production;
using Erp.Domain.Purchasing;

namespace Erp.Application.Tests;

public class MrpCalculationServiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 10);

    private static MrpCalculationService CreateService(
        IEnumerable<WorkOrder> workOrders,
        IEnumerable<InventoryBalance> balances,
        IEnumerable<PurchaseOrder>? purchaseOrders = null,
        IEnumerable<RoutingStep>? steps = null)
    {
        var itemRepo = new InMemoryItemRepository(TestData.Items, TestData.SupplyInfos);
        var inventoryRepo = new InMemoryInventoryRepository(balances);
        var bomService = new BomExplosionService(itemRepo, new InMemoryBomRepository(TestData.Bom), inventoryRepo);

        return new MrpCalculationService(
            new InMemoryWorkOrderRepository(workOrders, steps),
            itemRepo,
            inventoryRepo,
            new InMemoryPurchaseOrderRepository(purchaseOrders ?? []),
            bomService,
            new FakeClock(Today));
    }

    private static WorkOrder Wo(string no, DateOnly due, decimal qty = 100m) =>
        new()
        {
            WorkOrderNo = no,
            ItemCode = "TV-100",
            PlannedQty = qty,
            DueDate = due,
            Status = WorkOrderStatus.Released
        };

    /// 三張各 50 台的 TV 工單，交期分別落在第 1、2、4 週，每張吃掉 100 片面板。
    /// 期初可用 200 片 —— 總量看起來只缺 100 片，但真正的問題是「第 4 週就見底」。
    private static MrpCalculationService CreateTimePhasedScenario(
        IEnumerable<PurchaseOrder>? purchaseOrders = null)
        => CreateService(
            [
                Wo("WO-W1", new DateOnly(2026, 9, 13), qty: 50m),
                Wo("WO-W2", new DateOnly(2026, 9, 20), qty: 50m),
                Wo("WO-W4", new DateOnly(2026, 10, 2), qty: 50m),
            ],
            [TestData.Balance("PANEL-01", 200m), TestData.Balance("SCREW-05", 100_000m)],
            purchaseOrders);

    [Fact]
    public async Task 時間分期_總量看起來夠但第四週見底()
    {
        var result = await CreateTimePhasedScenario().RunTimePhasedAnalysisAsync(itemCode: "PANEL-01");

        var panel = Assert.Single(result.Items);
        Assert.Equal(200m, panel.OpeningAvailableQty);

        // 期末水位：200 → 100 → 0 → 0 → -100
        Assert.Equal(100m, panel.Buckets[0].ProjectedOnHandQty);
        Assert.Equal(0m, panel.Buckets[1].ProjectedOnHandQty);
        Assert.Equal(0m, panel.Buckets[2].ProjectedOnHandQty);
        Assert.Equal(-100m, panel.Buckets[3].ProjectedOnHandQty);

        Assert.Equal(4, panel.FirstShortageWeek);
        Assert.Equal(new DateOnly(2026, 10, 1), panel.FirstShortageDate);
    }

    [Fact]
    public async Task 時間分期_回答的是不分期版本回答不了的問題()
    {
        // 同一組資料，兩條路徑的總量一致 —— 差別在於分期版本說得出「什麼時候」。
        var service = CreateTimePhasedScenario();

        var flat = await service.RunShortageAnalysisAsync(planningHorizonDays: 60, itemCode: "PANEL-01");
        var phased = await service.RunTimePhasedAnalysisAsync(itemCode: "PANEL-01");

        var flatPanel = Assert.Single(flat.ShortageItems);
        Assert.Equal(300m, flatPanel.GrossRequirementQty);
        Assert.Equal(100m, flatPanel.NetShortageQty);

        var phasedPanel = Assert.Single(phased.Items);
        Assert.Equal(300m, phasedPanel.Buckets.Sum(b => b.RequirementQty));
        Assert.Equal(-100m, phasedPanel.Buckets[^1].ProjectedOnHandQty);
        Assert.Equal(4, phasedPanel.FirstShortageWeek);
    }

    [Fact]
    public async Task 時間分期_及時到貨的採購單會把水位補回來()
    {
        var service = CreateTimePhasedScenario(
        [
            new PurchaseOrder
            {
                PoNo = "PO-W3", SupplierCode = "SUP-008", ItemCode = "PANEL-01",
                OrderedQty = 100m, ReceivedQty = 0m,
                ExpectedArrivalDate = new DateOnly(2026, 9, 28),   // 第 3 週
                Status = PurchaseOrderStatus.Open
            }
        ]);

        var panel = Assert.Single((await service.RunTimePhasedAnalysisAsync(itemCode: "PANEL-01")).Items);

        Assert.Equal(100m, panel.Buckets[2].ScheduledReceiptQty);
        Assert.Equal(100m, panel.Buckets[2].ProjectedOnHandQty);
        Assert.Equal(0m, panel.Buckets[3].ProjectedOnHandQty);
        Assert.Null(panel.FirstShortageWeek);      // 補得上就不算缺料
    }

    [Fact]
    public async Task 時間分期_晚到的採購單救不了已經發生的缺口()
    {
        // 同樣 100 片，晚三週到 —— 總量一樣，結論完全不同。
        // 這正是不分期版本看不出來的那件事。
        var service = CreateTimePhasedScenario(
        [
            new PurchaseOrder
            {
                PoNo = "PO-W7", SupplierCode = "SUP-008", ItemCode = "PANEL-01",
                OrderedQty = 100m, ReceivedQty = 0m,
                ExpectedArrivalDate = new DateOnly(2026, 10, 26),  // 第 7 週
                Status = PurchaseOrderStatus.Open
            }
        ]);

        var panel = Assert.Single((await service.RunTimePhasedAnalysisAsync(itemCode: "PANEL-01")).Items);

        Assert.Equal(4, panel.FirstShortageWeek);
        Assert.Equal(0m, panel.Buckets[6].ProjectedOnHandQty);   // 第 7 週才補回來，缺口已經發生過
    }

    [Fact]
    public async Task 時間分期_逾期的需求與逾期未到的採購單都落在第一桶()
    {
        var service = CreateService(
            [Wo("WO-OVERDUE", new DateOnly(2026, 8, 20), qty: 50m)],   // 三週前就該交
            [TestData.Balance("PANEL-01", 20m), TestData.Balance("SCREW-05", 100_000m)],
            [
                new PurchaseOrder
                {
                    PoNo = "PO-LATE", SupplierCode = "SUP-008", ItemCode = "PANEL-01",
                    OrderedQty = 30m, ReceivedQty = 0m,
                    ExpectedArrivalDate = new DateOnly(2026, 9, 1),    // 早該到卻還沒到
                    Status = PurchaseOrderStatus.Open
                }
            ]);

        var panel = Assert.Single((await service.RunTimePhasedAnalysisAsync(itemCode: "PANEL-01")).Items);

        Assert.Equal(100m, panel.Buckets[0].RequirementQty);        // 50 台 ×2
        Assert.Equal(30m, panel.Buckets[0].ScheduledReceiptQty);
        Assert.Equal(-50m, panel.Buckets[0].ProjectedOnHandQty);    // 20 + 30 - 100
        Assert.Equal(1, panel.FirstShortageWeek);
    }

    [Fact]
    public async Task 時間分期_庫存充足時沒有缺料週次()
    {
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13), qty: 50m)],
            [TestData.Balance("PANEL-01", 100_000m), TestData.Balance("SCREW-05", 100_000m)]);

        var panel = Assert.Single((await service.RunTimePhasedAnalysisAsync(itemCode: "PANEL-01")).Items);

        Assert.Null(panel.FirstShortageWeek);
        Assert.Null(panel.FirstShortageDate);
        Assert.All(panel.Buckets, b => Assert.True(b.ProjectedOnHandQty >= 0));
    }

    [Fact]
    public async Task 時間分期_已全數發料的工單同樣不計入需求()
    {
        var service = CreateService(
            [new WorkOrder
            {
                WorkOrderNo = "WO-ISSUED", ItemCode = "TV-100", PlannedQty = 100m,
                DueDate = new DateOnly(2026, 9, 13), Status = WorkOrderStatus.Released,
                MaterialIssueStatus = MaterialIssueStatuses.FullyIssued
            }],
            [TestData.Balance("PANEL-01", 50m), TestData.Balance("SCREW-05", 100_000m)]);

        var result = await service.RunTimePhasedAnalysisAsync();

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task 時間分期_桶數與區間長度對得上()
    {
        var result = await CreateTimePhasedScenario().RunTimePhasedAnalysisAsync(weeks: 4);

        Assert.Equal(4, result.WeekCount);
        Assert.Equal(new DateOnly(2026, 9, 10), result.HorizonStart);
        Assert.Equal(new DateOnly(2026, 10, 7), result.HorizonEnd);   // 4 週 × 7 天 - 1

        var panel = result.Items.Single(i => i.ItemCode == "PANEL-01");
        Assert.Equal(4, panel.Buckets.Count);
        Assert.Equal(new DateOnly(2026, 9, 10), panel.Buckets[0].WeekStart);
        Assert.Equal(new DateOnly(2026, 9, 16), panel.Buckets[0].WeekEnd);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(53)]
    public async Task 時間分期_週數不合理時擲出參數例外(int weeks)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => CreateTimePhasedScenario().RunTimePhasedAnalysisAsync(weeks));
    }

    [Fact]
    public async Task 已全數發料的工單不再計入毛需求()
    {
        // 同一張工單的料已經發到現場、帳上庫存也扣過了。
        // 再把它的剩餘產量算成毛需求，等於同一份需求被算兩次，缺料量會憑空變大。
        var service = CreateService(
            [new WorkOrder
            {
                WorkOrderNo = "WO-ISSUED", ItemCode = "TV-100", PlannedQty = 100m,
                DueDate = new DateOnly(2026, 9, 13), Status = WorkOrderStatus.Released,
                MaterialIssueStatus = MaterialIssueStatuses.FullyIssued
            }],
            [TestData.Balance("PANEL-01", 50m), TestData.Balance("SCREW-05", 100_000m)]);

        var result = await service.RunShortageAnalysisAsync();

        Assert.Empty(result.ShortageItems);
    }

    [Fact]
    public async Task 部分發料的工單仍然計入毛需求()
    {
        // 只有「已全數發料」代表這張工單不會再來領料。
        // 部分發料還會再領，需求照算 —— 否則會少買。
        var service = CreateService(
            [new WorkOrder
            {
                WorkOrderNo = "WO-PARTIAL", ItemCode = "TV-100", PlannedQty = 100m,
                DueDate = new DateOnly(2026, 9, 13), Status = WorkOrderStatus.Released,
                MaterialIssueStatus = MaterialIssueStatuses.PartiallyIssued
            }],
            [TestData.Balance("PANEL-01", 50m), TestData.Balance("SCREW-05", 100_000m)]);

        var result = await service.RunShortageAnalysisAsync();

        var panel = result.ShortageItems.Single(s => s.ItemCode == "PANEL-01");
        Assert.Equal(200m, panel.GrossRequirementQty);
        Assert.Equal(150m, panel.NetShortageQty);
    }

    [Fact]
    public async Task 淨缺料量等於毛需求扣掉可用庫存與在途量()
    {
        // 100 台 TV-100 需要 200 片面板；可用 50 片、在途 30 片 → 淨缺 120 片
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13))],
            [TestData.Balance("PANEL-01", 50m), TestData.Balance("SCREW-05", 100_000m)],
            [
                new PurchaseOrder
                {
                    PoNo = "PO-001", SupplierCode = "SUP-008", ItemCode = "PANEL-01",
                    OrderedQty = 30m, ReceivedQty = 0m,
                    ExpectedArrivalDate = new DateOnly(2026, 9, 12), Status = PurchaseOrderStatus.Open
                }
            ]);

        var result = await service.RunShortageAnalysisAsync();

        var shortage = Assert.Single(result.ShortageItems);
        Assert.Equal("PANEL-01", shortage.ItemCode);
        Assert.Equal(200m, shortage.GrossRequirementQty);
        Assert.Equal(50m, shortage.AvailableQty);
        Assert.Equal(30m, shortage.InTransitQty);
        Assert.Equal(120m, shortage.NetShortageQty);
        Assert.Equal(CalculationBasis.Available, result.Basis);
    }

    [Fact]
    public async Task 到貨日晚於需求日的採購單不列為供給()
    {
        // 同上，但採購單 9/20 才到、需求日是 9/13 → 在途量不能扣，淨缺變 150
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13))],
            [TestData.Balance("PANEL-01", 50m), TestData.Balance("SCREW-05", 100_000m)],
            [
                new PurchaseOrder
                {
                    PoNo = "PO-002", SupplierCode = "SUP-008", ItemCode = "PANEL-01",
                    OrderedQty = 30m, ReceivedQty = 0m,
                    ExpectedArrivalDate = new DateOnly(2026, 9, 20), Status = PurchaseOrderStatus.Open
                }
            ]);

        var result = await service.RunShortageAnalysisAsync();

        var shortage = Assert.Single(result.ShortageItems);
        Assert.Equal(0m, shortage.InTransitQty);
        Assert.Equal(150m, shortage.NetShortageQty);
    }

    [Fact]
    public async Task 部分入庫的採購單只計算尚未到貨的數量()
    {
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13))],
            [TestData.Balance("PANEL-01", 50m), TestData.Balance("SCREW-05", 100_000m)],
            [
                new PurchaseOrder
                {
                    PoNo = "PO-003", SupplierCode = "SUP-008", ItemCode = "PANEL-01",
                    OrderedQty = 100m, ReceivedQty = 70m,
                    ExpectedArrivalDate = new DateOnly(2026, 9, 12),
                    Status = PurchaseOrderStatus.PartiallyReceived
                }
            ]);

        var shortage = Assert.Single((await service.RunShortageAnalysisAsync()).ShortageItems);
        Assert.Equal(30m, shortage.InTransitQty); // 已入庫的 70 已反映在庫存，不能重複計
        Assert.Equal(120m, shortage.NetShortageQty);
    }

    [Fact]
    public async Task 建議採購量向上湊到訂購倍量()
    {
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13))],
            [TestData.Balance("PANEL-01", 80m), TestData.Balance("SCREW-05", 100_000m)]);

        var shortage = Assert.Single((await service.RunShortageAnalysisAsync()).ShortageItems);
        Assert.Equal(120m, shortage.NetShortageQty);
        Assert.Equal(150m, shortage.SuggestedOrderQty); // 倍量 50 → 無條件進位
        Assert.Equal("SUP-008", shortage.SupplierCode);
        Assert.Equal(5, shortage.LeadTimeDays);
    }

    [Fact]
    public async Task 建議採購量不低於最小訂購量()
    {
        // 只缺螺絲：1 台需要 12 支，可用 0 → 缺 12 支，但最小訂購量 1000
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13), qty: 1m)],
            [TestData.Balance("PANEL-01", 100_000m)]);

        var shortage = Assert.Single((await service.RunShortageAnalysisAsync()).ShortageItems);
        Assert.Equal("SCREW-05", shortage.ItemCode);
        Assert.Equal(12m, shortage.NetShortageQty);
        Assert.Equal(1_000m, shortage.SuggestedOrderQty);
    }

    [Fact]
    public async Task 多張工單的需求會合併_需求日取最早的交期()
    {
        var service = CreateService(
        [
            Wo("WO-A", new DateOnly(2026, 9, 20), qty: 10m),
            Wo("WO-B", new DateOnly(2026, 9, 12), qty: 10m),
        ],
        [TestData.Balance("SCREW-05", 100_000m)]);

        var shortage = Assert.Single((await service.RunShortageAnalysisAsync()).ShortageItems);
        Assert.Equal("PANEL-01", shortage.ItemCode);
        Assert.Equal(40m, shortage.GrossRequirementQty);              // (10 + 10) × 2
        Assert.Equal(new DateOnly(2026, 9, 12), shortage.NeededByDate); // 取較早的那張
    }

    [Fact]
    public async Task 超出規劃期間的工單不納入試算()
    {
        var service = CreateService(
            [Wo("WO-FAR", new DateOnly(2026, 12, 1))],
            []);

        var result = await service.RunShortageAnalysisAsync(planningHorizonDays: 7);

        Assert.Empty(result.ShortageItems);
        Assert.Equal(new DateOnly(2026, 9, 17), result.HorizonEnd);
    }

    [Fact]
    public async Task 指定料號時只回報該料號的缺料()
    {
        var service = CreateService([Wo("WO-01", new DateOnly(2026, 9, 13))], []);

        var all = await service.RunShortageAnalysisAsync();
        var filtered = await service.RunShortageAnalysisAsync(itemCode: "SCREW-05");

        Assert.Equal(2, all.ShortageItems.Count);
        Assert.Equal("SCREW-05", Assert.Single(filtered.ShortageItems).ItemCode);
    }

    [Fact]
    public async Task 庫存足夠時不列入缺料清單()
    {
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13))],
            [TestData.Balance("PANEL-01", 100_000m), TestData.Balance("SCREW-05", 100_000m)]);

        Assert.Empty((await service.RunShortageAnalysisAsync()).ShortageItems);
    }

    [Fact]
    public async Task 已逾交期但未結案的工單仍要納入試算()
    {
        // 交期已過但工單還沒做完，物料照樣要備 —— 以今天為查詢起點會整批漏掉
        var service = CreateService(
            [Wo("WO-OVERDUE", new DateOnly(2026, 9, 5))],
            [TestData.Balance("SCREW-05", 100_000m)]);

        var shortage = Assert.Single((await service.RunShortageAnalysisAsync()).ShortageItems);

        Assert.Equal("PANEL-01", shortage.ItemCode);
        Assert.Equal(200m, shortage.GrossRequirementQty);
        Assert.Equal(new DateOnly(2026, 9, 5), shortage.NeededByDate); // 需求日已過期，代表來不及了
    }

    [Fact]
    public async Task 規劃期間必須大於零天()
    {
        var service = CreateService([], []);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.RunShortageAnalysisAsync(planningHorizonDays: 0));
    }
}
