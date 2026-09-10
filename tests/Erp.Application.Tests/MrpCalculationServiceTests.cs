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
        new() { WorkOrderNo = no, ItemCode = "TV-100", PlannedQty = qty, DueDate = due,
                Status = WorkOrderStatus.Released };

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
